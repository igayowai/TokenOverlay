using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// DeepSeek Harness (DSH) desktop's own numbers (user, 2026-11-16: "딥시크 까보고 오버레이 DSH쪽도 구현해", the same
// range as the OpenCode capsule): the chat in front and its turns, read-only from DSH's files under ~/.dsh.
// The chat: the window title less " — DeepSeek Harness" is the title in the session's projection cache
// (storages/session_projcache/sessions/<id>.json, record.rows.title.val); its log is sessions/<workspace>/<id>/
// session.v4.jsonl.zstd, zstd frames appended one after another, each ending on a line break, one event per line.
// Only numbers and model names come out of it, never a message's text.
// A turn: turn/start → turn/end. Each assistant/message is one model request: from its step/start, its first streamed
// piece, to the message (the answer fully in; the tools it called run after it). The events are handed to
// OpenCodeTurns as OpenCode rows, so both capsules count a turn the same way.
internal static class DeepSeekEvents
{
    // One log line as an OpenCode row, or none. `stepStart` carries the open step's start across lines.
    internal static OpenCodeRow? Row(string line, long seq, ref long? stepStart, ref long window)
    {
        // Cheap filter before parsing: the type comes first on each line.
        var head = line.Length > 48 ? line[..48] : line;
        if (!head.Contains("\"turn/", StringComparison.Ordinal) && !head.Contains("\"step/start\"", StringComparison.Ordinal)
            && !head.Contains("\"assistant/message\"", StringComparison.Ordinal)
            && !head.Contains("\"request/context\"", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("time", out var t) || !t.TryGetInt64(out var time)) return null;
            var data = e.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            switch (e.TryGetProperty("type", out var type) ? type.GetString() : null)
            {
                case "turn/start":
                    stepStart = null;
                    return new OpenCodeRow(seq, "user", time, null, null, null, 0, 0, 0, 0, 0, null, null, null);
                case "step/start":
                    stepStart = time;
                    return null;
                case "request/context":
                    if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("contextWindow", out var w) && w.TryGetInt64(out var size) && size > 0)
                        window = size;
                    return null;
                case "turn/end":
                    stepStart = null;
                    var kind = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.Object && reason.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    // DSH ends a turn completed, aborted (stopped by the user), or legacy; anything naming a failure is 오류.
                    var outcome = kind == "aborted" ? "interrupted"
                        : kind is not null && (kind.Contains("fail", StringComparison.OrdinalIgnoreCase) || kind.Contains("error", StringComparison.OrdinalIgnoreCase))
                            ? "failed" : "succeeded";
                    return new OpenCodeRow(seq, "idle", time, null, null, null, 0, 0, 0, 0, 0, null, null, outcome);
                case "assistant/message" when data.ValueKind == JsonValueKind.Object:
                    long input = 0, output = 0, read = 0, write = 0;
                    if (data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        input = Number(usage, "inputTokens"); output = Number(usage, "outputTokens");
                        read = Number(usage, "cacheReadTokens"); write = Number(usage, "cacheWriteTokens");
                    }
                    string? provider = null, model = null;
                    if (data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                    {
                        if (source.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String) provider = p.GetString();
                        if (source.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
                    }
                    var first = FirstPiece(data);
                    // A message with no step before it starts at its first piece, else at itself.
                    var started = stepStart is long s && s <= time ? s : first ?? time;
                    stepStart = null;
                    return new OpenCodeRow(seq, "assistant", time, started, first is long f && f <= time ? f : null, time,
                        input, output, 0, read, write, provider, model, null, time);
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static long Number(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) && n > 0 ? n : 0;

    // A message's first token, as DSH times it (its isTokenDelta): the first piece carrying text, reasoning or a tool
    // call's name or arguments. The log folds runs of them into reasoning-, text- and tool-call-chunks, each with the
    // time of its first ("time0"); a single delta stays a chunk with its "time". Block starts and ends, usage and finish
    // carry no token. Summed over a chat this gives DSH's own decodeMs to the millisecond (three chats, 2026-11-16).
    internal static long? FirstPiece(JsonElement data)
    {
        if (!data.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Array) return null;
        long? first = null;
        foreach (var piece in stream.EnumerateArray())
        {
            if (piece.ValueKind != JsonValueKind.Object || !piece.TryGetProperty("type", out var kind)) continue;
            long? at = null;
            switch (kind.GetString())
            {
                case "reasoning-chunks" or "text-chunks" or "tool-call-chunks":
                    if (piece.TryGetProperty("time0", out var t0) && t0.TryGetInt64(out var start)) at = start;
                    break;
                case "chunk" when piece.TryGetProperty("chunk", out var chunk) && chunk.ValueKind == JsonValueKind.Object
                        && IsToken(chunk) && piece.TryGetProperty("time", out var t) && t.TryGetInt64(out var time):
                    at = time;
                    break;
            }
            if (at is long a && (first is null || a < first)) first = a;
        }
        return first;
    }

    private static bool IsToken(JsonElement chunk)
    {
        static bool Filled(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 };
        return (chunk.TryGetProperty("type", out var type) ? type.GetString() : null) switch
        {
            "text-delta" or "reasoning-delta" => Filled(chunk, "text"),
            "tool-call-delta" => Filled(chunk, "argumentsDelta") || chunk.TryGetProperty("name", out _),
            _ => false
        };
    }

    // The end of each complete zstd frame from `start`, walking frame and block headers only (RFC 8878); a frame
    // still being written is left for the next read. Skippable frames are stepped over.
    internal static List<int> FrameEnds(ReadOnlySpan<byte> b)
    {
        var ends = new List<int>();
        var i = 0;
        while (FrameEnd(b, i) is int end) { ends.Add(end); i = end; }
        return ends;
    }

    private static int? FrameEnd(ReadOnlySpan<byte> b, int i)
    {
        if (i + 4 > b.Length) return null;
        var magic = BitConverter.ToUInt32(b.Slice(i, 4));
        if ((magic & 0xFFFFFFF0) == 0x184D2A50)
        {
            if (i + 8 > b.Length) return null;
            var skip = (long)i + 8 + BitConverter.ToUInt32(b.Slice(i + 4, 4));
            return skip <= b.Length ? (int)skip : null;
        }
        if (magic != 0xFD2FB528) return null;
        var p = i + 4;
        if (p >= b.Length) return null;
        int descriptor = b[p++];
        int fcs = descriptor >> 6, single = (descriptor >> 5) & 1, checksum = (descriptor >> 2) & 1, dictionary = descriptor & 3;
        p += (single == 1 ? 0 : 1) + dictionary switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 } + fcs switch { 0 => single, 1 => 2, 2 => 4, _ => 8 };
        while (true)
        {
            if (p + 3 > b.Length) return null;
            var header = b[p] | b[p + 1] << 8 | b[p + 2] << 16;
            p += 3;
            int last = header & 1, blockType = (header >> 1) & 3, size = header >> 3;
            if (blockType == 3) return null;
            p += blockType == 1 ? 1 : size;
            if (p > b.Length) return null;
            if (last == 1) break;
        }
        p += checksum == 1 ? 4 : 0;
        return p <= b.Length ? p : null;
    }
}

// Where DSH's automatic compaction starts (dsh-compaction-basic): floor(min(W × thresholdRatio, W − O − headroom)),
// W the route's context window, O its output cap per request, headroom 65,536; "auto: false" leaves the whole window.
// The settings are the desktop profile's own (~/.dsh/profiles/desktop/cordis.patch.yml, the "compaction-basic"
// entry's thresholdRatio, headroomTokens and auto); per-model overrides are not read.
internal static class DeepSeekCompaction
{
    internal readonly record struct Setting(bool Auto, double Ratio, long Headroom);
    internal static readonly Setting Default = new(true, 0.8, 65_536);
    private static readonly object Gate = new();
    private static (string Path, DateTime Written, Setting Value)? _cached;

    internal static string DefaultProfile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles", "desktop", "cordis.patch.yml");

    internal static Setting Current(string path)
    {
        try
        {
            if (!File.Exists(path)) return Default;
            var written = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                if (_cached is { } c && c.Path == path && c.Written == written) return c.Value;
                var value = Parse(File.ReadAllText(path));
                _cached = (path, written, value);
                return value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return _cached?.Value ?? Default; }
    }

    // The patch file is a YAML list of entries ("- id: …"); only the compaction entry's three plain values are read.
    internal static Setting Parse(string yaml)
    {
        var setting = Default;
        var inside = false;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            if (line.StartsWith("- ", StringComparison.Ordinal))
                inside = line[2..].Trim() is var first && first.StartsWith("id:", StringComparison.Ordinal)
                    && first[3..].Trim().Trim('"', '\'') == "compaction-basic";
            if (!inside) continue;
            var text = line.Trim();
            if (Value(text, "thresholdRatio") is { } ratio && double.TryParse(ratio, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var r) && r is > 0 and < 1)
                setting = setting with { Ratio = r };
            else if (Value(text, "headroomTokens") is { } headroom && long.TryParse(headroom.Replace("_", ""), out var h) && h >= 0)
                setting = setting with { Headroom = h };
            else if (Value(text, "auto") is { } auto)
                setting = setting with { Auto = !auto.Equals("false", StringComparison.OrdinalIgnoreCase) };
        }
        return setting;
    }

    private static string? Value(string line, string key) =>
        line.StartsWith(key + ":", StringComparison.Ordinal) ? line[(key.Length + 1)..].Trim().Trim('"', '\'') : null;

    // A request's output cap on the route (O). DSH's DeepSeek adapter defaults to 256,000; its catalog gives
    // opencode-go's deepseek-v4.1-flash 384,000. Another route counts as the adapter default.
    internal static long OutputCap(string? provider, string? model) =>
        provider == "opencode-go" && model == "deepseek-v4.1-flash" ? 384_000 : 256_000;

    internal static long Point(long window, Setting setting, long outputCap)
    {
        if (window <= 0 || !setting.Auto) return window;
        var byRatio = (long)Math.Floor(window * setting.Ratio);
        var room = window - outputCap - setting.Headroom;
        return room > 0 ? Math.Min(byRatio, room) : byRatio;
    }
}

internal sealed class DeepSeekSessionSource
{
    internal const string TitleSuffix = " — DeepSeek Harness";
    // Keeps DSH's route and session versions apart from the other apps'.
    private const long RouteVersionBase = 1L << 43;
    private const long SessionVersionBase = -(1L << 40);

    private readonly string _home;
    private readonly string _profile;
    private string? _sessionId;
    private long _version;
    // Title of each projection-cache file, read again when the file changes.
    private readonly Dictionary<string, (DateTime Write, string? Title)> _titles = new(StringComparer.OrdinalIgnoreCase);
    // The shown session's log: bytes read so far (whole frames), its rows and the state carried across lines.
    private string? _logPath;
    private long _consumed;
    private long _lines;
    private readonly List<OpenCodeRow> _rows = new();
    private long? _stepStart;
    private long _window;
    private TokenSnapshot? _snapshot;
    private DeepSeekCompaction.Setting? _compaction;
    public string? VisibleTitle { get; private set; }

    public DeepSeekSessionSource(string? home = null, string? profile = null)
    {
        _home = home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _profile = profile ?? DeepSeekCompaction.DefaultProfile;
    }

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
        var ids = SessionsTitled(title);
        if (ids.Count != 1)
            return (null, null, ids.Count == 0 ? "현재 화면 제목과 일치하는 DSH 세션 없음" : "같은 제목의 DSH 세션이 여럿임");
        var id = ids[0];
        var log = LogOf(id);
        if (log is null) return (null, null, "DSH 세션 기록 파일 없음");

        try
        {
            using var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Another chat, or a log that got shorter (rewritten): read it again from its start.
            if (_logPath != log || file.Length < _consumed) { _logPath = log; _consumed = 0; _lines = 0; _rows.Clear(); _stepStart = null; _window = 0; _snapshot = null; }
            var compaction = DeepSeekCompaction.Current(_profile);
            if (file.Length == _consumed && _snapshot is not null && _compaction == compaction) return (id, _snapshot, null);
            _compaction = compaction;
            if (file.Length > _consumed)
            {
                file.Position = _consumed;
                var fresh = new byte[file.Length - _consumed];
                file.ReadExactly(fresh);
                var ends = DeepSeekEvents.FrameEnds(fresh);
                if (ends.Count > 0)
                {
                    var whole = ends[^1];
                    foreach (var line in DeepSeekRateSource.Lines(fresh[..whole]))
                        if (DeepSeekEvents.Row(line, ++_lines, ref _stepStart, ref _window) is { } row) _rows.Add(row);
                    _consumed += whole;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null, "DSH 기록 읽기 대기"); }

        var read = OpenCodeTurns.Read(_rows);
        var model = read.Model == "deepseek-flash" ? "deepseek-v4.1-flash" : read.Model;
        var point = DeepSeekCompaction.Point(_window, _compaction ?? DeepSeekCompaction.Default,
            DeepSeekCompaction.OutputCap(read.Provider, read.Model));
        var t = read.Turn;
        _snapshot = new TokenSnapshot(id, log, t?.Total ?? 0, t?.Input ?? 0, t?.Cached ?? 0, t?.Output ?? 0, 0,
            read.Context, _window, DateTime.UtcNow)
        {
            Turn = t, ThreadTitle = title, App = HostApp.DeepSeekHarness, History = new TurnHistory(read.Recent),
            // The ring and its note count to where automatic compaction starts, as Claude's and OpenCode's do.
            ContextFill = point > 0 && read.Context > 0 ? (double)read.Context / point : null,
            RingNote = point > 0 ? ContextUsage.Note(model, point) : null
        };
        return (id, _snapshot, null);
    }

    // Sessions whose projection cache carries this title; a cache file is parsed again only when it changed.
    private List<string> SessionsTitled(string title)
    {
        var found = new List<string>();
        var folder = Path.Combine(_home, "storages", "session_projcache", "sessions");
        try
        {
            if (!Directory.Exists(folder)) return found;
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                try
                {
                    var write = File.GetLastWriteTimeUtc(path);
                    if (!_titles.TryGetValue(path, out var known) || known.Write != write)
                        _titles[path] = known = (write, TitleIn(File.ReadAllBytes(path)));
                    if (known.Title == title) found.Add(Path.GetFileNameWithoutExtension(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    internal static string? TitleIn(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("record", out var record) && record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Object
                && rows.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.Object
                && title.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // sessions/<workspace>/<id>/session.v4.jsonl.zstd, in whichever workspace folder holds it.
    private string? LogOf(string id)
    {
        try
        {
            var sessions = Path.Combine(_home, "sessions");
            if (!Directory.Exists(sessions)) return null;
            foreach (var workspace in Directory.EnumerateDirectories(sessions))
            {
                var path = Path.Combine(workspace, id, "session.v4.jsonl.zstd");
                if (File.Exists(path)) return path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string? WindowText(IntPtr handle)
    {
        var text = new StringBuilder(1024);
        return GetWindowText(handle, text, text.Capacity) > 0 ? text.ToString() : null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maximumCount);
}
