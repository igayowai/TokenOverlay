using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace TokenOverlay;

// OpenCode: its desktop app gets the capsule too, for the Ctrl list's OpenCode Go table (user, 2026-11-12: "opencode에도
// 토큰오버레이 뜨게 해줘 ... ctrl 누르고 마우스오버해서 오픈코드 전체사용량 볼 수 있으니까", "똑같이 우측 하단이면 됨").
// Its own turns are not read yet: the capsule waits as for a chat not known, with the Claude anchor setting.
// DeepSeek Harness: its desktop app the same way (user, 2026-11-13: "deepseek harness 앱에서도 opencode처럼 오버레이
// 뜨게 해줘"); its turns are not read either.
internal enum HostApp { None, Codex, Claude, OpenCode, DeepSeekHarness }

// Claude desktop "Code" tab sessions only. Regular claude.ai chats keep no local per-turn usage,
// so they are reported as unavailable instead of being estimated.
// Read-only JSONL reader for %USERPROFILE%\.claude\projects\*\<cliSessionId>.jsonl. No text is stored.
internal sealed class ClaudeTurnReader
{
    private string? _path;
    private long _offset;
    private readonly MemoryStream _line = new();
    private readonly byte[] _buffer = new byte[65536];
    private Turn? _active;
    private Turn? _previous;
    private readonly List<Turn> _earlier = new();
    // Whole-session cache facts: input and cache-read sums, and the last main-agent request with its cache lifetime.
    private long _sessionInput, _sessionCached, _lastContext;
    private bool _compacted;
    // After a compaction only the summary's size is logged. The rest of the next request (tools, skills, memory, prompt)
    // is learned from this chat: the first request after the last compaction minus its summary, else the chat's first request.
    private long _summary, _overhead;
    private bool _overheadLearned;
    private string? _lastModel;
    // The folder the session works in, for naming a session the app has no title for.
    private string? _folder;
    private DateTimeOffset? _lastRequest;
    private int? _ttl;
    // When this session's tool calls launched the DeepSeek worker, newest last (for its rows on this session only).
    private readonly List<DateTimeOffset> _workerCalls = new();
    // Claude Code sometimes writes earlier records again further down (seen around a compaction): a record is read once.
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    private sealed record Usage(long Input, long Output, long Cached, bool? Fast, string? RequestId);
    // Request ids of the turn the last Snapshot() describes, for matching telemetry to that same turn.
    internal IReadOnlyList<string?> DisplayedRequestIds { get; private set; } = [];
    private sealed class Turn(string id, DateTimeOffset? started)
    {
        public string Id = id;
        public DateTimeOffset? Started = started;
        public DateTimeOffset? Last;
        // One API response is logged once per content block with identical usage: count each id once.
        public readonly Dictionary<string, Usage> Messages = new(StringComparer.Ordinal);
        public bool Done;
        public bool AwaitingTool;
        public bool Restored;
        // Opened by a background task's notice: the app may never answer it, so it is not shown running until it does.
        public bool Notice;
        // Models of the main agent's responses, in order of first use.
        public readonly List<string> Models = new();
        public string State = "진행 중";
        public string? Problem;
        public double? Seconds => Started.HasValue && Last.HasValue ? (Last.Value - Started.Value).TotalSeconds : null;
        public string FastMode
        {
            get
            {
                // A cut-off response carries no speed; the turn's other responses still tell.
                if (Messages.Values.All(m => m.Fast is null)) return "unknown";
                var on = Messages.Values.Any(m => m.Fast == true);
                var off = Messages.Values.Any(m => m.Fast == false);
                return on && off ? "mixed" : on ? "on" : "off";
            }
        }
    }

    public TurnStatistics? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (_path != path || file.Length < _offset)
            {
                _path = path; _offset = 0; _line.SetLength(0); CaughtUp = false;
                _active = null; _previous = null; _earlier.Clear();
                _sessionInput = _sessionCached = _lastContext = 0; _lastRequest = null; _ttl = null; _compacted = false; _lastModel = null; _folder = null;
                _summary = _overhead = 0; _overheadLearned = false; _workerCalls.Clear(); _seen.Clear();
            }
            file.Position = _offset;
            var budget = 16 * 1024 * 1024;
            while (budget > 0)
            {
                var read = file.Read(_buffer, 0, Math.Min(_buffer.Length, budget));
                if (read == 0) break;
                _offset += read; budget -= read;
                for (var i = 0; i < read; i++)
                {
                    if (_buffer[i] == (byte)'\n')
                    {
                        ProcessLine(Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length));
                        _line.SetLength(0);
                    }
                    else _line.WriteByte(_buffer[i]);
                }
            }
            CaughtUp = _offset >= file.Length;
            if (!CaughtUp) return null; // never present a partially scanned history as latest
            return Snapshot();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TurnStatistics("", null, null, null, null, "로그 읽기 대기", false, "파일 접근 대기");
        }
    }

    internal void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Text(root, "type");
            if (type is not ("user" or "assistant" or "system")) return;
            // Sub-agent traffic is not the main agent's turn.
            if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;
            if (Text(root, "uuid") is { Length: > 0 } uuid && !_seen.Add(uuid)) return;
            var at = DateTimeOffset.TryParse(Text(root, "timestamp"), out var parsed) ? parsed : (DateTimeOffset?)null;
            _folder ??= Text(root, "cwd");

            if (type == "system")
            {
                // The compaction request itself is not logged; until the next request the old context size is stale.
                if (Text(root, "subtype") == "compact_boundary")
                {
                    _compacted = true; _lastContext = 0;
                    _summary = root.TryGetProperty("compactMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                        ? Number(meta, "postTokens") ?? 0 : 0;
                    return;
                }
                if (Text(root, "subtype") == "turn_duration" && _active is { Done: false, Messages.Count: > 0 } open)
                {
                    open.Done = true; open.State = "완료";
                }
                return;
            }

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (type == "user")
            {
                // Meta records are not prompts, except a message another session sent: that starts a turn.
                if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True
                    && !(root.TryGetProperty("origin", out var origin) && origin.ValueKind == JsonValueKind.Object
                        && Text(origin, "kind") == "peer")) return;
                // A compaction summary is not a prompt: an automatic compaction continues the turn it fell in.
                if (root.TryGetProperty("isCompactSummary", out var summary) && summary.ValueKind == JsonValueKind.True) return;
                var (toolResult, text) = DescribeUserContent(message);
                // A local command (/compact, /model) answers without the model: its turn ends with no reply.
                if (text is not null && (text.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
                    || text.StartsWith("<local-command-stderr>", StringComparison.Ordinal)))
                {
                    if (_active is { Done: false, Messages.Count: 0 }) { _active.Done = true; _active.State = "완료"; }
                    return;
                }
                if (toolResult)
                {
                    if (_active is not null) { _active.Done = false; _active.State = "진행 중"; _active.AwaitingTool = false; }
                    return;
                }
                if (text is not null && text.StartsWith("[Request interrupted", StringComparison.Ordinal))
                {
                    if (_active is { Done: false })
                    {
                        _active.Done = true; _active.State = "중단"; _active.Last = at ?? _active.Last;
                    }
                    return;
                }
                var id = Text(root, "promptId") ?? Text(root, "uuid") ?? "";
                if (_active is { Done: false })
                {
                    // A queued message delivered inside the same prompt continues the turn.
                    if (_active.Id == id) return;
                    _active.Done = true; _active.State = "중단";
                    if (_active.AwaitingTool) _active.Problem = "종료 기록 없음";
                }
                // A turn with no reply (a local command, a prompt stopped at once) has nothing to show as the previous turn.
                if (_active is { Messages.Count: > 0 })
                {
                    _previous = _active;
                    _earlier.Add(_active);
                    if (_earlier.Count > 16) _earlier.RemoveAt(0);
                }
                // Each response carries its own usage, so a damaged line only spoils the turn it fell in.
                _active = new Turn(id, at) { Notice = text?.StartsWith("<task-notification>", StringComparison.Ordinal) == true };
                return;
            }

            if (at.HasValue && message.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                foreach (var block in blocks.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && Text(block, "type") == "tool_use"
                        && block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        && Text(input, "command") is { } command && DeepSeekRateSource.StartsWorker(command))
                    {
                        _workerCalls.Add(at.Value);
                        if (_workerCalls.Count > 32) _workerCalls.RemoveAt(0);
                    }
            if (_active is null) return;
            // The app's own stand-in reply ("No response requested.", an API error) is no request: it ends the turn but adds
            // no usage, time or cache refresh. Seen 2026-10-21: one written 8 hours after a notice made an 8-hour 0-token turn.
            if (Text(message, "model") == "<synthetic>")
            {
                _active.Done = true; _active.AwaitingTool = false;
                if (_active.Messages.Count > 0 && _active.State == "진행 중") _active.State = "완료";
                return;
            }
            if (Text(message, "model") is { Length: > 0 } model && model[0] != '<' && !_active.Models.Contains(model))
                _active.Models.Add(model);
            var messageId = Text(message, "id");
            if (messageId is not null && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                var cached = Number(usage, "cache_read_input_tokens") ?? 0;
                var created = Number(usage, "cache_creation_input_tokens") ?? 0;
                var input = (Number(usage, "input_tokens") ?? 0) + created + cached;
                bool? fast = Text(usage, "speed") switch { "fast" => true, "standard" => false, _ => null };
                if (_active.Messages.TryGetValue(messageId, out var before)) { _sessionInput -= before.Input; _sessionCached -= before.Cached; }
                _active.Messages[messageId] = new Usage(input, Number(usage, "output_tokens") ?? 0, cached, fast, Text(root, "requestId"));
                _sessionInput += input; _sessionCached += cached;
                if (created * 2 > input && input >= RestoreFloor) _active.Restored = true;
                if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
                {
                    if (Number(creation, "ephemeral_1h_input_tokens") > 0) _ttl = 60;
                    else if (Number(creation, "ephemeral_5m_input_tokens") > 0) _ttl = 5;
                }
                if (at.HasValue && (_lastRequest is null || at > _lastRequest)) _lastRequest = at;
                // A synthetic reply with no usage is not a request: it neither sizes the context nor ends a compaction.
                if (input > 0)
                {
                    if (_compacted && _summary > 0 && input > _summary) { _overhead = input - _summary; _overheadLearned = true; }
                    else if (!_overheadLearned && _overhead == 0) _overhead = input;
                    _lastContext = input; _compacted = false;
                }
                if (Text(message, "model") is { Length: > 0 } used && used[0] != '<') _lastModel = used;
            }
            if (at.HasValue) _active.Last = at;
            var stop = Text(message, "stop_reason");
            _active.AwaitingTool = stop is "tool_use" or "pause_turn";
            var finished = stop is not null && !_active.AwaitingTool;
            _active.Done = finished;
            _active.State = finished ? "완료" : "진행 중";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            if (_active is not null) _active.Problem = "손상된 기록 포함";
        }
    }

    // Below this a fresh write is just a small chat's first request, not a rebuilt cache worth pointing out.
    internal const long RestoreFloor = 20_000;

    // Context of the last main-agent request and its model. Right after a compaction, until the next request,
    // the summary plus this chat's learned overhead; none when either is unknown.
    internal long Context => !CaughtUp ? 0 : !_compacted ? _lastContext : _summary > 0 && _overhead > 0 ? _summary + _overhead : 0;
    internal string? LastModel => _lastModel;
    internal string? Folder => _folder;
    internal IReadOnlyList<DateTimeOffset> WorkerCalls => CaughtUp ? _workerCalls.ToArray() : [];

    internal CacheState? Cache => CaughtUp && _sessionInput > 0
        ? new CacheState(_sessionInput, _sessionCached, _lastRequest, _ttl, _lastContext, Compacted: _compacted) : null;

    internal TurnStatistics? Snapshot()
    {
        var running = _active is { Done: false } && !(_active.Notice && _active.Messages.Count == 0);
        var t = running || _active is { Messages.Count: 0 } ? _previous ?? _active : _active;
        DisplayedRequestIds = t is null ? [] : RequestIds(t);
        return t is null ? null : Describe(t, running, ReferenceEquals(t, _active) && running);
    }

    // Completed turns, newest first, each with its request ids for matching telemetry.
    internal bool CaughtUp { get; private set; } = true;

    internal IReadOnlyList<(TurnStatistics Turn, IReadOnlyList<string?> RequestIds)> Recent(int count) =>
        !CaughtUp ? [] : _earlier.Concat(_active is { Done: true, Messages.Count: > 0 } ? new[] { _active } : Array.Empty<Turn>()).Reverse().Take(count)
            .Select(t => (Describe(t, false, false), RequestIds(t))).ToArray();

    private static IReadOnlyList<string?> RequestIds(Turn t) => t.Messages.Values
        .Where(m => m.RequestId is not null || m.Output > 0).Select(m => m.RequestId).ToArray();

    private static TurnStatistics Describe(Turn t, bool running, bool shownRunning)
    {
        var valid = t.Problem is null && t.Messages.Count > 0;
        double? seconds = shownRunning ? null : t.Seconds;
        var problem = t.Problem ?? (!shownRunning && seconds is not > 0 ? "유효한 소요 시간 없음" : null);
        return new TurnStatistics(t.Id,
            valid ? t.Messages.Values.Sum(m => m.Input) : null,
            valid ? t.Messages.Values.Sum(m => m.Output) : null,
            valid ? t.Messages.Values.Sum(m => m.Cached) : null,
            seconds,
            problem is not null ? "기록 불완전" : t.Messages.Count == 0 ? "집계 대기" : t.State,
            running, problem) { FastMode = t.FastMode, Restored = t.Restored, Model = t.Models.Count == 0 ? null : t.Models[^1] + (t.Models.Count > 1 ? "+" : "") };
    }

    private static (bool ToolResult, string? Text) DescribeUserContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return (false, null);
        if (content.ValueKind == JsonValueKind.String) return (false, content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return (false, null);
        string? first = null;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            var kind = Text(block, "type");
            if (kind == "tool_result") return (true, null);
            if (kind == "text") first ??= Text(block, "text");
        }
        return (false, first);
    }

    private static string? Text(JsonElement p, string key) => p.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Number(JsonElement p, string key) => p.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

internal sealed record ClaudeSessionEntry(string CliSessionId, string Title);

// Desktop session metadata (title ↔ CLI session id). Only those two fields are kept.
internal sealed class ClaudeSessionIndex
{
    private readonly Dictionary<string, (DateTime Write, ClaudeSessionEntry? Entry)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _logs = new(StringComparer.Ordinal);

    internal static IEnumerable<string> DataRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Since Claude 3.1623 (seen 2026-10-20) the data moves here, one folder per package family, and the old
        // places below keep only an empty ".user-data-left.*" marker.
        var moved = Path.Combine(local, "Claude-Data");
        if (Directory.Exists(moved))
            foreach (var family in Directory.EnumerateDirectories(moved))
                yield return family;
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
            foreach (var package in Directory.EnumerateDirectories(packages, "Claude_*"))
                yield return Path.Combine(package, "LocalCache", "Roaming", "Claude");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    }

    internal static string ProjectsRoot()
    {
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(home, "projects");
    }

    public string? ResolveUniqueTitle(string title)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DataRoots())
        {
            var sessions = Path.Combine(root, "claude-code-sessions");
            try
            {
                if (!Directory.Exists(sessions)) continue;
                foreach (var file in Directory.EnumerateFiles(sessions, "local_*.json", SearchOption.AllDirectories))
                {
                    seen.Add(file);
                    var write = File.GetLastWriteTimeUtc(file);
                    if (_files.TryGetValue(file, out var known) && known.Write == write) continue;
                    _files[file] = (write, ReadEntry(file));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var gone in _files.Keys.Where(key => !seen.Contains(key)).ToArray()) _files.Remove(gone);
        return MatchUniqueTitle(_files.Values.Where(v => v.Entry is not null).Select(v => v.Entry!), title);
    }

    // For a bug report: why the last seen title did or did not find its session. Counts and lengths only, never a title.
    internal static string Describe(string? title, DateTime nowUtc)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"claude config dir {(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")) ? "default" : "set")}"
            + $" · projects folder {(Directory.Exists(ProjectsRoot()) ? "found" : "missing")}");
        var entries = new List<(ClaudeSessionEntry Entry, DateTime Write)>();
        var archived = 0;
        foreach (var root in DataRoots())
        {
            var sessions = Path.Combine(root, "claude-code-sessions");
            var files = 0;
            try
            {
                if (Directory.Exists(sessions))
                    foreach (var file in Directory.EnumerateFiles(sessions, "local_*.json", SearchOption.AllDirectories))
                    {
                        files++;
                        if (ReadEntry(file) is { } entry) entries.Add((entry, File.GetLastWriteTimeUtc(file)));
                        else archived++;
                    }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text.AppendLine("claude sessions unreadable: " + e.GetType().Name); }
            text.AppendLine($"claude data {(root.Contains("Claude-Data", StringComparison.OrdinalIgnoreCase) ? "moved (Claude-Data)" : root.Contains("Packages", StringComparison.OrdinalIgnoreCase) ? "store app" : "roaming")}"
                + $" · folder {(Directory.Exists(sessions) ? "found" : "missing")} · session files {files}");
        }
        text.AppendLine($"claude sessions usable {entries.Count} · archived or unreadable {archived}");
        var recent = entries.Where(e => nowUtc - e.Write < TimeSpan.FromMinutes(15)).OrderByDescending(e => e.Write).Take(5)
            .Select(e => $"{e.Entry.Title.Length} chars{(new ClaudeSessionIndex().FindLog(e.Entry.CliSessionId) is null ? " (no log)" : "")}");
        var recentList = string.Join(", ", recent);
        text.AppendLine("claude sessions written in the last 15 min: " + (recentList.Length > 0 ? recentList : "none"));
        if (title is null) { text.AppendLine("claude last seen title: none"); return text.ToString(); }
        var exact = entries.Where(e => e.Entry.Title == title).Select(e => e.Entry.CliSessionId).Distinct().ToArray();
        var loose = entries.Count(e => string.Equals(e.Entry.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase));
        var prefix = entries.Count(e => e.Entry.Title.Length > 0 && e.Entry.Title != title
            && (e.Entry.Title.StartsWith(title, StringComparison.Ordinal) || title.StartsWith(e.Entry.Title, StringComparison.Ordinal)));
        text.AppendLine($"claude last seen title {title.Length} chars · same title {exact.Length} · ignoring case and spaces {loose}"
            + $" · one starts the other {prefix}{(exact.Length == 1 ? $" · log {(new ClaudeSessionIndex().FindLog(exact[0]) is null ? "missing" : "found")}" : "")}");
        return text.ToString();
    }

    // The app's title for a session, none for a session the app does not list (a terminal session, an archived one).
    public string? TitleOf(string cliSessionId)
    {
        ResolveUniqueTitle(string.Empty);
        return _files.Values.Select(v => v.Entry).FirstOrDefault(e => e?.CliSessionId == cliSessionId)?.Title;
    }

    // A split-view pane names its session only inside button labels ("<title>, rename session"):
    // the longest known title the label starts with. Uses the entries read by the last ResolveUniqueTitle.
    public string? TitleFromLabel(string label) =>
        MatchLabelTitle(_files.Values.Where(v => v.Entry is not null).Select(v => v.Entry!), label);

    internal static string? MatchLabelTitle(IEnumerable<ClaudeSessionEntry> entries, string label) =>
        entries.Select(e => e.Title)
            .Where(t => t.Length > 0 && label.Length > t.Length && label.StartsWith(t, StringComparison.Ordinal)
                && !char.IsLetterOrDigit(label[t.Length]))
            .OrderByDescending(t => t.Length).FirstOrDefault();

    internal static string? MatchUniqueTitle(IEnumerable<ClaudeSessionEntry> entries, string title)
    {
        var matches = entries.Where(e => e.Title == title).Select(e => e.CliSessionId)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public string? FindLog(string cliSessionId)
    {
        if (_logs.TryGetValue(cliSessionId, out var cached) && File.Exists(cached)) return cached;
        // The id comes from app metadata; never let it act as a path.
        if (!Guid.TryParse(cliSessionId, out _)) return null;
        try
        {
            var root = ProjectsRoot();
            if (!Directory.Exists(root)) return null;
            var found = Directory.EnumerateDirectories(root)
                .Select(directory => Path.Combine(directory, cliSessionId + ".jsonl"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (found is not null) _logs[cliSessionId] = found;
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static ClaudeSessionEntry? ReadEntry(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || (r.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True)
                || !r.TryGetProperty("cliSessionId", out var id) || id.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String)
                return null;
            return new ClaudeSessionEntry(id.GetString()!, title.GetString()!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

internal sealed record ClaudePollResult(ActiveThreadRouteStatus Route, TokenSnapshot? Snapshot, long SessionVersion)
{
    // Split view only (two or more panes); empty otherwise.
    public IReadOnlyList<ClaudePane> Panes { get; init; } = [];
}

// One split-view pane: how far its left edge is from the window's, its right edge from the rightmost pane's
// and its top from the topmost pane's.
internal sealed record ClaudePane(int LeftInset, int RightInset, string? Title, TokenSnapshot? Snapshot, bool Focused)
{
    public int TopInset { get; init; }

    public CodexWindowInfo Host(CodexWindowInfo window)
    {
        IntRect Fit(IntRect r) => new(r.X + LeftInset, r.Y + TopInset,
            Math.Max(1, r.Width - LeftInset + RightInset), Math.Max(1, r.Height - TopInset));
        return window with
        {
            WindowBounds = Fit(window.WindowBounds),
            ExtendedFrameBounds = Fit(window.ExtendedFrameBounds),
            CaptionButtonBounds = window.CaptionButtonBounds is { } c ? c with { X = c.X + RightInset, Y = c.Y + TopInset } : null
        };
    }

    internal static bool IsPaneClass(string? className) =>
        className is not null && (" " + className + " ").Contains(" dframe-pane ", StringComparison.Ordinal);
}

// Foreground Claude window → visible Code session title → local session log. Fails closed.
internal sealed class ClaudeDesktopSource
{
    internal const string CodeTitleSuffix = " - Claude Code";
    // Keeps Claude versions apart from the Codex monitors' non-negative counters.
    private const long RouteVersionBase = 1L << 40;
    private readonly ClaudeSessionIndex _index = new();
    // The last few chats shown, most recent first: going back to one reads only what was written since, not the whole log again.
    private const int KeptReaders = 6;
    private readonly List<(string Id, ClaudeTurnReader Reader)> _readers = new();
    private readonly Dictionary<string, ClaudeTurnReader> _paneReaders = new(StringComparer.Ordinal);
    private readonly TelemetryStore? _telemetry;
    private readonly WeeklyUsageMonitor? _weekly;
    public ClaudeDesktopSource(TelemetryStore? telemetry = null, WeeklyUsageMonitor? weekly = null)
    {
        _telemetry = telemetry; _weekly = weekly;
    }
    private string? _sessionId;
    private long _version;
    public string? VisibleTitle { get; private set; }

    internal static string? SessionTitleFromDocument(string? documentTitle) =>
        documentTitle is not null && documentTitle.Length > CodeTitleSuffix.Length
            && documentTitle.EndsWith(CodeTitleSuffix, StringComparison.Ordinal)
            ? documentTitle[..^CodeTitleSuffix.Length] : null;

    public ClaudePollResult Poll(CodexWindowTarget target)
    {
        string? id = null, error = null, title = null, visible = null;
        _weekly?.NotePresence(DateTimeOffset.UtcNow);
        var paneFacts = new List<(IntRect Bounds, int PaneRight, string? Title)>();
        try
        {
            var root = AutomationElement.FromHandle(target.HostWindow.Handle);
            paneFacts = ReadPanes(root);
            var documents = root.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")));
            var names = documents.Cast<AutomationElement>()
                .Where(d => !d.Current.IsOffscreen)
                .Select(d => d.Current.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal).ToArray();
            var titles = names.Select(SessionTitleFromDocument).Where(t => t is not null).Distinct().ToArray();
            visible = names.Length == 1 ? names[0] : null;
            // A popped-out session window carries the bare session title, both as window and document title.
            if (titles.Length == 0 && names.Length == 1 && root.Current.Name == names[0]) titles = [names[0]];
            if (titles.Length == 1)
            {
                title = titles[0];
                id = _index.ResolveUniqueTitle(title!);
                if (id is null) error = "현재 화면 제목과 일치하는 유일한 로컬 Claude 세션을 확인할 수 없음";
            }
            else error = names.Length == 0 ? "현재 화면 채팅 확인 대기" : "Claude Code 탭 세션이 아님 · 로컬 턴 기록 없음";
        }
        catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = "현재 화면 채팅 확인 대기";
        }

        var log = id is null ? null : _index.FindLog(id);
        if (id is not null && log is null) { error = "세션 로그를 찾을 수 없음"; id = null; }
        if (_sessionId != id) { _sessionId = id; _version++; }
        VisibleTitle = visible;
        var route = new ActiveThreadRouteStatus(id, id is null ? 0 : 1, id is not null, RouteVersionBase + _version, error);
        var snapshot = id is null ? null : Snapshot(ReaderFor(id), id, log!, title);
        return new ClaudePollResult(route, snapshot, -2 - _version) { Panes = Panes(target, paneFacts, title, snapshot) };
    }

    private ClaudeTurnReader ReaderFor(string id)
    {
        var at = _readers.FindIndex(r => r.Id == id);
        var reader = at >= 0 ? _readers[at].Reader : new ClaudeTurnReader();
        if (at >= 0) _readers.RemoveAt(at);
        _readers.Insert(0, (id, reader));
        if (_readers.Count > KeptReaders) _readers.RemoveAt(_readers.Count - 1);
        return reader;
    }

    private TokenSnapshot Snapshot(ClaudeTurnReader reader, string id, string log, string? title)
    {
        // A long log is read through in one poll rather than one 16 MB piece per poll.
        var turn = reader.Read(log);
        for (var pass = 0; turn is null && !reader.CaughtUp && pass < 15; pass++) turn = reader.Read(log);
        if (turn is { Seconds: not null } && _telemetry is not null)
            turn = turn.With(_telemetry.ClaudeTurn(reader.DisplayedRequestIds));
        // The window the session's own process settled on (its mod says), which a later /autocompact elsewhere does not move.
        var now = DateTimeOffset.UtcNow;
        var bridge = CacheWatch.BridgeOf(Path.GetFileNameWithoutExtension(log));
        var window = bridge is { Window: > 0 } b && b.Fresh(now) ? b.Window : (long?)null;
        // A ping keeps the cache without a line in the log; the mod says when it last did.
        var cache = CacheWatch.Effective(reader.Cache, bridge);
        var history = new TurnHistory(reader.Recent(TurnHistory.Size)
            .Select(r => _telemetry is null ? r.Turn : r.Turn.With(_telemetry.ClaudeTurn(r.RequestIds))).ToArray());
        return new TokenSnapshot(id, log, turn?.Total ?? 0, turn?.Input ?? 0, turn?.Cached ?? 0,
            turn?.Output ?? 0, 0, 0, 0, default) { Turn = turn, ThreadTitle = title, App = HostApp.Claude, History = history,
            HistoryFooter = CacheStatus.JoinLines(CacheStatus.Line(cache, now), _weekly?.Footer),
            CacheNote = CacheStatus.CapsuleNote(cache, now),
            CacheAlert = CacheStatus.CapsuleAlert(cache, now),
            CacheBar = CacheStatus.CapsuleBar(cache, now),
            ContextFill = ContextUsage.ClaudeFill(reader.Context, reader.LastModel, window), RingNote = ContextUsage.ClaudeNote(reader.LastModel, window),
            WorkerCalls = reader.WorkerCalls };
    }

    // A side panel (browser, preview, files…) opens inside a pane as a second full-height column at its right.
    // Returns the chat column, the pane less any such panel.
    internal static IntRect ChatColumn(IntRect pane, IEnumerable<IntRect> children)
    {
        var columns = children.Where(c => c.Height >= pane.Height * 3 / 5 && c.Width >= pane.Width / 8)
            .OrderBy(c => c.X).ToArray();
        return columns.Length >= 2 && columns[1].X > pane.X && columns[1].X < pane.Right
            ? pane with { Width = columns[1].X - pane.X } : pane;
    }

    private static IntRect ChatColumn(AutomationElement pane, IntRect bounds, CacheRequest cache)
    {
        var children = new List<IntRect>();
        var walker = TreeWalker.ControlViewWalker;
        for (var child = walker.GetFirstChild(pane, cache); child is not null; child = walker.GetNextSibling(child, cache))
        {
            var r = child.Cached.BoundingRectangle;
            if (!r.IsEmpty) children.Add(new IntRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
        }
        return ChatColumn(bounds, children);
    }

    // Split view: the document title names only the focused pane, so each pane's session comes from its header buttons.
    // One pane is listed only when a side panel narrows it; its chat is then the one in front.
    private List<(IntRect Bounds, int PaneRight, string? Title)> ReadPanes(AutomationElement root)
    {
        var result = new List<(IntRect, int, string?)>();
        var cache = new CacheRequest();
        cache.Add(AutomationElement.ClassNameProperty);
        cache.Add(AutomationElement.BoundingRectangleProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.Add(AutomationElement.NameProperty);
        using (cache.Activate())
        {
            var panes = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group))
                .Cast<AutomationElement>()
                .Where(g => ClaudePane.IsPaneClass(g.Cached.ClassName) && !g.Cached.IsOffscreen && !g.Cached.BoundingRectangle.IsEmpty)
                .ToArray();
            if (panes.Length == 0) return result;
            if (panes.Length == 1)
            {
                var r = panes[0].Cached.BoundingRectangle;
                var bounds = new IntRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                var chat = ChatColumn(panes[0], bounds, cache);
                if (chat != bounds) result.Add((chat, bounds.Right, null));
                return result;
            }
            _index.ResolveUniqueTitle(string.Empty);
            foreach (var pane in panes)
            {
                var r = pane.Cached.BoundingRectangle;
                var bounds = new IntRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                var title = pane.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>()
                    .Select(b => _index.TitleFromLabel(b.Cached.Name ?? string.Empty))
                    .FirstOrDefault(t => t is not null);
                result.Add((ChatColumn(pane, bounds, cache), bounds.Right, title));
            }
        }
        return result;
    }

    private IReadOnlyList<ClaudePane> Panes(CodexWindowTarget target, List<(IntRect Bounds, int PaneRight, string? Title)> facts,
        string? focusedTitle, TokenSnapshot? focusedSnapshot)
    {
        if (facts.Count == 0) { _paneReaders.Clear(); return []; }
        var frame = target.HostWindow.ExtendedFrameBounds;
        var right = facts.Max(f => f.PaneRight);
        var top = facts.Min(f => f.Bounds.Top);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var panes = new List<ClaudePane>();
        foreach (var (bounds, _, title) in facts)
        {
            var focused = facts.Count == 1 || (title is not null && title == focusedTitle);
            TokenSnapshot? snapshot = focused ? focusedSnapshot : null;
            if (!focused && title is not null && _index.ResolveUniqueTitle(title) is { } paneId && _index.FindLog(paneId) is { } paneLog)
            {
                used.Add(paneId);
                if (!_paneReaders.TryGetValue(paneId, out var reader)) _paneReaders[paneId] = reader = new ClaudeTurnReader();
                snapshot = Snapshot(reader, paneId, paneLog, title);
            }
            panes.Add(new ClaudePane(Math.Max(0, bounds.Left - frame.Left), Math.Min(0, bounds.Right - right), title, snapshot, focused)
                { TopInset = bounds.Top - top });
        }
        foreach (var gone in _paneReaders.Keys.Where(k => !used.Contains(k)).ToArray()) _paneReaders.Remove(gone);
        return panes;
    }
}
