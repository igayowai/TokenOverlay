using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TokenOverlay;

// Sum of the model request times that belong to one turn. Seconds is null unless every request
// of that turn was collected; Note says why.
internal sealed record ModelRate(double? Seconds, int Requests, string? Note)
{
    // The same requests less each one's wait for the first piece; PureNote says why it is missing.
    public double? PureSeconds { get; init; }
    public string? PureNote { get; init; }
    // Largest gap between a response in the session log and the request span matched to it (diagnostics).
    public double? WorstOffsetMs { get; init; }
    public const string Waiting = "계측 수집 대기";
    public static readonly ModelRate None = new(null, 0, Waiting);
}

// In-memory, allow-listed view of local OTLP telemetry. Only ids, durations and token counts are kept:
// no prompt text, tool arguments, e-mail or account/organization ids.
internal sealed class TelemetryStore
{
    private const int MaxClaudeRequests = 20000;
    private const int MaxStreams = 20000;
    private readonly int _maxSpans;
    // Requests whose turn is not known yet: their ancestors arrive later, and must be read before they are evicted.
    private readonly List<string> _unresolvedStreams = new();

    public TelemetryStore(int maxSpans = 400000) => _maxSpans = maxSpans;
    private readonly object _gate = new();
    // Ms: the whole request; Ttft: of that, the wait for the first piece of the answer (null when the event did not carry it).
    private readonly Dictionary<string, (double Ms, double? Ttft)> _claude = new(StringComparer.Ordinal);
    private readonly Queue<string> _claudeOrder = new();
    // EndMs: when the span ended (Unix ms), 0 for entries cached by a build that did not keep it.
    private sealed record SpanInfo(string? Parent, string? TurnId, bool Stream, double Milliseconds, double EndMs);
    private readonly Dictionary<string, SpanInfo> _spans = new(StringComparer.Ordinal);
    private readonly Queue<string> _spanOrder = new();
    private readonly List<string> _streams = new();
    // Codex reports each finished request's wait for the first piece in a log record of its own, with no link to the
    // request's span. It is written the moment the request ends (measured 2026-10-21: within 0.1 ms of the span's end).
    private readonly record struct CodexWait(double AtMs, double TtftMs, long? Output);
    private const int MaxCodexWaits = 20000;
    private readonly List<CodexWait> _codexWaits = new();

    // ----- small on-disk cache so the model line survives an overlay restart.
    // It holds request/turn ids, durations and token counts only — the same allow-listed fields as in memory.
    private const int MaxSavedClaude = 5000;
    private const int MaxSavedCodex = 2000;
    private const int MaxSavedPending = 300;
    private bool _dirty;
    public bool Dirty { get { lock (_gate) return _dirty; } }

    public static string DefaultCachePath => Path.Combine(AppPaths.DataFolder, "model-rate-cache.json");

    public string Export()
    {
        lock (_gate)
        {
            _dirty = false;
            var claude = _claudeOrder.Where(_claude.ContainsKey).TakeLast(MaxSavedClaude)
                .Select(id => _claude[id] is var (ms, ttft) && ttft is double wait ? new object[] { id, ms, wait } : new object[] { id, ms });
            // Only requests already tied to a turn are worth keeping; the span tree itself is not saved.
            var streams = _streams.Select(id => (Id: id, Turn: ResolveTurn(id))).Where(s => s.Turn is not null).TakeLast(MaxSavedCodex)
                .Select(s => new object[] { s.Id, s.Turn!, _spans[s.Id].Milliseconds, _spans[s.Id].EndMs });
            // Requests of a turn still running do not know their turn yet (its span arrives when the turn ends).
            // Keep them and the ancestors seen so far, so a restart in the middle of a turn does not lose them.
            var pending = new List<object[]>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in _unresolvedStreams.TakeLast(MaxSavedPending))
            {
                string? current = id;
                for (var hop = 0; hop < 64 && current is not null && _spans.TryGetValue(current, out var info) && seen.Add(current); hop++)
                {
                    pending.Add([current, info.Parent ?? "", info.TurnId ?? "", info.Stream ? 1 : 0, info.Milliseconds, info.EndMs]);
                    current = info.Parent;
                }
            }
            var waits = _codexWaits.TakeLast(MaxSavedCodex).Select(w => new object[] { w.AtMs, w.TtftMs, w.Output ?? -1 });
            return JsonSerializer.Serialize(new { version = 1, claude, codexStreams = streams, codexPending = pending, codexWaits = waits });
        }
    }

    public void Import(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.GetInt32() != 1) return;
        lock (_gate)
        {
            foreach (var row in Rows(root, "claude", 2))
                if (row[0].GetString() is { Length: > 0 } id)
                    RememberClaude(id, row[1].GetDouble(), row.Length > 2 ? row[2].GetDouble() : null, overwrite: false);
            foreach (var row in Rows(root, "codexStreams", 3))
            {
                if (row[0].GetString() is not { Length: > 0 } id || row[1].GetString() is not { Length: > 0 } turn || _spans.ContainsKey(id)) continue;
                _spans[id] = new SpanInfo(null, turn, true, row[2].GetDouble(), row.Length > 3 ? row[3].GetDouble() : 0);
                _streams.Add(id);
            }
            foreach (var row in Rows(root, "codexPending", 6))
            {
                if (row[0].GetString() is not { Length: > 0 } id || _spans.ContainsKey(id)) continue;
                var stream = row[3].GetInt32() != 0;
                _spans[id] = new SpanInfo(row[1].GetString() is { Length: > 0 } parent ? parent : null,
                    row[2].GetString() is { Length: > 0 } turn ? turn : null, stream, row[4].GetDouble(), row[5].GetDouble());
                if (stream) { _streams.Add(id); _unresolvedStreams.Add(id); }
                else _spanOrder.Enqueue(id);
            }
            foreach (var row in Rows(root, "codexWaits", 3))
                RememberCodexWait(row[0].GetDouble(), row[1].GetDouble(), row[2].GetInt64() is var output and >= 0 ? output : null);
            _dirty = false;
        }
    }

    private static IEnumerable<JsonElement[]> Rows(JsonElement root, string name, int width)
    {
        if (!root.TryGetProperty(name, out var rows) || rows.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= width) yield return row.EnumerateArray().ToArray();
    }

    public void SaveTo(string path)
    {
        var json = Export();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public bool LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            Import(File.ReadAllText(path));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return false; // a damaged cache only means the model line starts empty
        }
    }

    public long ClaudeRequests { get { lock (_gate) return _claude.Count; } }
    public long ClaudeRequestsWithTtft { get { lock (_gate) return _claude.Values.Count(v => v.Ttft is not null); } }
    public long CodexStreams { get { lock (_gate) return _streams.Count; } }

    public void Ingest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        lock (_gate)
        {
            foreach (var span in Leaves(root, "resourceSpans", "scopeSpans", "spans")) { IngestSpan(span); _dirty = true; }
            _unresolvedStreams.RemoveAll(id => !_spans.ContainsKey(id) || ResolveTurn(id) is not null);
            if (_unresolvedStreams.Count > 2000) _unresolvedStreams.RemoveRange(0, _unresolvedStreams.Count - 2000);
            foreach (var log in Leaves(root, "resourceLogs", "scopeLogs", "logRecords")) { IngestLog(log); _dirty = true; }
        }
    }

    private void IngestSpan(JsonElement span)
    {
        var name = Text(span, "name") ?? "";
        if (name == "claude_code.llm_request")
        {
            // duration_ms covers the whole request including retries; the id is the answered attempt's.
            if (Attribute(span, "request_id") is { Length: > 0 } requestId && Number(span, "duration_ms") is double ms)
                RememberClaude(requestId, ms, Number(span, "ttft_ms"), overwrite: true);
            return;
        }
        if (name.StartsWith("claude_code.", StringComparison.Ordinal)) return;
        var id = Text(span, "spanId");
        if (string.IsNullOrEmpty(id) || _spans.ContainsKey(id)) return;
        // Only the real transport span counts. run_sampling_request also contains the tool wait.
        var stream = name.EndsWith(".stream_request", StringComparison.Ordinal);
        var turn = Attribute(span, "turn_id") ?? Attribute(span, "turn.id");
        var ended = Nanos(span, "endTimeUnixNano");
        var elapsed = (ended - Nanos(span, "startTimeUnixNano")) / 1_000_000m;
        _spans[id] = new SpanInfo(Text(span, "parentSpanId") is { Length: > 0 } parent ? parent : null,
            string.IsNullOrEmpty(turn) ? null : turn, stream, (double)elapsed, (double)(ended / 1_000_000m));
        // The surrounding span tree is large (thousands of spans per request) and only needed until a request
        // knows its turn, so it is evicted on its own budget; the requests themselves are kept much longer.
        if (stream)
        {
            _streams.Add(id);
            _unresolvedStreams.Add(id);
            while (_streams.Count > MaxStreams) { _spans.Remove(_streams[0]); _streams.RemoveAt(0); }
        }
        else
        {
            _spanOrder.Enqueue(id);
            while (_spanOrder.Count > _maxSpans) _spans.Remove(_spanOrder.Dequeue());
        }
    }

    private void IngestLog(JsonElement log)
    {
        var name = Attribute(log, "event.name");
        if (name is "api_request" or "claude_code.api_request")
        {
            if (Attribute(log, "request_id") is { Length: > 0 } requestId && Number(log, "duration_ms") is double ms)
                RememberClaude(requestId, ms, Number(log, "ttft_ms"), overwrite: false);
        }
        else if (name == "codex.sse_event" && Attribute(log, "event.kind") == "response.completed"
            && Number(log, "ttft_ms") is double ttft && (Nanos(log, "timeUnixNano") is > 0 and var time ? time : Nanos(log, "observedTimeUnixNano")) is > 0 and var at)
        {
            RememberCodexWait((double)(at / 1_000_000m), ttft, Number(log, "output_token_count") is double output ? (long)output : null);
        }
    }

    private void RememberCodexWait(double atMs, double ttftMs, long? output)
    {
        var wait = new CodexWait(atMs, ttftMs, output);
        if (_codexWaits.Contains(wait)) return; // re-sent
        _codexWaits.Add(wait);
        if (_codexWaits.Count > MaxCodexWaits) _codexWaits.RemoveRange(0, _codexWaits.Count - MaxCodexWaits);
    }

    // The wait recorded when this request ended: between its span's end and its token record, with the same output.
    private int FindCodexWait(double spanEnd, double at, long output, HashSet<int> taken)
    {
        var found = -1;
        for (var i = _codexWaits.Count - 1; i >= 0; i--)
        {
            var wait = _codexWaits[i];
            if (taken.Contains(i) || wait.AtMs < spanEnd - ClockSlackMs || wait.AtMs > Math.Max(spanEnd, at) + ClockSlackMs) continue;
            if (wait.Output is long counted && counted != output) continue;
            if (found < 0 || Math.Abs(wait.AtMs - spanEnd) < Math.Abs(_codexWaits[found].AtMs - spanEnd)) found = i;
        }
        return found;
    }

    // The span and the event of one request may each carry the first-piece wait; whichever has it keeps it.
    private void RememberClaude(string requestId, double milliseconds, double? ttft, bool overwrite)
    {
        if (_claude.TryGetValue(requestId, out var known))
        {
            _claude[requestId] = (overwrite ? milliseconds : known.Ms, (overwrite ? ttft : known.Ttft) ?? known.Ttft ?? ttft);
            return;
        }
        _claude[requestId] = (milliseconds, ttft);
        _claudeOrder.Enqueue(requestId);
        while (_claudeOrder.Count > MaxClaudeRequests) _claude.Remove(_claudeOrder.Dequeue());
    }

    // requestIds: one per main-agent response of the turn, exactly as the session log lists them.
    public ModelRate ClaudeTurn(IReadOnlyCollection<string?> requestIds)
    {
        var wanted = requestIds.Distinct().ToArray();
        if (wanted.Length == 0) return ModelRate.None;
        lock (_gate)
        {
            // Whole request times, and the same less each one's wait for the first piece (generating time only).
            double total = 0, pure = 0;
            int found = 0, timed = 0;
            foreach (var id in wanted)
                if (id is not null && _claude.TryGetValue(id, out var request))
                {
                    found++;
                    total += request.Ms;
                    if (request.Ttft is double wait) { pure += request.Ms - Math.Min(wait, request.Ms); timed++; }
                }
            if (found == 0) return ModelRate.None with { PureNote = ModelRate.Waiting };
            if (found < wanted.Length)
            {
                var note = $"계측 일부 누락 ({found}/{wanted.Length})";
                return new ModelRate(null, found, note) { PureNote = note };
            }
            return timed < found
                ? new ModelRate(total / 1000d, found, null) { PureNote = $"첫 토큰 대기 계측 없음 ({found - timed}/{found})" }
                : new ModelRate(total / 1000d, found, null) { PureSeconds = pure / 1000d };
        }
    }

    // Order inside a turn (measured): request ends → the tools it called run → the log writes that response's token
    // record → next request starts. So a record belongs to the request that most recently ended before it, however
    // long the tools took. If that request is already taken, the record's own request was not collected.
    private const double ClockSlackMs = 200;

    // A turn's requests are the transport spans below a span that names the turn. Each response the session log
    // counted (a token increase at a given time) is paired with the request that ended at that moment. Requests
    // with no counted response — cut off mid-way, or a context compaction whose tokens the log leaves out — are
    // not part of the turn's output and are left out of its time as well.
    // Codex may close a request's span once the reply starts streaming, long before the model finishes (measured
    // 2026-10-18: spans of 1-3 s while the reply kept coming for 15-40 s). So a request lasts until the later of its
    // span's end and the last output the log wrote for its response.
    public ModelRate CodexTurn(string turnId, IReadOnlyList<CountedResponse> responses)
    {
        lock (_gate)
        {
            var streams = new List<SpanInfo>();
            foreach (var id in _streams)
                if (ResolveTurn(id) == turnId) streams.Add(_spans[id]);
            if (streams.Count == 0 || responses.Count == 0) return ModelRate.None;
            if (streams.Any(s => s.EndMs <= 0))
            {
                // Cached by an older build without end times: only an exact one-to-one count can be trusted.
                return streams.Count == responses.Count
                    ? new ModelRate(streams.Sum(s => s.Milliseconds) / 1000d, streams.Count, null) { PureNote = "첫 토큰 대기 계측 없음" }
                    : new ModelRate(null, streams.Count, $"계측 일부 누락 (요청 {streams.Count} · 응답 {responses.Count})");
            }
            // pure: the same request times less each one's wait for the first piece.
            double total = 0, pure = 0, worst = 0;
            int matched = 0, timed = 0;
            var used = new bool[streams.Count];
            var takenWaits = new HashSet<int>();
            foreach (var response in responses.OrderBy(r => r.At))
            {
                var at = (double)response.At.ToUnixTimeMilliseconds();
                var best = -1;
                var bestEnd = double.MinValue;
                for (var i = 0; i < streams.Count; i++)
                {
                    var end = streams[i].EndMs;
                    if (end <= at + ClockSlackMs && end > bestEnd) { best = i; bestEnd = end; }
                }
                if (best < 0 || used[best]) continue;
                var bestGap = Math.Abs(at - bestEnd);
                used[best] = true;
                var spanEnd = streams[best].EndMs;
                var lastOutput = response.LastItem is DateTimeOffset item ? (double)item.ToUnixTimeMilliseconds() : spanEnd;
                var request = streams[best].Milliseconds + Math.Clamp(lastOutput - spanEnd, 0, Math.Max(0, at + ClockSlackMs - spanEnd));
                total += request;
                if (FindCodexWait(spanEnd, at, response.Output, takenWaits) is var wait and >= 0)
                {
                    takenWaits.Add(wait);
                    pure += request - Math.Min(_codexWaits[wait].TtftMs, request);
                    timed++;
                }
                worst = Math.Max(worst, bestGap);
                matched++;
            }
            if (matched < responses.Count)
            {
                var note = $"계측 일부 누락 ({matched}/{responses.Count})";
                return new ModelRate(null, matched, note) { WorstOffsetMs = worst, PureNote = note };
            }
            return timed < matched
                ? new ModelRate(total / 1000d, matched, null) { WorstOffsetMs = worst, PureNote = $"첫 토큰 대기 계측 없음 ({matched - timed}/{matched})" }
                : new ModelRate(total / 1000d, matched, null) { WorstOffsetMs = worst, PureSeconds = pure / 1000d };
        }
    }

    private string? ResolveTurn(string spanId)
    {
        var current = spanId;
        for (var hop = 0; hop < 64 && current is not null && _spans.TryGetValue(current, out var info); hop++)
        {
            if (info.TurnId is not null)
            {
                // Remember it on the request itself, so it no longer depends on the ancestors staying in memory.
                if (hop > 0 && _spans.TryGetValue(spanId, out var self) && self.Stream) _spans[spanId] = self with { TurnId = info.TurnId };
                return info.TurnId;
            }
            current = info.Parent;
        }
        return null;
    }

    private static IEnumerable<JsonElement> Leaves(JsonElement root, string resources, string scopes, string items)
    {
        if (!root.TryGetProperty(resources, out var a) || a.ValueKind != JsonValueKind.Array) yield break;
        foreach (var resource in a.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object || !resource.TryGetProperty(scopes, out var b)
                || b.ValueKind != JsonValueKind.Array) continue;
            foreach (var scope in b.EnumerateArray())
            {
                if (scope.ValueKind != JsonValueKind.Object || !scope.TryGetProperty(items, out var c)
                    || c.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in c.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object) yield return item;
            }
        }
    }

    private static string? Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal Nanos(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var s) ? s : 0;
    }

    private static JsonElement? AttributeValue(JsonElement e, string key)
    {
        if (!e.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array) return null;
        foreach (var attribute in attributes.EnumerateArray())
            if (attribute.ValueKind == JsonValueKind.Object && Text(attribute, "key") == key
                && attribute.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        return null;
    }

    private static string? Attribute(JsonElement e, string key)
    {
        if (AttributeValue(e, key) is not { } value) return null;
        foreach (var property in value.EnumerateObject())
            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                _ => null
            };
        return null;
    }

    private static double? Number(JsonElement e, string key) =>
        double.TryParse(Attribute(e, key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;
}

// Loopback-only OTLP/HTTP JSON endpoint living inside the overlay process. Nothing is written to disk.
internal sealed class TelemetryReceiver : IDisposable
{
    public const int DefaultPort = 43189;
    private const int MaxBodyBytes = 32 * 1024 * 1024;
    private readonly HttpListener _listener = new();
    private readonly TelemetryStore _store;
    public string? Error { get; private set; }
    public long Accepted;

    public static string EndpointFor(int port) => $"http://127.0.0.1:{port}";

    public TelemetryReceiver(TelemetryStore store, int port = DefaultPort)
    {
        _store = store;
        _listener.Prefixes.Add(EndpointFor(port) + "/");
    }

    public bool Start()
    {
        try
        {
            _listener.Start();
            _ = Task.Run(AcceptLoop);
            return true;
        }
        catch (HttpListenerException e)
        {
            // Never fall back to another port: the apps are configured for this one.
            Error = "계측 수신 포트를 열 수 없음: " + e.Message;
            return false;
        }
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var status = 200;
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath;
            if (request.HttpMethod != "POST" || path is not ("/v1/traces" or "/v1/logs")) status = 404;
            // application/json only: a web page can send text/plain to 127.0.0.1 without asking, but not JSON.
            else if (!IsJson(request.ContentType)) status = 415;
            else if (request.ContentLength64 > MaxBodyBytes) status = 413;
            else
            {
                Stream body = request.InputStream;
                if (string.Equals(request.Headers["Content-Encoding"], "gzip", StringComparison.OrdinalIgnoreCase))
                    body = new GZipStream(body, CompressionMode.Decompress);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > MaxBodyBytes) { status = 413; break; }
                    buffer.Write(chunk, 0, read);
                }
                if (status == 200)
                {
                    using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
                    _store.Ingest(document.RootElement);
                    Interlocked.Increment(ref Accepted);
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or HttpListenerException)
        {
            status = 400;
        }
        try
        {
            var reply = Encoding.ASCII.GetBytes("{}");
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = reply.Length;
            context.Response.OutputStream.Write(reply, 0, reply.Length);
            context.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    internal static bool IsJson(string? contentType) =>
        contentType?.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase) == true;

    public void Dispose()
    {
        try { _listener.Close(); }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }
}

internal enum TelemetryLinkState { Off, On, Conflict, AppMissing }

// Connects/disconnects the two apps' own telemetry exporters to the local receiver.
// Only the keys added here are ever written or removed; an existing exporter is never replaced.
internal static class TelemetrySetup
{
    private const string TomlBegin = "# >>> TokenOverlay telemetry (managed block; turn off from the overlay tray menu)";
    private const string TomlEnd = "# <<< TokenOverlay telemetry";

    public static string ClaudeSettingsPath
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(home, "settings.json");
        }
    }

    public static string CodexConfigPath
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            return Path.Combine(home, "config.toml");
        }
    }

    internal static IReadOnlyList<KeyValuePair<string, string>> ClaudeEnvironment(string endpoint) =>
    [
        new("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
        new("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
        new("OTEL_TRACES_EXPORTER", "otlp"),
        new("OTEL_LOGS_EXPORTER", "otlp"),
        new("OTEL_METRICS_EXPORTER", "none"),
        new("OTEL_EXPORTER_OTLP_PROTOCOL", "http/json"),
        new("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint),
        new("OTEL_TRACES_EXPORT_INTERVAL", "1000"),
        new("OTEL_LOGS_EXPORT_INTERVAL", "1000"),
        new("OTEL_LOG_USER_PROMPTS", "0"),
        new("OTEL_LOG_TOOL_DETAILS", "0"),
        new("OTEL_LOG_TOOL_CONTENT", "0"),
    ];

    public static TelemetryLinkState ClaudeState(string path, string endpoint)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) return TelemetryLinkState.AppMissing;
        if (ReadJson(path)?["env"] is not JsonObject env) return TelemetryLinkState.Off;
        var ours = ClaudeEnvironment(endpoint);
        if (ours.All(pair => env[pair.Key] is JsonValue value && value.ToString() == pair.Value)) return TelemetryLinkState.On;
        var foreign = env.Any(pair => (pair.Key.StartsWith("OTEL_", StringComparison.Ordinal) || ours.Any(o => o.Key == pair.Key))
            && ours.FirstOrDefault(o => o.Key == pair.Key).Value != pair.Value?.ToString());
        return foreign ? TelemetryLinkState.Conflict : TelemetryLinkState.Off;
    }

    // Which keys this program added to the Claude settings, kept beside them so it survives restarts.
    // Disconnecting removes exactly those, and only while they still hold the value written here.
    internal static string ClaudeOwnershipPath(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(settingsPath)!, "token-overlay.telemetry.json");

    private static readonly JsonSerializerOptions Indented = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TelemetryLinkState SetClaude(string path, string endpoint, bool enabled)
    {
        var state = ClaudeState(path, endpoint);
        if (state == TelemetryLinkState.AppMissing) return state;
        var ownershipPath = ClaudeOwnershipPath(path);
        var owned = ReadJson(ownershipPath);
        var root = ReadJson(path) ?? new JsonObject();
        var ours = ClaudeEnvironment(endpoint);
        if (enabled)
        {
            if (state is TelemetryLinkState.Conflict or TelemetryLinkState.On) return state;
            var env = root["env"] as JsonObject;
            var added = owned?["added"] as JsonObject ?? new JsonObject();
            var createdEnv = owned?["createdEnv"]?.GetValue<bool>() ?? env is null;
            if (env is null) root["env"] = env = new JsonObject();
            foreach (var pair in ours)
            {
                // A key that was already there (same value, or it would be a conflict) stays the user's.
                if (env[pair.Key] is not null) continue;
                env[pair.Key] = pair.Value;
                added[pair.Key] = pair.Value;
            }
            // Record first: a settings file changed without a record could not be undone precisely.
            File.WriteAllText(ownershipPath, new JsonObject { ["endpoint"] = endpoint, ["createdEnv"] = createdEnv,
                ["added"] = added.DeepClone() }.ToJsonString(Indented) + "\n", new UTF8Encoding(false));
        }
        else
        {
            if (root["env"] is not JsonObject env) { DeleteOwnership(ownershipPath); return state; }
            if (owned?["added"] is JsonObject added)
            {
                foreach (var pair in added.ToArray())
                    if (env[pair.Key] is JsonValue current && current.ToString() == pair.Value?.ToString()) env.Remove(pair.Key);
                if (env.Count == 0 && owned["createdEnv"]?.GetValue<bool>() == true) root.Remove("env");
            }
            else
            {
                // Connected by a build that kept no record: which keys were the user's is unknown, so only the
                // one that can be nobody else's — this receiver's endpoint — is removed.
                const string endpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
                if (env[endpointKey] is JsonValue current && current.ToString() == endpoint) env.Remove(endpointKey);
                else return state;
            }
        }
        File.WriteAllText(path, root.ToJsonString(Indented) + "\n", new UTF8Encoding(false));
        if (!enabled) DeleteOwnership(ownershipPath);
        return ClaudeState(path, endpoint);
    }

    private static void DeleteOwnership(string ownershipPath)
    {
        if (File.Exists(ownershipPath)) File.Delete(ownershipPath);
    }

    internal static string CodexBlock(string endpoint, string newline) => string.Join(newline,
        TomlBegin,
        "[otel]",
        "log_user_prompt = false",
        $"exporter = {{ otlp-http = {{ endpoint = \"{endpoint}/v1/logs\", protocol = \"json\" }} }}",
        $"trace_exporter = {{ otlp-http = {{ endpoint = \"{endpoint}/v1/traces\", protocol = \"json\" }} }}",
        TomlEnd) + newline;

    private static readonly Regex ManagedBlock = new(
        @"(\r?\n)?" + Regex.Escape(TomlBegin) + @".*?" + Regex.Escape(TomlEnd) + @"[^\S\r\n]*(\r?\n)?",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ForeignOtel = new(@"^[^\S\r\n]*(\[\s*otel\s*[\].]|otel\s*[.=])",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public static TelemetryLinkState CodexState(string path, string endpoint)
    {
        if (!Directory.Exists(Path.GetDirectoryName(path))) return TelemetryLinkState.AppMissing;
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var block = ManagedBlock.Match(text);
        if (ForeignOtel.IsMatch(ManagedBlock.Replace(text, "\n"))) return TelemetryLinkState.Conflict;
        if (!block.Success) return TelemetryLinkState.Off;
        return block.Value.Contains(endpoint + "/v1/traces", StringComparison.Ordinal)
            ? TelemetryLinkState.On : TelemetryLinkState.Conflict;
    }

    public static TelemetryLinkState SetCodex(string path, string endpoint, bool enabled)
    {
        var state = CodexState(path, endpoint);
        if (state is TelemetryLinkState.AppMissing or TelemetryLinkState.Conflict) return state;
        if ((state == TelemetryLinkState.On) == enabled) return state;
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (enabled)
        {
            if (text.Length > 0 && !text.EndsWith('\n')) text += newline;
            text += (text.Length > 0 ? newline : "") + CodexBlock(endpoint, newline);
        }
        else
        {
            // The separating blank line added on connect goes with the block.
            text = ManagedBlock.Replace(text, match => CodexLeftover(match.Value, endpoint, newline));
        }
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return CodexState(path, endpoint);
    }

    // Codex writes a new table of its own at the end of the file, ahead of the closing marker comment, so it
    // lands inside the block. Only the block's own lines go; whatever Codex wrote between them stays.
    private static string CodexLeftover(string block, string endpoint, string newline)
    {
        var ours = CodexBlock(endpoint, "\n").Split('\n');
        var kept = block.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => !ours.Contains(line)).ToList();
        while (kept.Count > 0 && kept[0].Trim().Length == 0) kept.RemoveAt(0);
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        return kept.Count == 0 ? "" : newline + string.Join(newline, kept) + newline;
    }

    public static (TelemetryLinkState Claude, TelemetryLinkState Codex) Status()
    {
        var endpoint = TelemetryReceiver.EndpointFor(TelemetryReceiver.DefaultPort);
        return (ClaudeState(ClaudeSettingsPath, endpoint), CodexState(CodexConfigPath, endpoint));
    }

    public static (TelemetryLinkState Claude, TelemetryLinkState Codex) SetEnabled(bool enabled)
    {
        var endpoint = TelemetryReceiver.EndpointFor(TelemetryReceiver.DefaultPort);
        return (SetClaude(ClaudeSettingsPath, endpoint, enabled), SetCodex(CodexConfigPath, endpoint, enabled));
    }

    // Same implementation as the tray toggle, callable for installation verification.
    public static bool TryRun(string[] args)
    {
        if (args.Length < 3 || args[0] != "--telemetry") return false;
        object result;
        try
        {
            var state = args[1] switch
            {
                "on" => SetEnabled(true),
                "off" => SetEnabled(false),
                "status" => Status(),
                _ => throw new ArgumentException("Unknown telemetry action")
            };
            result = new { Success = true, Claude = state.Claude.ToString(), Codex = state.Codex.ToString(),
                Endpoint = TelemetryReceiver.EndpointFor(TelemetryReceiver.DefaultPort), ClaudeSettingsPath, CodexConfigPath };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Environment.ExitCode = 1;
            result = new { Success = false, Error = e.Message };
        }
        File.WriteAllText(args[2], JsonSerializer.Serialize(result));
        return true;
    }

    private static JsonObject? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new JsonException("settings.json 최상위가 객체가 아님");
    }
}
