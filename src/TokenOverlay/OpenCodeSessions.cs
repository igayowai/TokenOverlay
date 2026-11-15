using System.Runtime.InteropServices;
using System.Text;

namespace TokenOverlay;

// OpenCode desktop's own numbers (user, 2026-11-15: "오픈코드에서도 토큰오버레이 기능작동하게할수있냐 TPS랑 컨텍스트 등"):
// the chat in front and its turns, from OpenCode's database (~/.local/share/opencode/opencode.db, SQLite in WAL mode),
// opened read-only through Windows' own SQLite. Only ids, times and token numbers come out (json_extract), never a
// message's text; the account and credential tables are never read.
// The chat: the window title less " · OpenCode" is a main session's title (sub-agents run in sessions of their own).
// A turn: from the first user or synthetic message after an idle row up to the next idle row, whose outcome says how
// it ended. Each assistant row is one model request with its times (created, firstToken, completed) and tokens.
internal readonly record struct OpenCodeRow(long Seq, string Type, long At, long? Created, long? FirstToken, long? Completed,
    long Input, long Output, long Reasoning, long CacheRead, long CacheWrite, string? Provider, string? Model, string? Outcome);

internal sealed record OpenCodeRead(TurnStatistics? Turn, IReadOnlyList<TurnStatistics> Recent, long Context, string? Provider, string? Model);

internal static class OpenCodeTurns
{
    private sealed class Turn(long start)
    {
        public readonly long Start = start;
        public long? End;
        public string? Outcome;
        public readonly List<OpenCodeRow> Requests = new();
    }

    public static OpenCodeRead Read(IReadOnlyList<OpenCodeRow> rows)
    {
        var turns = new List<Turn>();
        Turn? open = null;
        OpenCodeRow? last = null;
        foreach (var row in rows)
        {
            switch (row.Type)
            {
                case "user" or "synthetic":
                    if (open is null) turns.Add(open = new Turn(row.At));
                    break;
                case "assistant":
                    // A request with no message before it (a compaction carrying on by itself) opens its own turn.
                    if (open is null) turns.Add(open = new Turn(row.Created ?? row.At));
                    open.Requests.Add(row);
                    if (row.Completed is not null && row.Input + row.CacheRead + row.CacheWrite > 0) last = row;
                    break;
                case "idle":
                    if (open is not null) { open.End = row.At; open.Outcome = row.Outcome; open = null; }
                    break;
            }
        }

        // Like the Claude capsule: while a turn runs, the last finished one stays on screen, marked 직전.
        var running = open is not null;
        var finished = turns.Where(t => t.End is not null && t.Requests.Count > 0).ToList();
        TurnStatistics? shown = running
            ? finished.Count > 0 ? Describe(finished[^1], running: true, shownRunning: false) : Describe(open!, true, true)
            : turns.Count > 0 ? Describe(turns[^1].Requests.Count > 0 || finished.Count == 0 ? turns[^1] : finished[^1], false, false) : null;
        var recent = finished.AsEnumerable().Reverse().Take(TurnHistory.Size).Select(t => Describe(t, false, false)).ToArray();
        var context = last is { } l ? l.Input + l.CacheRead + l.CacheWrite : 0;
        return new OpenCodeRead(shown, recent, context, last?.Provider, last?.Model);
    }

    private static TurnStatistics Describe(Turn t, bool running, bool shownRunning)
    {
        var requests = t.Requests.Where(r => r.Completed is not null).ToArray();
        double? seconds = shownRunning || t.End is not long end || end <= t.Start ? null : (end - t.Start) / 1000.0;
        var problem = !shownRunning && requests.Length > 0 && seconds is null ? "유효한 소요 시간 없음" : null;
        var state = requests.Length == 0 ? "집계 대기" : problem is not null ? "기록 불완전" : shownRunning ? "진행 중"
            : t.Outcome switch { "interrupted" => "중단", "failed" => "오류", _ => "완료" };
        long Sum(Func<OpenCodeRow, long> pick) => requests.Sum(pick);
        var output = Sum(r => r.Output + r.Reasoning);
        var models = new List<string>();
        foreach (var r in requests)
            if (r.Model is { Length: > 0 } m && (models.Count == 0 || models[^1] != m)) { models.Remove(m); models.Add(m); }

        // 모델: each request from its start to its end, the wait for its first piece in. 생성: from its first piece.
        // A request that never answered (no first piece and no output) adds nothing to 생성.
        var timed = requests.Where(r => r.Created is not null).ToArray();
        double? modelSeconds = timed.Length == requests.Length && timed.Length > 0
            ? timed.Sum(r => r.Completed!.Value - r.Created!.Value) / 1000.0 : null;
        var generating = requests.Where(r => r.FirstToken is not null || r.Output + r.Reasoning > 0).ToArray();
        double? pureSeconds = generating.Length > 0 && generating.All(r => r.FirstToken is not null)
            ? generating.Sum(r => r.Completed!.Value - r.FirstToken!.Value) / 1000.0 : null;

        return new TurnStatistics(t.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requests.Length > 0 ? Sum(r => r.Input + r.CacheRead + r.CacheWrite) : null,
            requests.Length > 0 ? output : null,
            requests.Length > 0 ? Sum(r => r.CacheRead) : null,
            seconds, state, running, problem)
        {
            Model = models.Count == 0 ? null : models[^1] + (models.Count > 1 ? "+" : ""),
            // Same mark as Claude's: a request wrote more than half its context to the cache.
            Restored = requests.Any(r => r.CacheWrite >= ClaudeTurnReader.RestoreFloor
                && r.CacheWrite * 2 > r.Input + r.CacheRead + r.CacheWrite),
            ModelSeconds = modelSeconds,
            ModelRequests = requests.Length,
            ModelNote = requests.Length == 0 ? null : modelSeconds is null ? "요청 시작 시각 없음" : null,
            PureSeconds = pureSeconds,
            PureNote = generating.Length == 0 ? null : pureSeconds is null ? "첫 응답 시각 없음" : null
        };
    }
}

internal sealed class OpenCodeSource
{
    internal const string TitleSuffix = " · OpenCode";
    // Keeps OpenCode's route and session versions apart from Claude's and the Codex monitors'.
    private const long RouteVersionBase = 1L << 42;
    private const long SessionVersionBase = -(1L << 41);
    private const string Catalog = "models-dev:v2:catalog";

    private readonly string _database;
    private string? _sessionId;
    private long _version;
    // The shown session's rows by seq, and the newest time_updated among them: a poll reads only what changed since.
    private readonly Dictionary<long, OpenCodeRow> _rows = new();
    private string? _rowsSession;
    private long _rowsUpdated;
    private (long Count, long Updated) _probe;
    private TokenSnapshot? _snapshot;
    private readonly Dictionary<string, (long Limit, DateTime At)> _limits = new(StringComparer.Ordinal);
    public string? VisibleTitle { get; private set; }

    public OpenCodeSource(string? database = null) => _database = database ?? DefaultDatabase;

    internal static string DefaultDatabase => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "opencode", "opencode.db");

    internal static string? SessionTitle(string? windowTitle) =>
        windowTitle is not null && windowTitle.Length > TitleSuffix.Length && windowTitle.EndsWith(TitleSuffix, StringComparison.Ordinal)
            ? windowTitle[..^TitleSuffix.Length] : null;

    public ClaudePollResult Poll(CodexWindowTarget target)
    {
        var title = SessionTitle(WindowText(target.HostWindow.Handle));
        VisibleTitle = title;
        string? id = null, error;
        TokenSnapshot? snapshot = null;
        if (title is null) error = "현재 화면 채팅 확인 대기";
        else (id, snapshot, error) = Read(title);
        if (_sessionId != id) { _sessionId = id; _version++; }
        var route = new ActiveThreadRouteStatus(id, id is null ? 0 : 1, id is not null, RouteVersionBase + _version, error);
        return new ClaudePollResult(route, snapshot, SessionVersionBase - _version);
    }

    internal (string? Id, TokenSnapshot? Snapshot, string? Error) Read(string title)
    {
        if (!File.Exists(_database)) return (null, null, "OpenCode 기록 파일 없음");
        IntPtr db = IntPtr.Zero;
        try
        {
            db = Sqlite.Open(_database);
            if (db == IntPtr.Zero) return (null, null, "OpenCode 기록 읽기 대기");
            var ids = new List<string>();
            Sqlite.Query(db, "select id from session_v2 where title = ?1 and parent_id is null and time_archived is null",
                s => Sqlite.Bind(s, 1, title), s => { if (Sqlite.Text(s, 0) is { Length: > 0 } found) ids.Add(found); });
            if (ids.Count != 1)
                return (null, null, ids.Count == 0 ? "현재 화면 제목과 일치하는 OpenCode 세션 없음" : "같은 제목의 OpenCode 세션이 여럿임");
            var id = ids[0];

            (long Count, long Updated) probe = (0, 0);
            Sqlite.Query(db, "select count(*), coalesce(max(time_updated), 0) from session_message where session_id = ?1",
                s => Sqlite.Bind(s, 1, id), s => probe = (Sqlite.Long(s, 0) ?? 0, Sqlite.Long(s, 1) ?? 0));
            if (_rowsSession == id && probe == _probe && _snapshot is not null) return (id, _snapshot, null);
            // Another chat, or rows taken away (a revert): read the chat again from its start.
            if (_rowsSession != id || probe.Count < _probe.Count) { _rows.Clear(); _rowsUpdated = 0; _rowsSession = id; }
            _probe = probe;

            const string rowsSql = "select seq, type, time_created, time_updated,"
                + " json_extract(data, '$.time.created'), json_extract(data, '$.time.firstToken'), json_extract(data, '$.time.completed'),"
                + " json_extract(data, '$.tokens.input'), json_extract(data, '$.tokens.output'), json_extract(data, '$.tokens.reasoning'),"
                + " json_extract(data, '$.tokens.cache.read'), json_extract(data, '$.tokens.cache.write'),"
                + " json_extract(data, '$.model.providerID'), json_extract(data, '$.model.id'), json_extract(data, '$.outcome')"
                + " from session_message where session_id = ?1 and type in ('user', 'synthetic', 'assistant', 'idle') and time_updated >= ?2";
            var newest = _rowsUpdated;
            var done = Sqlite.Query(db, rowsSql, s => { Sqlite.Bind(s, 1, id); Sqlite.Bind(s, 2, _rowsUpdated); }, s =>
            {
                var row = new OpenCodeRow(Sqlite.Long(s, 0) ?? 0, Sqlite.Text(s, 1) ?? "", Sqlite.Long(s, 2) ?? 0,
                    Sqlite.Long(s, 4), Sqlite.Long(s, 5), Sqlite.Long(s, 6),
                    Sqlite.Long(s, 7) ?? 0, Sqlite.Long(s, 8) ?? 0, Sqlite.Long(s, 9) ?? 0, Sqlite.Long(s, 10) ?? 0, Sqlite.Long(s, 11) ?? 0,
                    Sqlite.Text(s, 12), Sqlite.Text(s, 13), Sqlite.Text(s, 14));
                _rows[row.Seq] = row;
                newest = Math.Max(newest, Sqlite.Long(s, 3) ?? 0);
            });
            if (!done) { _probe = default; return (null, null, "OpenCode 기록 읽기 대기"); }
            _rowsUpdated = newest;

            var read = OpenCodeTurns.Read(_rows.Values.OrderBy(r => r.Seq).ToArray());
            var limit = Limit(db, read.Provider, read.Model);
            var t = read.Turn;
            _snapshot = new TokenSnapshot(id, _database, t?.Total ?? 0, t?.Input ?? 0, t?.Cached ?? 0, t?.Output ?? 0, 0,
                read.Context, limit, DateTime.UtcNow)
            {
                Turn = t, ThreadTitle = title, App = HostApp.OpenCode, History = new TurnHistory(read.Recent),
                ContextFill = limit > 0 && read.Context > 0 ? (double)read.Context / limit : null,
                RingNote = limit > 0 ? ContextUsage.Note(read.Model, limit) : null
            };
            return (id, _snapshot, null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null, "Windows SQLite 없음");
        }
        finally
        {
            if (db != IntPtr.Zero) Sqlite.Close(db);
        }
    }

    // The model's context window from the models.dev catalogue OpenCode keeps in its own kv table; 0 when it is not there.
    private long Limit(IntPtr db, string? provider, string? model)
    {
        if (provider is null || model is null || provider.Contains('"') || model.Contains('"')) return 0;
        var key = provider + "/" + model;
        if (_limits.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromHours(1)) return known.Limit;
        long limit = 0;
        // The catalogue's body may be stored as an object or as JSON text; json_extract of it is JSON text either way.
        Sqlite.Query(db, "select json_extract(json_extract(value, '$.body'), ?1) from kv where key = ?2",
            s => { Sqlite.Bind(s, 1, $"$.\"{provider}\".models.\"{model}\".limit.context"); Sqlite.Bind(s, 2, Catalog); },
            s => limit = Sqlite.Long(s, 0) ?? 0);
        _limits[key] = (limit, DateTime.UtcNow);
        return limit;
    }

    private static string? WindowText(IntPtr handle)
    {
        var text = new StringBuilder(1024);
        return GetWindowText(handle, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);

    // Windows' own SQLite (winsqlite3.dll), as CodexThreadTitles uses it. Writing is only for the checks' made-up databases.
    internal static class Sqlite
    {
        private const int Ok = 0, Row = 100, Done = 101, OpenReadOnly = 0x1, OpenReadWrite = 0x2, OpenCreate = 0x4, Null = 5;
        private static readonly IntPtr Transient = new(-1);

        public static IntPtr Open(string path, bool write = false)
        {
            if (sqlite3_open_v2(Utf8(path), out var db, write ? OpenReadWrite | OpenCreate : OpenReadOnly, IntPtr.Zero) != Ok)
            {
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                return IntPtr.Zero;
            }
            sqlite3_busy_timeout(db, 200);
            return db;
        }

        public static void Close(IntPtr db) => sqlite3_close_v2(db);

        public static bool Execute(IntPtr db, string sql) => sqlite3_exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == Ok;

        // Runs one statement; false when it could not be prepared or stopped before its end.
        public static bool Query(IntPtr db, string sql, Action<IntPtr>? bind, Action<IntPtr> row)
        {
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != Ok) return false;
            try
            {
                bind?.Invoke(statement);
                int step;
                while ((step = sqlite3_step(statement)) == Row) row(statement);
                return step == Done;
            }
            finally { sqlite3_finalize(statement); }
        }

        public static void Bind(IntPtr statement, int index, string text) => sqlite3_bind_text(statement, index, Utf8(text), -1, Transient);
        public static void Bind(IntPtr statement, int index, long value) => sqlite3_bind_int64(statement, index, value);
        public static long? Long(IntPtr statement, int column) =>
            sqlite3_column_type(statement, column) == Null ? null : sqlite3_column_int64(statement, column);
        public static string? Text(IntPtr statement, int column) =>
            sqlite3_column_type(statement, column) == Null ? null : Marshal.PtrToStringUTF8(sqlite3_column_text(statement, column));

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

        [DllImport("winsqlite3.dll")] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] text, int bytes, IntPtr destructor);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_column_type(IntPtr statement, int column);
        [DllImport("winsqlite3.dll")] private static extern long sqlite3_column_int64(IntPtr statement, int column);
        [DllImport("winsqlite3.dll")] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll")] private static extern int sqlite3_close_v2(IntPtr db);
    }
}
