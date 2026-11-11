using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenOverlay;

// Claude sessions left uncompacted with a large context whose cache is about to lapse: the next message would write
// all of it again at twice the price. Every session log written in the last hour gets a reader of its own (the shown
// session's reader is not touched), and each lapse is told once, until that session's next request.
// The partner mod (tokenoverlay-cache, inside each Claude Code session) meets the overlay in BridgeFolder: it writes
// <session>.json (alive, its last request, the last ping, a compaction) and reads <session>.ping, a request id the
// overlay writes when the person presses 핑. A ping leaves nothing in the session log, so its time comes from there.
internal sealed partial class CacheWatch : IDisposable
{
    internal const int WarnMinutes = 10;
    // Below this a lapse costs little more than a compacted session's fresh start, so it is not worth a notice.
    internal const long LargeContext = 150_000;
    // The mod writes every 30 seconds while its session's process runs.
    private static readonly TimeSpan PartnerFresh = TimeSpan.FromMinutes(2);

    internal static string BridgeFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOverlay", "cache-bridge");

    internal sealed record Warning(string Name, double MinutesLeft, long Context, string Id = "", bool Partner = false);

    // What the mod last wrote for a session. Times in the mod's milliseconds since the epoch.
    // Code: the mod's build (3 and up keep a session when asked, see KeepUntil). PingAuto: the last ping was one of those.
    // Busy: the session was answering. Compact*: the last 압축 asked from the overlay, as the mod took it up (build 4 on).
    // Attempt: the last compaction the mod tried (build 5 on), whatever asked it. Context, Compacted: the context of
    // the session's last request and whether a compaction came after it, as the mod saw them.
    internal sealed record Bridge(DateTimeOffset AliveAt, DateTimeOffset? LastAt, string Handled,
        DateTimeOffset? PingAt, bool PingOk, string? PingReason, long PingRead, long PingWrite, DateTimeOffset? CompactedAt,
        long Window = 0, int Code = 0, bool PingAuto = false, bool Busy = false,
        string CompactHandled = "", DateTimeOffset? CompactAt = null, bool CompactOk = false, Attempt? Attempt = null,
        long Context = 0, bool Compacted = false, long Threshold = 0)
    {
        // Still written by a running session, so what it says about that session's process holds.
        public bool Fresh(DateTimeOffset now) => now - AliveAt < PartnerFresh;
    }

    // By: auto (55 minutes idle), asked (압축), retry (asked by this overlay). Done: when the call answered, null while
    // it runs. Via: api ($.session.compact) or command (/compact). Ok: what the call answered, not whether it compacted.
    internal sealed record Attempt(DateTimeOffset At, string By, DateTimeOffset? Done, string? Via, bool Ok, string? Reason);

    // What became of the mod's last compaction attempt, as far as the session's log tells: none to tell of (or the log
    // has the compaction, or a request came after), still running, answered but not in the log yet, retried, failed.
    internal enum Compaction { None, Running, Checking, Retrying, Failed }

    // A large uncompacted session whose cache is still alive, for the window that pings; or one whose cache lapsed
    // after a compaction the mod tried did not reach the log, so it is not taken for compacted.
    internal sealed record Session(string Id, string Name, DateTimeOffset LapsesAt, long Context, Bridge? Bridge, bool Partner,
        Compaction Compaction = Compaction.None)
    {
        public bool Shown(DateTimeOffset now) => LapsesAt > now || Compaction != Compaction.None;
    }

    // An attempt that answered is looked for in the log this long; one that never answered, a little longer.
    internal static readonly TimeSpan ConfirmWithin = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan RunningAtMost = TimeSpan.FromMinutes(10);
    // A failed attempt is told until the session's next request, or this long.
    private static readonly TimeSpan FailedFor = TimeSpan.FromHours(12);
    // The attempt each session's retry was asked for, so it is asked once, and when.
    private readonly Dictionary<string, (DateTimeOffset Attempt, DateTimeOffset At)> _retried = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, ClaudeTurnReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _told = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClaudeSessionIndex _index = new();
    private readonly System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private IReadOnlyList<Session> _sessions = [];
    private int _busy;

    public event Action<IReadOnlyList<Warning>>? Warned;
    public bool Enabled { get; set; } = true;

    public CacheWatch(bool start = true)
    {
        if (!start) return;
        _timer = new System.Threading.Timer(_ => Scan(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
        _turnEndTimer = new System.Threading.Timer(_ => TurnEndScan(), null, TurnEndEvery, TurnEndEvery);
    }

    // Turn-end compaction (2026-11-09, the user's "압축이 제일 중요"): a session whose main turn has ended answered with
    // a context of 300k (TurnEndAt) or more is compacted then, before a next message can carry it past the engine's own threshold
    // mid-turn (68% of those happened mid-turn, KB X-3127). The overlay decides it and asks through <session>.compact,
    // which every mod from build 4 takes up within 30 seconds, so a session that kept an older mod build gets it
    // without /reload-plugins, and the rule changes with the overlay alone. Builds 8 and 9 compact 1 second after the
    // answer by themselves; build 10 leaves it to the overlay again.
    // The user's two complaints of 2026-11-10 ("보낸 말이 기다림", "일이 남았는데 압축됨"): a message sent during the
    // minute a compaction takes waited behind it, and a session that had just said it would carry on when its background
    // work ended was compacted. So the overlay waits for 2 minutes of quiet after the answer (76% of 2,201 replies since
    // 11-03 came sooner, so the person is most likely away by then), and not while the main loop has background work
    // with no end notification yet; unless the context is within 30k of the engine's own threshold, where the next turn
    // would be compacted mid-turn anyway, and the old rule holds.
    // The size was 400k (X-3127's replay) until the user lowered it to 350k on 2026-11-10 ("40만 기준은 35만으로"), and
    // to 300k the same morning ("30만으로 바꾸자").
    // Builds 8 and 9 keep their own 400k: below it the overlay asks them like any other build.
    internal const long TurnEndAt = 300_000;
    internal const long OwnTurnEndAt = 400_000;
    internal const int TurnEndOwnCode = 8;
    internal const int TurnEndAskedAgainCode = 10;
    internal const string TurnEndPrefix = "turnend-";
    internal static readonly TimeSpan TurnEndWait = TimeSpan.FromMinutes(2);
    internal const long TurnEndNearThreshold = 30_000;
    private static readonly TimeSpan TurnEndEvery = TimeSpan.FromSeconds(5);
    // Without the stop hooks' closing line (a PC with none), a turn counts as ended once its log is this quiet.
    private static readonly TimeSpan TurnEndQuiet = TimeSpan.FromSeconds(10);
    private const int TurnEndTail = 1 << 20;
    private readonly System.Threading.Timer? _turnEndTimer;
    private int _turnEndBusy;
    // The last request each session was asked about, so one turn's end is asked once.
    private readonly Dictionary<string, DateTimeOffset> _turnEndAsked = new(StringComparer.OrdinalIgnoreCase);
    // The turn each session was last held back for background work, so the log says so once a turn.
    private readonly Dictionary<string, DateTimeOffset> _turnEndHeld = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _logs = new(StringComparer.OrdinalIgnoreCase);

    private void TurnEndScan()
    {
        if (Interlocked.Exchange(ref _turnEndBusy, 1) == 1) return;
        try { TurnEnds(DateTimeOffset.Now, ClaudeSessionIndex.ProjectsRoot()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _turnEndBusy, 0); }
    }

    // Asks each running session due a turn-end compaction; returns how many were asked.
    internal int TurnEnds(DateTimeOffset now, string root, string? bridgeFolder = null)
    {
        bridgeFolder ??= BridgeFolder;
        var folder = new DirectoryInfo(bridgeFolder);
        if (!folder.Exists) return 0;
        var asked = 0;
        foreach (var report in folder.EnumerateFiles("*.json"))
        {
            if (now.UtcDateTime - report.LastWriteTimeUtc >= PartnerFresh) continue;
            var id = Path.GetFileNameWithoutExtension(report.Name);
            if (!Guid.TryParse(id, out _) || ReadBridge(report.FullName) is not { } bridge || !TurnEndDue(id, bridge, now, bridgeFolder)) continue;
            // Large (350k): the turn-end rule. Pausable (150k): the answer said the work pauses. Either on a build that
            // takes 압축 and will not compact this turn by itself (builds 8 and 9 do at 400k).
            var leftToUs = !CompactsItself(bridge.Code) || bridge.Context < OwnTurnEndAt;
            var large = bridge.Context >= TurnEndAt && leftToUs;
            var pausable = bridge.Context >= LargeContext && leftToUs;
            if ((!large && !pausable) || LogOf(root, id) is not { } log) continue;
            var near = large && NearThreshold(bridge);
            var state = TurnEndState(log, now, near ? TimeSpan.Zero : large ? TurnEndWait : TimeSpan.MaxValue, holdForWork: !near,
                pauseAtOnce: pausable, out var paused);
            if (state == TurnEnd.HeldForWork && (!_turnEndHeld.TryGetValue(id, out var held) || held != bridge.LastAt))
            {
                _turnEndHeld[id] = bridge.LastAt!.Value;
                DiagnosticLog.Info("cache", $"turn-end compaction held: background work under way (build {bridge.Code}, {bridge.Context / 1000}K)");
            }
            if (state != TurnEnd.Due) continue;
            try { Request(id, ".compact", bridgeFolder, TurnEndPrefix); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            _turnEndAsked[id] = bridge.LastAt!.Value;
            asked++;
            DiagnosticLog.Info("cache", $"compact asked at turn end (build {bridge.Code}, {bridge.Context / 1000}K, {(near ? "near the threshold, at once" : paused ? "the answer said to compact, at once" : "after 2 min quiet")})");
        }
        return asked;
    }

    // Builds 8 and 9 compact 1 second after an answer of 400k (OwnTurnEndAt) or more by themselves.
    internal static bool CompactsItself(int code) => code is >= TurnEndOwnCode and < TurnEndAskedAgainCode;

    // What the mod's report says: an uncompacted session on a build that takes 압축, not answering, with no compaction
    // tried since its last request, none asked and not yet taken up, and not asked already for this request.
    internal bool TurnEndDue(string id, Bridge bridge, DateTimeOffset now, string bridgeFolder)
    {
        if (bridge is not { Busy: false, Compacted: false, LastAt: { } last } || bridge.Code < CompactCodeNeeded || !bridge.Fresh(now)) return false;
        if (bridge.Attempt is { } tried && tried.At >= last) return false;
        if (_turnEndAsked.TryGetValue(id, out var asked) && asked == last) return false;
        var waiting = Path.Combine(bridgeFolder, id + ".compact");
        try { return !File.Exists(waiting) || File.ReadAllText(waiting).Trim() == bridge.CompactHandled; }
        catch (IOException) { return false; }
    }

    private string? LogOf(string root, string id)
    {
        if (_logs.TryGetValue(id, out var known) && File.Exists(known)) return known;
        if (!Directory.Exists(root)) return null;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(directory, id + ".jsonl");
            if (File.Exists(path)) return _logs[id] = path;
        }
        return null;
    }

    // Whether the session's main turn has ended answered, from the end of its log: going back from the last line, the
    // main loop's last word is an answer that ended the turn (stop_reason end_turn), followed by the stop hooks' closing
    // line or a quiet log; or a slash command that ran on its own (local_command). A prompt, a tool's result or any
    // other stop of the model's means a turn is under way, or ended otherwise, and nothing is asked.
    internal static bool TurnEnded(string log, DateTimeOffset now) => TurnEndState(log, now, TimeSpan.Zero, holdForWork: false, false, out _) == TurnEnd.Due;

    internal static bool NearThreshold(Bridge bridge) =>
        bridge.Threshold > 0 && bridge.Context >= bridge.Threshold - TurnEndNearThreshold;

    internal enum TurnEnd { UnderWay, Waiting, HeldForWork, Due }

    // The same, then the log quiet for `wait` (nothing written since: no new prompt, no notification), and with
    // `holdForWork` no background work the main loop started still waiting for its end notification. With
    // `pauseAtOnce`, a turn whose answer said the work pauses ("지금 /compact 하기 좋음", see Paused) waits for nothing:
    // the user's ask of 2026-11-10 ("이런 경우는 즉시 압축").
    internal static TurnEnd TurnEndState(string log, DateTimeOffset now, TimeSpan wait, bool holdForWork, bool pauseAtOnce, out bool paused)
    {
        paused = false;
        try
        {
            string[] lines;
            long start;
            using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                start = Math.Max(0, stream.Length - TurnEndTail);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                lines = reader.ReadToEnd().Split('\n');
            }
            if (!Ended(lines, start, log, now)) return TurnEnd.UnderWay;
            if (pauseAtOnce && Paused(lines)) { paused = true; wait = TimeSpan.Zero; }
            if (wait == TimeSpan.MaxValue || now.UtcDateTime - File.GetLastWriteTimeUtc(log) < wait) return TurnEnd.Waiting;
            return holdForWork && WorkUnderWay(lines) ? TurnEnd.HeldForWork : TurnEnd.Due;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return TurnEnd.UnderWay; }
    }

    // Whether this turn's answer (the main loop's words since the person's last message; a stop hook's feedback is not
    // the person's) ends a line with "지금 /compact 하기 좋음", the global rule's line for a point where work pauses.
    // Only where a line ends: in the log's JSON the phrase is followed by "\n" or the string's closing quote, while the
    // phrase quoted in a sentence is followed by an escaped quote.
    internal static bool Paused(string[] lines)
    {
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length == 0 || line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;
            if (line.Contains("\"type\":\"user\"", StringComparison.Ordinal) && !line.Contains("\"isMeta\":true", StringComparison.Ordinal)
                && !line.Contains("\"tool_result\"", StringComparison.Ordinal)) return false;
            if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) && PauseLine().IsMatch(line)) return true;
        }
        return false;
    }

    [GeneratedRegex(@"지금 /compact 하기 좋음\.?(?:\\n|"")")]
    private static partial Regex PauseLine();

    // Background work the main loop started (a command run in the background, an agent sent off, a Monitor) whose end
    // notification (<task-notification> with completed, failed, stopped or killed) is not in the log yet. Work started
    // before the part of the log read is not seen.
    internal static bool WorkUnderWay(IEnumerable<string> lines)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Contains("\"isSidechain\":true", StringComparison.Ordinal)) continue;
            foreach (Match m in WorkStarted().Matches(line)) open.Add(m.Groups["id"].Value);
            if (open.Count > 0) foreach (Match m in WorkEnded().Matches(line)) open.Remove(m.Groups["id"].Value);
        }
        return open.Count > 0;
    }

    // As written in the log's JSON: "\n" between the agent's two lines is two characters there.
    [GeneratedRegex(@"(?:running in background with ID: |Async agent launched successfully\.[^""]*?agentId: |Monitor started \(task )(?<id>[A-Za-z0-9_-]+)")]
    private static partial Regex WorkStarted();
    [GeneratedRegex(@"<task-id>(?<id>[A-Za-z0-9_-]+)</task-id>(?:(?!<task-id>).)*?<status>(?:completed|failed|stopped|killed)</status>")]
    private static partial Regex WorkEnded();

    // Going back from the last line read: see TurnEnded.
    private static bool Ended(string[] lines, long start, string log, DateTimeOffset now)
    {
        var closed = false;
        for (var i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.Length > 200_000) continue;
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) continue;
            if (r.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
            var type = t.GetString();
            if (type == "system")
            {
                var subtype = r.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (subtype == "local_command") return true;
                if (subtype == "stop_hook_summary") closed = true;
                continue;
            }
            if (type == "user")
            {
                if (r.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                return false;
            }
            if (type != "assistant") continue;
            var stop = r.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
            return stop == "end_turn" && (closed || now.UtcDateTime - File.GetLastWriteTimeUtc(log) >= TurnEndQuiet);
        }
        return false;
    }

    // As of the last scan, soonest lapse first.
    public IReadOnlyList<Session> Sessions { get { lock (_gate) return _sessions; } }

    public void ScanNow() => Scan(force: true);

    // For pictures and checks: the sessions as if a scan had found them.
    internal void ShowSessions(IReadOnlyList<Session> sessions) { lock (_gate) _sessions = sessions; }

    private void Scan(bool force = false)
    {
        // With the notice off the scan still runs where the mod does, so a compaction it tried is checked and retried.
        if (!(Enabled || force || PartnerRunning(DateTimeOffset.Now)) || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var due = Check(DateTimeOffset.Now, ClaudeSessionIndex.ProjectsRoot(), _index.TitleOf);
            if (Enabled && due.Count > 0) Warned?.Invoke(due);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    internal List<Warning> Check(DateTimeOffset now, string root, Func<string, string?> titleOf, string? bridgeFolder = null)
    {
        bridgeFolder ??= BridgeFolder;
        var live = new List<string>();
        if (Directory.Exists(root))
            foreach (var directory in Directory.EnumerateDirectories(root))
                foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
                    if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(65)
                        || File.Exists(Path.Combine(bridgeFolder, Path.GetFileNameWithoutExtension(file) + ".json"))) live.Add(file);
        foreach (var gone in _readers.Keys.Except(live, StringComparer.OrdinalIgnoreCase).ToArray()) { _readers.Remove(gone); _told.Remove(gone); }

        var due = new List<Warning>();
        var sessions = new List<Session>();
        foreach (var file in live)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var bridge = ReadBridge(Path.Combine(bridgeFolder, id + ".json"));
            // A ping keeps the cache without a line in the log: no reader is needed for an old log the mod has not pinged,
            // nor tried to compact.
            if (now.UtcDateTime - File.GetLastWriteTimeUtc(file) >= TimeSpan.FromMinutes(65)
                && !(bridge?.LastAt is { } kept && (now - kept).TotalMinutes < 60)
                && !(bridge?.Attempt is { } tried && now - tried.At < FailedFor)) continue;
            if (!_readers.TryGetValue(file, out var reader)) _readers[file] = reader = new ClaudeTurnReader();
            // A long log is read in several pieces; read on until it is caught up.
            for (var piece = 0; piece < 64; piece++) { reader.Read(file); if (reader.CaughtUp) break; }
            var state = Effective(reader.Cache, bridge);
            if (state is not { TtlMinutes: 60, Compacted: false, LastContext: >= LargeContext }
                || CacheStatus.RemainingMinutes(state, now) is not double left) continue;
            var compaction = Outcome(state, bridge, now);
            if (compaction == Compaction.Failed && Retry(id, bridge!, now, bridgeFolder)) compaction = Compaction.Retrying;
            if (left <= 0 && compaction == Compaction.None) continue;
            var name = Short(titleOf(id) ?? Path.GetFileName(reader.Folder?.TrimEnd('\\', '/')) ?? id[..Math.Min(8, id.Length)]);
            var partner = bridge?.Fresh(now) == true;
            sessions.Add(new Session(id, name, now.AddMinutes(left), state.LastContext, bridge, partner, compaction));
            if (Due(state, now) is null) continue;
            if (_told.TryGetValue(file, out var told) && told == state.LastRequest) continue;
            _told[file] = state.LastRequest!.Value;
            due.Add(new Warning(name, left, state.LastContext, id, partner));
        }
        lock (_gate) _sessions = sessions.OrderBy(s => s.LapsesAt).ToArray();
        return due;
    }

    // The log's cache facts with what only the mod knows: a ping that kept the cache. Whether the session was compacted
    // is the log's alone: the mod once reported a compaction its session's log never got (2026-10-29).
    internal static CacheState? Effective(CacheState? state, Bridge? bridge)
    {
        if (state is null || bridge is null) return state;
        if (bridge.LastAt is { } kept && (state.LastRequest is not { } logged || kept > logged)) state = state with { LastRequest = kept };
        return state;
    }

    // What became of the mod's last attempt for a session the log shows uncompacted (see Compaction).
    internal static Compaction Outcome(CacheState state, Bridge? bridge, DateTimeOffset now)
    {
        if (bridge?.Attempt is not { } a || state.Compacted || state.LastRequest is { } last && last > a.At || now - a.At >= FailedFor)
            return Compaction.None;
        var retry = a.By == "retry";
        if (a.Done is null) return now - a.At < RunningAtMost ? retry ? Compaction.Retrying : Compaction.Running : Compaction.Failed;
        if (a.Ok && now - a.At < ConfirmWithin) return retry ? Compaction.Retrying : Compaction.Checking;
        return Compaction.Failed;
    }

    // Asks the mod once to try a failed attempt again the other way. True while that retry is asked and not yet taken up.
    private bool Retry(string id, Bridge bridge, DateTimeOffset now, string bridgeFolder)
    {
        var attempt = bridge.Attempt!;
        // The mod looks every 30 seconds; much longer means its session's process is not running it.
        if (_retried.TryGetValue(id, out var asked) && asked.Attempt == attempt.At) return now - asked.At < TimeSpan.FromMinutes(2);
        // "stopped" (build 7): a session another session stopped refuses both ways until the person types in it.
        if (attempt.By == "retry" || attempt.Reason is "skipped" or "stopped" || bridge.Code < RetryCodeNeeded || !bridge.Fresh(now) || bridge.Busy) return false;
        try { RequestCompact(id, bridgeFolder, retry: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        _retried[id] = (attempt.At, now);
        DiagnosticLog.Info("cache", $"compact retried ({attempt.By}, {attempt.Via ?? "?"}, {(attempt.Ok ? "answered" : attempt.Reason ?? "failed")})");
        return true;
    }

    internal static Bridge? ReadBridge(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var r = doc.RootElement;
            static DateTimeOffset? At(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var ms) && ms > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            static long Count(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
            static string? Text(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var ping = r.TryGetProperty("ping", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
            var hasPing = ping.ValueKind == JsonValueKind.Object;
            var compact = r.TryGetProperty("compact", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
            var hasCompact = compact.ValueKind == JsonValueKind.Object;
            return new Bridge(At(r, "aliveAt") ?? DateTimeOffset.MinValue, At(r, "lastAt"),
                r.TryGetProperty("handled", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()! : "",
                hasPing ? At(ping, "at") : null,
                hasPing && ping.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                hasPing && ping.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null,
                hasPing ? Count(ping, "read") : 0, hasPing ? Count(ping, "write") : 0, At(r, "compactedAt"), Count(r, "window"),
                (int)Count(r, "code"), hasPing && ping.TryGetProperty("auto", out var auto) && auto.ValueKind == JsonValueKind.True,
                r.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
                r.TryGetProperty("compactHandled", out var ch) && ch.ValueKind == JsonValueKind.String ? ch.GetString()! : "",
                hasCompact ? At(compact, "at") : null,
                hasCompact && compact.TryGetProperty("ok", out var cok) && cok.ValueKind == JsonValueKind.True,
                r.TryGetProperty("attempt", out var a) && a.ValueKind == JsonValueKind.Object && At(a, "at") is { } tried
                    ? new Attempt(tried, Text(a, "by") ?? "", At(a, "done"), Text(a, "via"),
                        a.TryGetProperty("ok", out var aok) && aok.ValueKind == JsonValueKind.True, Text(a, "reason"))
                    : null,
                Count(r, "context"), r.TryGetProperty("compacted", out var cd) && cd.ValueKind == JsonValueKind.True,
                Count(r, "threshold"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // Asks the session's mod for one ping: it picks the request up within 30 seconds. Returns the request id.
    // The compaction window a running session measures against, as its mod last wrote it; null without one.
    internal static long? WindowOf(string sessionId, DateTimeOffset now, string? bridgeFolder = null) =>
        BridgeOf(sessionId, bridgeFolder) is { Window: > 0 } b && b.Fresh(now) ? b.Window : null;

    internal static Bridge? BridgeOf(string sessionId, string? bridgeFolder = null) =>
        Guid.TryParse(sessionId, out _) ? ReadBridge(Path.Combine(bridgeFolder ?? BridgeFolder, sessionId + ".json")) : null;

    // 유지: while <session>.keep names a time still ahead, the session's mod (build 3 and up) pings by itself 10 minutes
    // before each lapse. Turned on for 8 hours at most, so a session left on does not spend pings for days.
    internal const int KeepCodeNeeded = 3;
    // 압축: the session's mod (build 4 and up) compacts it once it is not answering, on <session>.compact.
    internal const int CompactCodeNeeded = 4;
    // A retry (`retry-<id>` in <session>.compact) is taken the other way by build 5 and up.
    internal const int RetryCodeNeeded = 5;
    // The build of the mod this overlay carries (`code` in claude-mod's register.ts).
    internal const int ModCode = 11;
    // The last build whose change is worth a /reload-plugins: an older session is told so. Build 9 only named its
    // process for the CPU line, so the hint stayed at 8 then, not to ask every open session to reload for little (the
    // user's complaint, 2026-11-09). Build 10 stops compacting 1 second after an answer, which the user asked for;
    // build 11 only drops the size from its turn-end instructions, so the hint stays at 10.
    internal const int ReloadHintCode = 10;
    internal static readonly TimeSpan KeepFor = TimeSpan.FromHours(8);

    internal static DateTimeOffset? KeepUntil(string sessionId, DateTimeOffset now, string? bridgeFolder = null)
    {
        try
        {
            var path = Path.Combine(bridgeFolder ?? BridgeFolder, sessionId + ".keep");
            if (!Guid.TryParse(sessionId, out _) || !File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("until", out var v) && v.TryGetInt64(out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(ms) is var until && until > now ? until : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void SetKeep(string sessionId, bool on, DateTimeOffset now, string? bridgeFolder = null)
    {
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("not a session id", nameof(sessionId));
        bridgeFolder ??= BridgeFolder;
        var path = Path.Combine(bridgeFolder, sessionId + ".keep");
        if (!on) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(bridgeFolder);
        File.WriteAllText(path, JsonSerializer.Serialize(new { until = (now + KeepFor).ToUnixTimeMilliseconds() }));
    }

    // Whether the mod runs in any Claude session on this PC: each one rewrites its report every 30 seconds, small or not.
    internal static bool PartnerRunning(DateTimeOffset now, string? bridgeFolder = null)
    {
        try
        {
            var folder = new DirectoryInfo(bridgeFolder ?? BridgeFolder);
            return folder.Exists && folder.EnumerateFiles("*.json").Any(f => now.UtcDateTime - f.LastWriteTimeUtc < PartnerFresh);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string RequestPing(string sessionId, string? bridgeFolder = null) => Request(sessionId, ".ping", bridgeFolder);

    // Asks the session's mod to compact it: taken up within 30 seconds, or once a reply under way ends. A retry is
    // taken the other way than the attempt before it.
    internal static string RequestCompact(string sessionId, string? bridgeFolder = null, bool retry = false) =>
        Request(sessionId, ".compact", bridgeFolder, retry ? "retry-" : "");

    private static string Request(string sessionId, string extension, string? bridgeFolder, string prefix = "")
    {
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("not a session id", nameof(sessionId));
        bridgeFolder ??= BridgeFolder;
        Directory.CreateDirectory(bridgeFolder);
        var request = prefix + Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(bridgeFolder, sessionId + extension), request);
        return request;
    }

    // Minutes left when the session should be told: an hour-long cache, not compacted, large, and lapsing soon.
    internal static double? Due(CacheState? state, DateTimeOffset now) =>
        state is { TtlMinutes: 60, Compacted: false, LastContext: >= LargeContext }
        && CacheStatus.RemainingMinutes(state, now) is double left && left > 0 && left <= WarnMinutes ? left : null;

    private static string Short(string name) => name.Length <= 24 ? name : name[..23] + "…";

    // Tray notice: one line per session (three at most), then what to do. With the mod in those sessions a click opens
    // the window that pings, and the mod compacts a session left alone 5 minutes before its cache lapses.
    internal static (string Title, string Text) Notice(IReadOnlyList<Warning> due)
    {
        var lines = due.OrderBy(w => w.MinutesLeft).Take(3)
            .Select(w => $"{w.Name} · {Math.Ceiling(w.MinutesLeft):0}분 뒤 {OverlayPresentationBuilder.FormatContextCount(w.Context)} 다시 저장");
        var more = due.Count > 3 ? $"\n그 밖에 {due.Count - 3}개" : "";
        var help = due.Any(w => w.Partner)
            ? "\n눌러서 핑할 세션 고르기 · 그냥 두면 만료 5분 전 자동 압축"
            : "\n자리를 비울 거면 그 세션에서 /compact";
        return ("Claude 캐시 곧 만료 · 압축 안 한 세션", string.Join("\n", lines) + more + help);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _turnEndTimer?.Dispose();
    }
}
