using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// One response the session log counted: when its token record was written, how much output it added, and when the
// log wrote the last thing the model produced for it (reasoning, message or tool call), if it wrote one.
internal readonly record struct CountedResponse(DateTimeOffset At, long Output, DateTimeOffset? LastItem = null);

internal sealed record TurnStatistics(string TurnId, long? Input, long? Output, long? Cached,
    double? Seconds, string State, bool Running, string? Problem)
{
    public string FastMode { get; init; } = "unknown";
    // A request in this turn wrote more than half its context to the cache: the cache was rebuilt (lapsed, model switch, ...).
    public bool Restored { get; init; }
    // Model id as the session log names it; "+" appended when the turn used more than one.
    public string? Model { get; init; }
    public long? Total => Input + Output;
    public double? TokensPerSecond => Seconds > 0 && Output.HasValue ? Output / Seconds : null;
    // Sum of this turn's model request times from local telemetry; null until every request is collected.
    public double? ModelSeconds { get; init; }
    public int ModelRequests { get; init; }
    public string? ModelNote { get; init; }
    public double? ModelWorstOffsetMs { get; init; }
    public double? ModelTokensPerSecond => ModelSeconds > 0 && Output > 0 ? Output / ModelSeconds : null;
    // The same requests less each one's wait for the first piece; null when a wait was not recorded.
    public double? PureSeconds { get; init; }
    public string? PureNote { get; init; }
    public double? PureTokensPerSecond => PureSeconds > 0 && Output > 0 ? Output / PureSeconds : null;
    public TurnStatistics With(ModelRate rate) =>
        this with { ModelSeconds = rate.Seconds, ModelRequests = rate.Requests, ModelNote = rate.Note, ModelWorstOffsetMs = rate.WorstOffsetMs,
            PureSeconds = rate.PureSeconds, PureNote = rate.PureNote };
}

// Incremental, read-only JSONL reader. Only complete lines advance the state machine.
// A partial UTF-8 line is retained until its newline arrives. No conversation text is stored.
internal sealed class TurnStatisticsReader
{
    private string? _path;
    internal string? LogPath => _path;
    private long _offset;
    private readonly MemoryStream _line = new();
    private readonly byte[] _buffer = new byte[1 << 20];
    // False while a long log is still being read through: nothing from it is shown until the end is reached.
    internal bool CaughtUp { get; private set; } = true;
    internal int ReadSliceMs { get; set; } = 300;
    private readonly Dictionary<string, Turn> _turns = new(StringComparer.Ordinal);
    private Usage? _last;
    private Turn? _active;
    private Turn? _finished;
    private bool _parseFailure;
    private string? _sessionId;
    private bool? _appliedFast;
    // Start of the turn the last Snapshot() describes, for matching telemetry to that same turn.
    internal DateTimeOffset? DisplayedStarted { get; private set; }
    internal IReadOnlyList<CountedResponse> DisplayedResponses { get; private set; } = [];
    internal string? SessionId => _sessionId;
    // The session's running totals: Codex counts cached input inside input. No lifetime is counted (see CacheState).
    internal CacheState? Cache => CaughtUp && _last is { Input: > 0 } last
        ? new CacheState(last.Input, last.Cached, _lastRequestAt, null, _lastContext, HitOnly: true, LastCached: _lastCached) : null;
    private DateTimeOffset? _lastRequestAt;
    private long _lastContext;
    private long _lastCached;
    // Each request read since the last call, for the cache record (CodexCacheLog).
    private readonly List<CodexRequest> _requests = new();
    internal IReadOnlyList<CodexRequest> TakeRequests()
    {
        var taken = _requests.ToArray();
        _requests.Clear();
        return taken;
    }
    // When the log last wrote something the model produced; the next token record closes that response.
    private DateTimeOffset? _lastOutputAt;
    private readonly List<Turn> _order = new();
    private bool _sawSettings;
    private sealed record Usage(long Input, long Output, long Cached);
    private sealed class Turn(string id, DateTimeOffset? started, Usage? baseline)
    {
        public string Id = id;
        public DateTimeOffset? Started = started;
        public Usage? Baseline = baseline;
        public Usage? End;
        public double? Seconds;
        public string State = "진행 중";
        public bool Closed;
        public string? Problem;
        public bool SawFast, SawStandard, SawUnknown;
        public string? Model;
        // Each response the log counted for this turn: when its token record was written and how much output it added.
        public readonly List<CountedResponse> Responses = new();
        // Started before this chat logged any applied settings (Codex records none ahead of a new chat's first turn).
        public bool AwaitingFirstSettings;
        public void ObserveFast(bool? mode)
        {
            if (mode == true) SawFast = true;
            else if (mode == false) SawStandard = true;
            else SawUnknown = true;
        }
        public string FastMode => SawFast && SawStandard ? "mixed" : SawUnknown ? "unknown"
            : SawFast ? "on" : SawStandard ? "off" : "unknown";
    }

    public TurnStatistics? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (_path != path || file.Length < _offset)
            {
                _path = path; _offset = 0; _line.SetLength(0); _turns.Clear(); _order.Clear(); _sawSettings = false;
                CaughtUp = false;
                _last = null; _active = null; _finished = null; _parseFailure = false;
                _sessionId = null; _appliedFast = null; _lastRequestAt = null; _lastContext = 0; _lastCached = 0; _lastOutputAt = null;
                _requests.Clear();
                if (file.Length > LargeLogBytes)
                {
                    // A chat logged for weeks reaches many gigabytes (one of 14 GB seen 2026-10-20); reading it from the
                    // start took minutes. Only the chat's id is taken from its first line, then reading starts a couple of
                    // turns before the shown ones (the oldest turn read lacks its starting totals and is never shown).
                    // Settings recorded before that point are not seen, so those turns' speed tier reads as unknown.
                    if (FirstLine(file) is { } first) ProcessLine(first);
                    _sawSettings = true;
                    _offset = RecentTurnsStart(file, TurnHistory.Size + 2);
                }
            }
            file.Position = _offset;
            // Logs reach hundreds of megabytes, so one call reads for a short slice of time and the next continues.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var read = file.Read(_buffer, 0, _buffer.Length);
                if (read == 0) break;
                _offset += read;
                var rest = _buffer.AsSpan(0, read);
                for (var end = rest.IndexOf((byte)'\n'); end >= 0; end = rest.IndexOf((byte)'\n'))
                {
                    if (_line.Length == 0) ProcessBytes(rest[..end]);
                    else
                    {
                        _line.Write(rest[..end]);
                        ProcessBytes(_line.GetBuffer().AsSpan(0, (int)_line.Length));
                        _line.SetLength(0);
                    }
                    rest = rest[(end + 1)..];
                }
                _line.Write(rest);
            } while (clock.ElapsedMilliseconds < ReadSliceMs);
            CaughtUp = _offset >= file.Length;
            if (!CaughtUp) return null; // never present a partially scanned history as latest
            return Snapshot();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TurnStatistics("", null, null, null, null, "로그 읽기 대기", false, "파일 접근 대기");
        }
    }

    internal static long LargeLogBytes { get; set; } = 256L * 1024 * 1024;
    // How far back a large log is searched for its recent turns before reading from that point regardless.
    internal static long LargeLogSearchBytes { get; set; } = 1024L * 1024 * 1024;

    private static string? FirstLine(FileStream file)
    {
        file.Position = 0;
        var buffer = new byte[Math.Min(file.Length, 1 << 20)];
        var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var end = buffer.AsSpan(0, read).IndexOf((byte)'\n');
        return end > 0 ? Encoding.UTF8.GetString(buffer, 0, end) : null;
    }

    // Where the line of the count-th latest turn start begins, searched backwards from the end of the log.
    // Only a record's own type counts: the marker must sit in the head of a line that is a log record.
    internal static long RecentTurnsStart(FileStream file, int count)
    {
        const int block = 4 << 20, overlap = 64 << 10;
        byte[][] markers = ["\"payload\":{\"type\":\"task_started\""u8.ToArray(), "\"payload\":{\"type\":\"turn_started\""u8.ToArray()];
        var starts = new SortedSet<long>();
        var buffer = new byte[block];
        var floor = Math.Max(0, file.Length - LargeLogSearchBytes);
        var end = file.Length;
        while (end > floor && starts.Count < count)
        {
            var start = Math.Max(floor, end - block);
            file.Position = start;
            var read = file.ReadAtLeast(buffer, (int)(end - start), throwOnEndOfStream: false);
            var span = buffer.AsSpan(0, read);
            foreach (var marker in markers)
                for (var from = 0; ;)
                {
                    var hit = span[from..].IndexOf(marker);
                    if (hit < 0) break;
                    hit += from; from = hit + marker.Length;
                    var lineStart = span[..hit].LastIndexOf((byte)'\n') + 1;
                    // A line begun before this block is judged in the earlier block, which overlaps this one.
                    if (lineStart == 0 && start > 0) continue;
                    if (hit - lineStart > 256 || !span[lineStart..].StartsWith("{\"timestamp\":"u8)) continue;
                    starts.Add(start + lineStart);
                }
            if (start == floor) break;
            end = start + overlap;
        }
        if (starts.Count >= count || floor == 0) return starts.Count == 0 ? 0 : starts.Reverse().Take(count).Min();
        // Fewer turns than wanted within the searched stretch: begin at its first line, or the earliest start found.
        if (starts.Count > 0) return starts.Min;
        file.Position = floor;
        var tail = new byte[Math.Min(block, file.Length - floor)];
        var got = file.ReadAtLeast(tail, tail.Length, throwOnEndOfStream: false);
        var newline = tail.AsSpan(0, got).IndexOf((byte)'\n');
        return newline < 0 ? file.Length : floor + newline + 1;
    }

    private void ProcessBytes(ReadOnlySpan<byte> line)
    {
        // Model output records carry long text; only their time is needed, and it is read from the line's head.
        if (OutputItemAt(line) is DateTimeOffset at) { _lastOutputAt = at; return; }
        if (Relevant(line)) ProcessLine(Encoding.UTF8.GetString(line));
    }

    // What the model itself writes into a response. Tool results, user/developer messages and messages from
    // other agents arrive on their own schedule and say nothing about when the model stopped.
    private static readonly string[] ModelOutputKinds =
        ["reasoning", "function_call", "custom_tool_call", "local_shell_call", "web_search_call", "message"];

    // The time of a response_item line the model produced, read from the first bytes of the line; null otherwise.
    internal static DateTimeOffset? OutputItemAt(ReadOnlySpan<byte> line)
    {
        var head = line[..Math.Min(line.Length, 320)];
        var prefix = "{\"timestamp\":\""u8;
        if (!head.StartsWith(prefix)) return null;
        var marker = "\"type\":\"response_item\",\"payload\":{\"type\":\""u8;
        var at = head.IndexOf(marker);
        if (at < 0 || head[prefix.Length..at].IndexOf((byte)'{') >= 0) return null;
        var kindStart = head[(at + marker.Length)..];
        var close = kindStart.IndexOf((byte)'"');
        if (close < 0) return null;
        var kind = Encoding.UTF8.GetString(kindStart[..close]);
        if (!ModelOutputKinds.Contains(kind)) return null;
        if (kind == "message" && kindStart.IndexOf("\"role\":\"assistant\""u8) < 0) return null;
        var stamp = head[prefix.Length..];
        var end = stamp.IndexOf((byte)'"');
        return end > 0 && DateTimeOffset.TryParse(Encoding.UTF8.GetString(stamp[..end]), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) ? time : null;
    }

    // Nearly all of a Codex log is message and tool output this reader never uses. A line is left unparsed only
    // when its own head names a record this reader ignores; anything shaped differently is parsed as before.
    internal static bool Relevant(ReadOnlySpan<byte> line)
    {
        var head = line[..Math.Min(line.Length, 256)];
        var at = head.IndexOf("\"type\":\""u8);
        // The first "type" is the record's own only if no nested object opens before it.
        if (at < 1 || head[1..at].IndexOf((byte)'{') >= 0) return true;
        var name = head[(at + 8)..];
        var close = name.IndexOf((byte)'"');
        if (close < 0) return true;
        var type = name[..close];
        if (type.SequenceEqual("session_meta"u8) || type.SequenceEqual("turn_context"u8)) return true;
        if (!type.SequenceEqual("event_msg"u8)) return false;
        var opening = "\",\"payload\":{\"type\":\""u8;
        var rest = name[close..];
        if (!rest.StartsWith(opening)) return true;
        var kind = rest[opening.Length..];
        close = kind.IndexOf((byte)'"');
        if (close < 0) return true;
        kind = kind[..close];
        return kind.SequenceEqual("token_count"u8) || kind.SequenceEqual("thread_settings_applied"u8)
            || kind.SequenceEqual("task_started"u8) || kind.SequenceEqual("turn_started"u8)
            || kind.SequenceEqual("task_complete"u8) || kind.SequenceEqual("turn_completed"u8)
            || kind.SequenceEqual("turn_aborted"u8);
    }

    internal void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("payload", out var p)) return;
            var rowType = Text(root, "type");
            if (rowType == "session_meta") { _sessionId = Text(p, "id"); return; }
            if (rowType == "turn_context")
            {
                var contextId = Text(p, "turn_id");
                if (contextId is not null && _turns.TryGetValue(contextId, out var contextTurn) && !contextTurn.Closed)
                {
                    contextTurn.ObserveFast(_appliedFast);
                    contextTurn.Model = Text(p, "model") ?? contextTurn.Model;
                }
                return;
            }
            if (rowType == "response_item")
            {
                if (OutputItemAt(Encoding.UTF8.GetBytes(line)) is DateTimeOffset at) _lastOutputAt = at;
                return;
            }
            if (rowType != "event_msg") return;
            var type = Text(p, "type");
            if (type == "thread_settings_applied")
            {
                // Applied settings belong to this session. Never read the current global/UI toggle.
                if (_sessionId is null || Text(p, "thread_id") != _sessionId) return;
                _appliedFast = p.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object
                    ? Text(settings, "service_tier") switch { "priority" or "fast" => true, "default" => false, _ => null }
                    : null;
                if (!_sawSettings)
                {
                    // The chat's first settings record is the only evidence for the turns before it. It is taken as
                    // their tier (an inference: a toggle between those turns and this record would go unnoticed).
                    // Later records never relabel earlier turns.
                    _sawSettings = true;
                    foreach (var earlier in _order.Where(t => t.AwaitingFirstSettings))
                    {
                        earlier.AwaitingFirstSettings = false;
                        if (_appliedFast is not bool initial || earlier.SawFast || earlier.SawStandard) continue;
                        earlier.SawUnknown = false;
                        earlier.ObserveFast(initial);
                    }
                }
            }
            else if (type is "task_started" or "turn_started")
            {
                var id = Text(p, "turn_id");
                if (string.IsNullOrEmpty(id) || _turns.ContainsKey(id)) return;
                if (_active is { Closed: false })
                {
                    _active.Closed = true; _active.State = "종료 기록 없음";
                    _active.Problem = "종료 기록 없음"; _finished = _active;
                }
                _active = new Turn(id, Timestamp(root, p, "started_at"), _last ?? new Usage(0, 0, 0));
                _active.ObserveFast(_appliedFast);
                _active.AwaitingFirstSettings = !_sawSettings;
                if (_parseFailure) _active.Problem = "손상된 기록 포함";
                _turns[id] = _active;
                _order.Add(_active);
            }
            else if (type == "token_count")
            {
                if (!p.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("total_token_usage", out var total)
                    || !total.TryGetProperty("input_tokens", out var input)
                    || !total.TryGetProperty("output_tokens", out var output)) return;
                var value = new Usage(input.GetInt64(), output.GetInt64(), Number(total, "cached_input_tokens") ?? 0);
                var lastOutputAt = _lastOutputAt;
                _lastOutputAt = null;
                var explicitId = Text(p, "turn_id");
                var owner = explicitId is not null ? _turns.GetValueOrDefault(explicitId) : _active;
                if (explicitId is not null && _active is not null && owner != _active)
                {
                    // A late counter crossing a new turn boundary cannot safely fix its baseline.
                    _active.Problem = "턴 경계를 넘긴 지연 기록";
                    return;
                }
                // A chat continued in a second file keeps counting from its first file, so the file's first turn
                // starts where that count stood before this request: the running total less the request's own use.
                if (_last is null && owner is { End: null } && owner.Baseline == new Usage(0, 0, 0)
                    && info.TryGetProperty("last_token_usage", out var firstUsage) && firstUsage.ValueKind == JsonValueKind.Object)
                {
                    owner.Baseline = new Usage(
                        Math.Max(0, value.Input - (Number(firstUsage, "input_tokens") ?? 0)),
                        Math.Max(0, value.Output - (Number(firstUsage, "output_tokens") ?? 0)),
                        Math.Max(0, value.Cached - (Number(firstUsage, "cached_input_tokens") ?? 0)));
                }
                if (_last is not null && (value.Input < _last.Input || value.Output < _last.Output))
                {
                    if (owner is not null) owner.Problem = "누적 카운터 변경 · 집계 불완전";
                }
                if (owner is not null && _last is not null && value.Output > _last.Output
                    && DateTimeOffset.TryParse(Text(root, "timestamp"), out var countedAt))
                {
                    owner.Responses.Add(new(countedAt, value.Output - _last.Output, lastOutputAt));
                }
                // More input counted means a request reached the model and used or refreshed the cache.
                if (value.Input > (_last?.Input ?? 0) && DateTimeOffset.TryParse(Text(root, "timestamp"), out var requestAt))
                {
                    var previousAt = _lastRequestAt;
                    var previousContext = _lastContext;
                    _lastRequestAt = requestAt;
                    // That request's whole input and the part of it read from the cache.
                    var hasLast = info.TryGetProperty("last_token_usage", out var lastUsage) && lastUsage.ValueKind == JsonValueKind.Object;
                    _lastContext = hasLast ? Number(lastUsage, "input_tokens") ?? 0 : 0;
                    _lastCached = hasLast ? Number(lastUsage, "cached_input_tokens") ?? 0 : 0;
                    if (_lastContext > 0)
                        _requests.Add(new(requestAt, _sessionId, owner?.Model, previousAt is { } before ? (requestAt - before).TotalMinutes : null,
                            previousContext, _lastContext, _lastCached));
                }
                _last = value;
                // A fresh running total: the next turn's starting point no longer depends on a damaged line.
                _parseFailure = false;
                if (owner is not null)
                {
                    owner.End = value;
                    if (owner.Closed && !IsEmptyAbort(owner)) _finished = owner;
                }
            }
            else if (type is "task_complete" or "turn_completed" or "turn_aborted")
            {
                var id = Text(p, "turn_id");
                var target = id is null ? _active : _turns.GetValueOrDefault(id);
                if (target is null || target.Closed) return;
                target.Closed = true;
                target.State = type == "turn_aborted" ? "중단" : "완료";
                var ms = Number(p, "duration_ms");
                var end = Timestamp(root, p, "completed_at");
                target.Seconds = ms.HasValue ? ms.Value / 1000d
                    : end.HasValue && target.Started.HasValue ? (end.Value - target.Started.Value).TotalSeconds : null;
                if (target.Seconds <= 0) target.Problem = "유효한 소요 시간 없음";
                // A turn stopped before any response finished used no tokens: it is not a result worth showing,
                // so the previous completed turn stays on display.
                if (!IsEmptyAbort(target) || _finished is null) _finished = target;
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            _parseFailure = true;
            _appliedFast = null;
            if (_active is not null) _active.Problem = "손상된 기록 포함";
        }
    }

    internal TurnStatistics? Snapshot()
    {
        var t = _finished ?? _active;
        DisplayedStarted = t?.Started;
        DisplayedResponses = t is null ? [] : t.Responses.ToArray();
        return t is null ? null : Describe(t, _active is { Closed: false });
    }

    // Completed turns, newest first, each with its start time for matching telemetry.
    internal IReadOnlyList<(TurnStatistics Turn, IReadOnlyList<CountedResponse> Responses)> Recent(int count) =>
        !CaughtUp ? [] : _order.Where(t => t.Closed && !IsEmptyAbort(t)).Reverse().Take(count)
            .Select(t => (Describe(t, false), (IReadOnlyList<CountedResponse>)t.Responses.ToArray())).ToArray();

    private static bool IsEmptyAbort(Turn t) => t.State == "중단" && t.Problem is null
        && (t.End is null || t.Baseline is null || (t.End.Output == t.Baseline.Output && t.End.Input == t.Baseline.Input));

    private static TurnStatistics Describe(Turn t, bool running)
    {
        var valid = t.Problem is null && t.End is not null && t.Baseline is not null;
        return new TurnStatistics(t.Id,
            valid ? t.End!.Input - t.Baseline!.Input : null,
            valid ? t.End!.Output - t.Baseline!.Output : null,
            valid ? t.End!.Cached - t.Baseline!.Cached : null,
            t.Seconds,
            t.Problem is not null ? "기록 불완전" : t.End is null ? "집계 대기" : t.State,
            running, t.Problem) { FastMode = t.FastMode, Model = t.Model };
    }

    private static string? Text(JsonElement p, string key) => p.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Number(JsonElement p, string key) => p.TryGetProperty(key, out var v)
        && v.TryGetInt64(out var n) ? n : null;
    private static DateTimeOffset? Timestamp(JsonElement root, JsonElement p, string key)
    {
        // Millisecond event timestamps preserve precision when duration_ms is unavailable.
        if (DateTimeOffset.TryParse(Text(root, "timestamp"), out var date)) return date;
        var unix = Number(p, key);
        return unix.HasValue ? DateTimeOffset.FromUnixTimeSeconds(unix.Value) : null;
    }
}

// Recent completed turns, newest first. Compared by content so an unchanged list does not look like new data.
internal sealed record TurnHistory(IReadOnlyList<TurnStatistics> Turns)
{
    public const int Size = 5;
    public bool Equals(TurnHistory? other) => other is not null && Turns.SequenceEqual(other.Turns);
    public override int GetHashCode() => Turns.Count;
}

internal sealed record HistoryRow(string When, string Tokens, string Seconds, string Rate, string Model, string ModelName = "—",
    string Cache = "—");

internal static class ModelNames
{
    // "claude-opus-5-5" → "Opus 5.5", "claude-haiku-4-5-20251001" → "Haiku 4.5", "gpt-6-astra" → "6 Astra".
    public static string? Short(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var several = id.EndsWith('+');
        var name = id.TrimEnd('+');
        var bracket = name.IndexOf('[');
        if (bracket > 0) name = name[..bracket];
        foreach (var prefix in new[] { "claude-", "gpt-" })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !(part.Length == 8 && part.All(char.IsDigit))).ToArray();
        var text = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var number = parts[i].All(char.IsDigit);
            if (i > 0) text.Append(number && parts[i - 1].All(char.IsDigit) ? '.' : ' ');
            text.Append(number ? parts[i] : char.ToUpperInvariant(parts[i][0]) + parts[i][1..]);
        }
        return text.Length == 0 ? null : text + (several ? "+" : "");
    }
}

internal static class TurnPresentation
{
    // The settings' choice of rate lines. The cache note keeps its row (an empty rate line) when both are off, and a
    // line switched off lets the one under it move up. The worker's cache hit line and provider are switched on their own.
    // The rate lines (TPS, 모델, 생성) always show, so the capsule keeps one shape; only the worker's extras can be left out.
    internal static OverlayPresentation ChooseLines(OverlayPresentation p, bool cache = true, bool provider = true) =>
        p with { WorkerCacheLine = cache ? p.WorkerCacheLine : null, WorkerProvider = provider ? p.WorkerProvider : null };

    internal static IReadOnlyList<HistoryRow> HistoryRows(TurnHistory? history) => history is null ? [] : history.Turns
        .Select((turn, index) => new HistoryRow(
            $"{index + 1}턴 전",
            turn.Total is long total ? OverlayPresentationBuilder.FormatTokenCount(total) : "—",
            turn.Seconds is double seconds ? OverlayPresentationBuilder.FormatDuration(seconds) : "—",
            // Same Fast mark as the capsule: each row shows the mode that turn itself ran with.
            turn.TokensPerSecond is double rate
                ? (turn.FastMode switch { "on" => "⚡ ", "mixed" => "⚡* ", _ => "" }) + rate.ToString("0.00") : "—",
            turn.ModelTokensPerSecond?.ToString("0.00") ?? "—",
            ModelNames.Short(turn.Model) ?? "—",
            (turn.Restored ? "↻ " : "") + (CacheStatus.Percent(turn.Cached, turn.Input) ?? "—"))).ToArray();

    private static string Count(long? n) => n?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    public static OverlayPresentation Create(TokenSnapshot? snapshot)
    {
        var t = snapshot?.Turn;
        OverlayMetric Metric(DisplayField field, string compact, string label, string value) =>
            new(field, compact, label, value, value != "—");
        var total = Metric(DisplayField.Total, t?.Running == true ? "직전" : "소모", "소모 토큰", t?.Total is long n
            ? OverlayPresentationBuilder.FormatTokenCount(n) : "—");
        var speedLabel = t?.FastMode switch { "on" => "⚡ TPS", "mixed" => "⚡* TPS", _ => "TPS" };
        var speed = Metric(DisplayField.ContextPercent, speedLabel, "출력 토큰/초", t?.TokensPerSecond?.ToString("0.00") ?? "—")
            with { LabelAfterValue = true };
        var rows = new List<OverlayMetric>
        {
            Metric(DisplayField.Thread, "", "채팅", snapshot?.ThreadTitle ?? "현재 채팅 확인 중"),
            Metric(DisplayField.Thread, "", "상태", t is null ? "집계 대기" : t.Running && t.Seconds.HasValue ? "진행 중 · 직전 완료 턴" : t.State),
            Metric(DisplayField.Thread, "", "Fast 설정", t?.FastMode switch {
                "on" => "⚡ ON", "off" => "OFF", "mixed" => "⚡* 혼합 · 턴 도중 변경", _ => "확인 불가" }),
            Metric(DisplayField.Total, "", "전체 소모 토큰", Count(t?.Total)),
            Metric(DisplayField.Input, "", "입력 토큰", Count(t?.Input)),
            Metric(DisplayField.CacheHit, "", "입력 중 캐시", Count(t?.Cached)),
            Metric(DisplayField.Output, "", "출력 토큰", Count(t?.Output)),
            Metric(DisplayField.Thread, "", "소요 시간", t?.Seconds is double seconds ? OverlayPresentationBuilder.FormatDuration(seconds) : "—"),
            Metric(DisplayField.Thread, "", "", t?.TokensPerSecond is double rate
                ? $"{Count(t.Output)} ÷ {OverlayPresentationBuilder.FormatDuration(t.Seconds ?? 0)} = {rate:0.00} TPS" : t?.Problem ?? "완료 기록을 기다리는 중"),
            Metric(DisplayField.Thread, "", "", "도구·대기 포함 · 본 에이전트 · 로그 기준"),
            Metric(DisplayField.Thread, "", "모델 (도구 제외)", t?.ModelTokensPerSecond is double modelRate ? $"{modelRate:0.00} TPS" : "—"),
            Metric(DisplayField.Thread, "", "", t?.ModelTokensPerSecond is double
                ? $"{Count(t.Output)} ÷ 요청 {t.ModelRequests}건 {OverlayPresentationBuilder.FormatDuration(t.ModelSeconds ?? 0)} · 도구 대기 제외 · 첫 응답 대기 포함"
                : t?.ModelNote ?? ModelRate.Waiting)
        };
        if (snapshot?.App is HostApp.Claude or HostApp.Codex)
        {
            rows.Add(Metric(DisplayField.Thread, "", "생성 (대기 제외)", t?.PureTokensPerSecond is double pureRate ? $"{pureRate:0.00} TPS" : "—"));
            rows.Add(Metric(DisplayField.Thread, "", "", t?.PureTokensPerSecond is double
                ? $"{Count(t.Output)} ÷ {OverlayPresentationBuilder.FormatDuration(t.PureSeconds ?? 0)} · 요청마다 첫 응답 대기 제외"
                : t?.PureNote ?? ModelRate.Waiting));
        }
        var status = t is null ? "현재 채팅 · 집계 대기" : !t.Seconds.HasValue ? "작업 중 · 완료 후 표시" : null;
        return new OverlayPresentation(total, speed, rows, 0, false, status)
        {
            // Second and third lines: same turn as the first, never a substitute value. 모델: the model requests with
            // their wait for the first piece; 생성: generating time only.
            SubLine = status is not null ? null
                : t?.ModelTokensPerSecond is double shown ? $"모델 {shown:0.00} TPS" : "모델 —",
            SubLine2 = status is not null || snapshot?.App is not (HostApp.Claude or HostApp.Codex) ? null
                : t?.PureTokensPerSecond is double pure ? $"생성 {pure:0.00} TPS" : "생성 —",
            SubLineLeft = status is null ? snapshot?.CacheNote : null,
            SubLineLeftAlert = snapshot?.CacheAlert == true,
            SubLineLeftBar = status is null ? snapshot?.CacheBar : null,
            ContextFill = status is null ? snapshot?.ContextFill : null,
            RingNote = status is null ? snapshot?.RingNote : null,
            WorkerLine = status is null ? snapshot?.DeepSeek?.Line : null,
            WorkerSubLine = status is null ? snapshot?.DeepSeek?.TtftLine : null,
            WorkerThirdLine = status is null ? snapshot?.DeepSeek?.PureLine : null,
            WorkerCacheLine = status is null ? snapshot?.DeepSeek?.CacheLine : null,
            WorkerProvider = status is null ? snapshot?.DeepSeek?.ProviderLines : null,
            CompactMiddle = Metric(DisplayField.Thread, "", "소요 시간", t?.Seconds is double elapsed ? OverlayPresentationBuilder.FormatDuration(elapsed) : "—"),
            App = snapshot?.App ?? HostApp.None,
            History = HistoryRows(snapshot?.History),
            HistoryFooter = snapshot?.HistoryFooter,
            Running = t?.Running == true
        };
    }
}

internal sealed class ThreadTitleReader(string sessionRoot)
{
    private DateTime _write;
    private readonly Dictionary<string, string> _names = new();
    // Titles from Codex's state database, which also holds renames; they win over the index file's.
    private Dictionary<string, string> _stateTitles = new();
    private long? _stateReadAt;
    private const int StateRereadMs = 3000;
    public string? ResolveUniqueTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        Read("");
        if (MatchUniqueTitle(Merged(), title) is { } id) return id;
        // Unknown title: a chat newer than the last database read, or renamed since. Read again, at most every 3 s.
        if (!StateDue()) return null;
        ReadState();
        return MatchUniqueTitle(Merged(), title);
    }
    private bool StateDue() => _stateReadAt is not long at || Environment.TickCount64 - at >= StateRereadMs;
    private void ReadState()
    {
        _stateReadAt = Environment.TickCount64;
        if (CodexThreadTitles.Read(Directory.GetParent(sessionRoot)!.FullName) is { } titles) _stateTitles = titles;
    }
    private Dictionary<string, string> Merged()
    {
        var merged = new Dictionary<string, string>(_names, StringComparer.Ordinal);
        foreach (var pair in _stateTitles) merged[pair.Key] = pair.Value;
        return merged;
    }
    // Exact title first. A long title is cut on screen and ends in "…": then the one chat whose title starts with
    // the visible part. Two candidates either way: no match, never a guess.
    internal static string? MatchUniqueTitle(IReadOnlyDictionary<string, string> names, string title)
    {
        var matches = names.Where(pair => pair.Value == title).Select(pair => pair.Key).Take(2).ToArray();
        if (matches.Length == 0 && title.EndsWith('…') && title[..^1].TrimEnd() is { Length: > 0 } shown)
            matches = names.Where(pair => pair.Value.StartsWith(shown, StringComparison.Ordinal)).Select(pair => pair.Key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public string Read(string id)
    {
        var index = Path.Combine(Directory.GetParent(sessionRoot)!.FullName, "session_index.jsonl");
        try
        {
            var modified = File.GetLastWriteTimeUtc(index);
            if (modified != _write)
            {
                using var file = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        // A line of another shape is skipped; it must not stop the chat on screen from being read.
                        if (r.ValueKind == JsonValueKind.Object
                            && r.TryGetProperty("id", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() is { Length: > 0 } threadId
                            && r.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String)
                            _names[threadId] = name.GetString()!;
                    }
                    catch (JsonException) { }
                }
                _write = modified;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (id.Length > 0 && !_stateTitles.ContainsKey(id) && StateDue()) ReadState();
        return _stateTitles.GetValueOrDefault(id) ?? _names.GetValueOrDefault(id) ?? OverlayPresentationBuilder.ShortThreadId(id);
    }
}
