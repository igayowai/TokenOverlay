using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace TokenOverlay;

// Antigravity desktop's own numbers (user, 2026-11-19: "antigravity 앱에도 오버레이 구현해줘", picked 숫자까지 채우기): the
// conversation in front and its turns, read-only from Antigravity's files under ~/.gemini/antigravity. Only ids, times,
// token numbers and model ids come out, never a message's text.
// The conversation: the window title is its title, cut to 25 characters and "..." when longer; it is looked up in
// conversation_summaries.db (CLI and sub-agent conversations left out). Titles sharing that start are told apart by the
// time the user last viewed each (annotations/<id>.pbtxt, last_user_view_time).
// Its steps: conversations/<id>.db, table steps, each with a protobuf metadata blob. Step type 14 is the user's input,
// 15 a model request (PLANNER_RESPONSE): metadata field 1 its start, 6 its first token, 7 its answer fully in, 9 its
// usage {1 model enum, 2 input, 3 output (thinking included), 4 cache write, 5 cache read}. Other steps (tools) only
// move the turn's end (field 8, done). The turn ends when the next input comes or the conversation goes idle
// (the summary's status). The steps are handed to OpenCodeTurns as OpenCode rows, so all three capsules count a turn alike.
// The ring: gen_metadata rows carry the request's model id (field 1.19) and Antigravity's context budget for it
// (1.9.10.4, 512,000 for its Gemini agents); the newest gen row also carries the whole prompt, so only rows under
// 256 KB are read.
internal static class AntigravityProto
{
    // The next field of a message: its number, wire type, a varint's value or a length-delimited value's span.
    // False at the end or on bytes that are not a message.
    internal static bool Next(ReadOnlySpan<byte> b, ref int i, out int field, out int wire, out ulong number, out int start, out int length)
    {
        field = wire = start = length = 0;
        number = 0;
        if (i >= b.Length || !Varint(b, ref i, out var key)) return false;
        field = (int)(key >> 3);
        wire = (int)(key & 7);
        if (field <= 0) return false;
        switch (wire)
        {
            case 0: return Varint(b, ref i, out number);
            case 1: if (i + 8 > b.Length) return false; i += 8; return true;
            case 5: if (i + 4 > b.Length) return false; i += 4; return true;
            case 2:
                if (!Varint(b, ref i, out var size) || size > (ulong)(b.Length - i)) return false;
                start = i; length = (int)size; i += length;
                return true;
            default: return false;
        }
    }

    private static bool Varint(ReadOnlySpan<byte> b, ref int i, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && i < b.Length; shift += 7)
        {
            var c = b[i++];
            value |= (ulong)(c & 0x7f) << shift;
            if (c < 0x80) return true;
        }
        return false;
    }

    // The last value of a varint field, or none.
    internal static long? Number(ReadOnlySpan<byte> b, int wanted)
    {
        long? found = null;
        var i = 0;
        while (Next(b, ref i, out var field, out var wire, out var number, out _, out _))
            if (field == wanted && wire == 0) found = (long)number;
        return found;
    }

    // The last value of a length-delimited field (a message, text or bytes), or an empty span.
    internal static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> b, int wanted)
    {
        int? at = null, size = null;
        var i = 0;
        while (Next(b, ref i, out var field, out var wire, out _, out var start, out var length))
            if (field == wanted && wire == 2) { at = start; size = length; }
        return at is int s ? b.Slice(s, size!.Value) : [];
    }

    internal static ReadOnlySpan<byte> Nested(ReadOnlySpan<byte> b, params int[] fields)
    {
        foreach (var f in fields) b = Field(b, f);
        return b;
    }

    // A google.protobuf.Timestamp field {1 seconds, 2 nanos} in Unix milliseconds.
    internal static long? Time(ReadOnlySpan<byte> b, int wanted)
    {
        var t = Field(b, wanted);
        return t.IsEmpty || Number(t, 1) is not long seconds || seconds <= 0 ? null : seconds * 1000 + (Number(t, 2) ?? 0) / 1_000_000;
    }

    internal static string? Text(ReadOnlySpan<byte> b) => b.IsEmpty ? null : Encoding.UTF8.GetString(b);
}

internal readonly record struct AntigravityStep(long Index, int Type, byte[] Metadata);

// Model ids as Antigravity's model menu names them (user's screenshot, 2026-11-19: Gemini 4 Neon / Argon / Hydrogen,
// Orchestrator (Neon) / (Argon), Gemini 3.8 Flash, Claude Opus 5.5 …). "gemini-pro-orchestrator-g1-pro" is the
// Orchestrator (Argon) the user had picked (last_selected_agent_model M668, the same enum in that conversation). The
// orchestrator hands the work to a flash worker the menu does not list; its requests go under the picked model's name.
internal static class AntigravityModels
{
    private static readonly HashSet<string> Grades = new(StringComparer.Ordinal) { "trial", "high", "low", "medium", "xhigh", "minimal", "tiered", "a" };

    internal static bool IsWorker(string? id) => id is not null && id.Contains("-worker", StringComparison.Ordinal);

    // "gemini-4-argon-trial" → "Gemini 4 Argon", "gemini-3.5-flash-lite" → "Gemini 3.5 Flash Lite"; other makers' ids
    // as the other capsules shorten them.
    internal static string? Display(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id == "gemini-pro-orchestrator-g1-pro") return "Orchestrator (Argon)";
        // "gemini-pro-orchestrator-neon" is Orchestrator (Neon) (seen 2026-11-20 after the user picked it).
        const string orchestrator = "orchestrator-";
        if (id.IndexOf(orchestrator, StringComparison.Ordinal) is var at && at >= 0)
        {
            var name = id[(at + orchestrator.Length)..].Split('-')[0];
            return name.Length > 0 && name.All(char.IsLetter) ? $"Orchestrator ({char.ToUpperInvariant(name[0])}{name[1..]})" : "Orchestrator";
        }
        if (id.Contains("orchestrator", StringComparison.Ordinal)) return "Orchestrator";
        if (IsWorker(id)) return "Flash Worker";
        var parts = id.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["gemini", var version, ..] && version.Length > 0 && char.IsDigit(version[0]))
        {
            var words = parts.Skip(2).TakeWhile(p => !Grades.Contains(p)).Select(p => char.ToUpperInvariant(p[0]) + p[1..]).ToArray();
            return words.Length == 0 ? null : $"Gemini {version} {string.Join(' ', words)}";
        }
        return ModelNames.Short(id);
    }

    // The name each request's model enum shows under: its own, but a worker's is the conversation's latest other model
    // (the orchestrator that sent it), so one turn reads as the picked model.
    internal static Func<long, string?> Namer(IEnumerable<AntigravityStep> steps, Func<long, string?> id)
    {
        string? planner = null;
        foreach (var step in steps.OrderBy(s => s.Index))
            if (step.Type == AntigravitySteps.PlannerResponse
                && AntigravityProto.Number(AntigravityProto.Field(step.Metadata, 9), 1) is long e && e > 0
                && id(e) is { } name && !IsWorker(name))
                planner = name;
        return e => id(e) is { } name ? Display(IsWorker(name) && planner is not null ? planner : name) : null;
    }
}

internal static class AntigravitySteps
{
    internal const int UserInput = 14, PlannerResponse = 15;

    // The steps as OpenCode rows. `running`: the conversation is still working, so its last turn has no end yet.
    // `model` names a request's model enum (null when unknown).
    internal static List<OpenCodeRow> Rows(IEnumerable<AntigravityStep> steps, bool running, Func<long, string?> model)
    {
        var rows = new List<OpenCodeRow>();
        long seq = 0;
        long? lastEnd = null;
        var open = false;
        foreach (var step in steps.OrderBy(s => s.Index))
        {
            var m = step.Metadata.AsSpan();
            var created = AntigravityProto.Time(m, 1);
            if (step.Type == UserInput)
            {
                // A new input ends the turn before it at its last step's end.
                if (open && lastEnd is long end) rows.Add(Idle(++seq, end));
                var at = created ?? lastEnd ?? 0;
                rows.Add(new OpenCodeRow(++seq, "user", at, null, null, null, 0, 0, 0, 0, 0, null, null, null));
                open = true;
                lastEnd = at;
                continue;
            }
            if (step.Type == PlannerResponse)
            {
                var usage = AntigravityProto.Field(m, 9);
                var first = AntigravityProto.Time(m, 6);
                var completed = AntigravityProto.Time(m, 7) ?? AntigravityProto.Time(m, 8);
                var enumValue = AntigravityProto.Number(usage, 1) ?? 0;
                rows.Add(new OpenCodeRow(++seq, "assistant", completed ?? created ?? lastEnd ?? 0, created,
                    first is long f && (created is null || f >= created) ? f : null, completed,
                    Count(usage, 2), Count(usage, 3), 0, Count(usage, 5), Count(usage, 4), null,
                    enumValue > 0 ? model(enumValue) : null, null, completed));
                open = true;
            }
            var done = AntigravityProto.Time(m, 8) ?? AntigravityProto.Time(m, 7) ?? AntigravityProto.Time(m, 6) ?? created;
            if (done is long d && (lastEnd is null || d > lastEnd)) lastEnd = d;
        }
        if (open && !running && lastEnd is long last) rows.Add(Idle(++seq, last));
        return rows;
    }

    private static long Count(ReadOnlySpan<byte> usage, int field) => AntigravityProto.Number(usage, field) is long n && n > 0 ? n : 0;

    // Antigravity marks no outcome on a turn; a stopped one reads 완료 like a finished one.
    private static OpenCodeRow Idle(long seq, long at) => new(seq, "idle", at, null, null, null, 0, 0, 0, 0, 0, null, null, "succeeded");

    // The last request's model enum, for the ring's budget and name.
    internal static long? LastModel(IEnumerable<AntigravityStep> steps)
    {
        long? found = null;
        foreach (var step in steps.OrderBy(s => s.Index))
            if (step.Type == PlannerResponse && AntigravityProto.Number(AntigravityProto.Field(step.Metadata, 9), 1) is long e && e > 0)
                found = e;
        return found;
    }
}

internal sealed class AntigravitySource
{
    // Keeps Antigravity's route and session versions apart from the other apps'.
    private const long RouteVersionBase = 1L << 44;
    private const long SessionVersionBase = -(1L << 39);
    internal const string CliSource = "CORTEX_TRAJECTORY_SOURCE_CLI";
    // Gen rows above this carry the whole prompt; they are left alone.
    private const int SmallGenRow = 256 * 1024;
    private static readonly Regex ViewTime = new(@"last_user_view_time\s*:?\s*\{\s*seconds\s*:\s*(\d+)(?:\s+nanos\s*:\s*(\d+))?", RegexOptions.CultureInvariant);

    private readonly string _home;
    private string? _conversationId;
    private long _version;
    // The shown conversation: its files' sizes and times and its status when last read, and what was read.
    private (string Id, long Db, DateTime DbAt, long Wal, DateTime WalAt, string Status) _probe;
    private TokenSnapshot? _snapshot;
    // Per model enum, from gen rows: its model id and Antigravity's context budget for it; the gen rows read so far.
    private readonly Dictionary<long, (string? Name, long Budget)> _models = new();
    private string? _genConversation;
    private long _genRead = -1;
    public string? VisibleTitle { get; private set; }

    public AntigravitySource(string? home = null)
    {
        _home = home ?? DefaultHome;
    }

    internal static string DefaultHome => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");

    // The window's title as a conversation title; the app's own name alone (no conversation open) is none.
    internal static string? ConversationTitle(string? windowTitle) =>
        string.IsNullOrWhiteSpace(windowTitle) || windowTitle.Trim() == "Antigravity" ? null : windowTitle;

    // A title as the window shows it: whole, or its start with "..." when cut.
    internal static bool TitleMatches(string window, string title) =>
        title.Length > 0 && (title == window
            || window.Length > 3 && window.EndsWith("...", StringComparison.Ordinal) && title.Length > window.Length - 3
               && title.StartsWith(window[..^3], StringComparison.Ordinal));

    public ClaudePollResult Poll(CodexWindowTarget target)
    {
        var title = ConversationTitle(WindowText(target.HostWindow.Handle));
        VisibleTitle = title;
        string? id = null, error;
        TokenSnapshot? snapshot = null;
        if (title is null) error = "현재 화면 대화 확인 대기";
        else (id, snapshot, error) = Read(title);
        if (_conversationId != id) { _conversationId = id; _version++; }
        var route = new ActiveThreadRouteStatus(id, id is null ? 0 : 1, id is not null, RouteVersionBase + _version, error);
        return new ClaudePollResult(route, snapshot, SessionVersionBase - _version);
    }

    internal (string? Id, TokenSnapshot? Snapshot, string? Error) Read(string windowTitle)
    {
        var summaries = System.IO.Path.Combine(_home, "conversation_summaries.db");
        if (!File.Exists(summaries)) return (null, null, "Antigravity 기록 파일 없음");
        try
        {
            var found = Conversations(summaries, windowTitle);
            if (found is null) return (null, null, "Antigravity 기록 읽기 대기");
            if (found.Count > 1) found = Newest(found);
            if (found.Count != 1)
                return (null, null, found.Count == 0 ? "현재 화면 제목과 일치하는 Antigravity 대화 없음" : "같은 제목의 Antigravity 대화가 여럿임");
            var (id, title, status) = found[0];
            var database = System.IO.Path.Combine(_home, "conversations", id + ".db");
            if (!File.Exists(database)) return (null, null, "Antigravity 대화 기록 파일 없음");

            var db = new FileInfo(database);
            var wal = new FileInfo(database + "-wal");
            var probe = (id, db.Length, db.LastWriteTimeUtc, wal.Exists ? wal.Length : 0, wal.Exists ? wal.LastWriteTimeUtc : default, status);
            if (probe == _probe && _snapshot is not null) return (id, _snapshot, null);

            var steps = new List<AntigravityStep>();
            var handle = OpenCodeSource.Sqlite.Open(database);
            if (handle == IntPtr.Zero) return (null, null, "Antigravity 기록 읽기 대기");
            try
            {
                if (!OpenCodeSource.Sqlite.Query(handle, "select idx, step_type, metadata from steps", null,
                        s => steps.Add(new AntigravityStep(OpenCodeSource.Sqlite.Long(s, 0) ?? 0, (int)(OpenCodeSource.Sqlite.Long(s, 1) ?? 0),
                            OpenCodeSource.Sqlite.Blob(s, 2)))))
                    return (null, null, "Antigravity 기록 읽기 대기");
                ReadModels(handle, id);
            }
            finally { OpenCodeSource.Sqlite.Close(handle); }

            var running = status is "CASCADE_RUN_STATUS_RUNNING" or "CASCADE_RUN_STATUS_BUSY" or "CASCADE_RUN_STATUS_CANCELING";
            var rows = AntigravitySteps.Rows(steps, running,
                AntigravityModels.Namer(steps, e => _models.TryGetValue(e, out var m) ? m.Name : null));
            var read = OpenCodeTurns.Read(rows);
            var budget = AntigravitySteps.LastModel(steps) is long last && _models.TryGetValue(last, out var known) ? known.Budget : 0;
            var t = read.Turn;
            _snapshot = new TokenSnapshot(id, database, t?.Total ?? 0, t?.Input ?? 0, t?.Cached ?? 0, t?.Output ?? 0, 0,
                read.Context, budget, DateTime.UtcNow)
            {
                Turn = t, ThreadTitle = title, App = HostApp.Antigravity, History = new TurnHistory(read.Recent),
                // The ring counts against the budget Antigravity itself keeps for the model's context.
                ContextFill = budget > 0 && read.Context > 0 ? (double)read.Context / budget : null,
                RingNote = budget > 0 ? ContextUsage.Note(read.Model, budget) : null
            };
            _probe = probe;
            return (id, _snapshot, null);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return (null, null, "Windows SQLite 없음");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null, "Antigravity 기록 읽기 대기");
        }
    }

    // App conversations whose title the window shows; null when the summaries could not be read.
    private static List<(string Id, string Title, string Status)>? Conversations(string summaries, string windowTitle)
    {
        var found = new List<(string, string, string)>();
        var handle = OpenCodeSource.Sqlite.Open(summaries);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var done = OpenCodeSource.Sqlite.Query(handle,
                "select conversation_id, title, status from conversation_summaries where title <> '' and source <> ?1"
                + " and agent_name = '' and coalesce(parent_conversation_id, '') = ''",
                s => OpenCodeSource.Sqlite.Bind(s, 1, CliSource),
                s =>
                {
                    if (OpenCodeSource.Sqlite.Text(s, 0) is { Length: > 0 } id && OpenCodeSource.Sqlite.Text(s, 1) is { } title
                        && TitleMatches(windowTitle, title))
                        found.Add((id, title, OpenCodeSource.Sqlite.Text(s, 2) ?? ""));
                });
            return done ? found : null;
        }
        finally { OpenCodeSource.Sqlite.Close(handle); }
    }

    // Of conversations sharing the shown start, the one the user viewed last; all of them when that cannot tell.
    private List<(string Id, string Title, string Status)> Newest(List<(string Id, string Title, string Status)> found)
    {
        var viewed = found.Select(f => (f, At: ViewedAt(f.Id))).Where(v => v.At is not null).OrderByDescending(v => v.At).ToArray();
        return viewed.Length > 0 && (viewed.Length == 1 || viewed[0].At != viewed[1].At) ? [viewed[0].f] : found;
    }

    private long? ViewedAt(string id)
    {
        try
        {
            var path = System.IO.Path.Combine(_home, "annotations", id + ".pbtxt");
            return File.Exists(path) ? ViewedAtIn(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static long? ViewedAtIn(string text) =>
        ViewTime.Match(text) is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds * 1000 + (m.Groups[2].Success && long.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var nanos) ? nanos / 1_000_000 : 0)
            : null;

    // Model ids and context budgets from the gen rows not read yet (small ones only).
    private void ReadModels(IntPtr handle, string id)
    {
        if (_genConversation != id) { _genConversation = id; _genRead = -1; }
        var newest = _genRead;
        OpenCodeSource.Sqlite.Query(handle, "select idx, data from gen_metadata where idx > ?1 and length(data) < ?2 order by idx",
            s => { OpenCodeSource.Sqlite.Bind(s, 1, _genRead); OpenCodeSource.Sqlite.Bind(s, 2, SmallGenRow); },
            s =>
            {
                newest = Math.Max(newest, OpenCodeSource.Sqlite.Long(s, 0) ?? newest);
                if (ModelIn(OpenCodeSource.Sqlite.Blob(s, 1)) is { } model) Remember(model);
            });
        _genRead = newest;
    }

    internal void Remember((long Enum, string? Name, long Budget) model)
    {
        _models.TryGetValue(model.Enum, out var known);
        _models[model.Enum] = (model.Name ?? known.Name, model.Budget > 0 ? model.Budget : known.Budget);
    }

    // A gen row's model: its enum (1.3), id (1.19) and context budget (1.9.10.4).
    internal static (long Enum, string? Name, long Budget)? ModelIn(byte[] data)
    {
        var request = AntigravityProto.Field(data, 1);
        if (AntigravityProto.Number(request, 3) is not long e || e <= 0) return null;
        var name = AntigravityProto.Text(AntigravityProto.Field(request, 19));
        var budget = AntigravityProto.Number(AntigravityProto.Nested(request, 9, 10), 4) ?? 0;
        return (e, name is { Length: > 0 and <= 80 } ? name : null, budget);
    }

    private static string? WindowText(IntPtr handle)
    {
        var text = new StringBuilder(1024);
        return GetWindowText(handle, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);
}
