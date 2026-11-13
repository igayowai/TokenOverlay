using System.Text.Json;

namespace TokenOverlay;

// One finished DeepSeek Harness (DSH) task (a turn: from the request to the end of its last step), counted the way the
// Claude capsule counts a turn: TPS = output ÷ the whole turn (tools and every wait included); 모델 = output ÷ its
// requests' times added up (step start → the step's committed message, the wait for the first piece included);
// 생성 = output ÷ their generating time only (first streamed piece → committed message). Tool time is out of both.
internal sealed record DeepSeekTurn(long Output, double Seconds, double RequestSeconds, double ModelSeconds, int Requests, string? Model, DateTimeOffset At)
{
    public double TokensPerSecond => Output / Seconds;
    public double RequestTokensPerSecond => Output / RequestSeconds;
    public double ModelTokensPerSecond => Output / ModelSeconds;
    // "DeepSeek V4.1 Flash 141.07 TPS": the name is drawn at the left, the rate under Claude's.
    public string Line => "DeepSeek " + (DeepSeekRateSource.ShortModel(Model) is { } model ? model + " " : "") + $"{TokensPerSecond:0.00} TPS";
    public string TtftLine => $"모델 {RequestTokensPerSecond:0.00} TPS";
    public string PureLine => $"생성 {ModelTokensPerSecond:0.00} TPS";
    // Input the API read from its cache ÷ all input, over the task's requests: "캐시히트 96.7%". DSH's inputTokens is the
    // part not read from the cache. Never 100.0% while some input missed it; none without cache numbers.
    public long CacheRead { get; init; }
    public long CacheMiss { get; init; }
    public string? CacheLine => CacheRead + CacheMiss <= 0 ? null
        : $"캐시히트 {Math.Min(CacheMiss > 0 ? 99.9 : 100, Math.Round(100.0 * CacheRead / (CacheRead + CacheMiss), 1, MidpointRounding.AwayFromZero)):0.0}%";
    // The provider routes the task's requests went through, as DSH names them ("deepseek-official"; a route added in
    // DSH's settings has the name given there, one API key each), the most used first, one per line.
    public IReadOnlyList<string> Providers { get; init; } = [];
    public string? ProviderLines => Providers.Count == 0 ? null : string.Join("\n", Providers);
    // When the task began (its turn/start), for matching it to the Claude session that started the worker.
    public DateTimeOffset Started { get; init; }
}

// The latest finished task of the DSH worker started from the Claude session on screen, for its worker rows.
// DSH does not record who called it, so a task belongs to a session whose log started the worker just before it began.
// Read-only: DSH writes each session as session.v4.jsonl.zstd (several zstd frames appended), one event per line,
// so the numbers change when a task ends, never while it runs. Only numbers are kept, no text.
internal sealed class DeepSeekRateSource
{
    // A task that ended longer ago than this is no longer shown.
    internal static readonly TimeSpan ShowFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(2);
    private const string SessionFile = "session.v4.jsonl.zstd";
    private readonly string[] _roots;
    // A task starts this long at most after the tool call that launched the worker (Node starting, the session opening).
    internal static readonly TimeSpan StartWithin = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, (long Length, DateTime Write, DeepSeekTurn? Last)> _files = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _scannedAt = DateTime.MinValue;

    public DeepSeekRateSource(params string[] roots) => _roots = roots;

    // The sessions of the headless worker Claude Code hands work to on this PC. The DSH desktop app is not started
    // from a Claude session, so its tasks are on no Claude capsule.
    public static DeepSeekRateSource ForThisPc()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(Path.Combine(home, "Documents", "Claude", "dsh-worker", "home", "sessions"));
    }

    // The newest recent task that began within a minute after one of the session's worker calls; none without calls.
    public DeepSeekTurn? Latest(DateTimeOffset now, IReadOnlyList<DateTimeOffset> calls)
    {
        if (calls.Count == 0) return null;
        if (DateTime.UtcNow - _scannedAt >= ScanEvery) Scan(now);
        DeepSeekTurn? best = null;
        foreach (var (_, _, last) in _files.Values)
            if (last is not null && now - last.At <= ShowFor && (best is null || last.At > best.At)
                && calls.Any(call => last.Started >= call && last.Started - call <= StartWithin))
                best = last;
        return best;
    }

    // Sub-agents a session started log their tool calls beside it (<session>/subagents/agent-*.jsonl); a worker one
    // of them launched counts for that session too. Each log is read again only when it changed.
    private readonly Dictionary<string, (long Length, DateTime Write, DateTimeOffset[] Calls)> _agentLogs = new(StringComparer.OrdinalIgnoreCase);
    private (string Log, DateTime At, DateTimeOffset[] Calls)? _agentCalls;

    public IReadOnlyList<DateTimeOffset> SubagentCalls(string mainLog, DateTimeOffset now)
    {
        if (_agentCalls is { } known && known.Log == mainLog && DateTime.UtcNow - known.At < ScanEvery) return known.Calls;
        var calls = new List<DateTimeOffset>();
        var folder = Path.Combine(Path.ChangeExtension(mainLog, null), "subagents");
        try
        {
            if (Directory.Exists(folder))
                foreach (var path in Directory.EnumerateFiles(folder, "agent-*.jsonl"))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.LastWriteTimeUtc < now.UtcDateTime - ShowFor - StartWithin) continue;
                        if (!_agentLogs.TryGetValue(path, out var log) || log.Length != info.Length || log.Write != info.LastWriteTimeUtc)
                            _agentLogs[path] = log = (info.Length, info.LastWriteTimeUtc, CallsIn(ReadShared(path)));
                        calls.AddRange(log.Calls);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _agentCalls = (mainLog, DateTime.UtcNow, calls.ToArray());
        return _agentCalls.Value.Calls;
    }

    // Times of the tool calls in a Claude log that launch the worker.
    internal static DateTimeOffset[] CallsIn(byte[] log)
    {
        var calls = new List<DateTimeOffset>();
        using var reader = new StreamReader(new MemoryStream(log), System.Text.Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("dsh-worker", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || type.GetString() != "assistant"
                    || !e.TryGetProperty("timestamp", out var stamp) || !DateTimeOffset.TryParse(stamp.GetString(), out var at)
                    || !e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array) continue;
                foreach (var block in blocks.EnumerateArray())
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind) && kind.GetString() == "tool_use"
                        && block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object
                        && input.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                        && StartsWorker(command.GetString()!))
                        calls.Add(at);
            }
            catch (JsonException) { }
        }
        return calls.ToArray();
    }

    // A tool call that launches the worker: its command names the worker's folder and one of its entry points.
    internal static bool StartsWorker(string command) =>
        command.Contains("dsh-worker", StringComparison.OrdinalIgnoreCase)
        && (command.Contains("run-worker", StringComparison.OrdinalIgnoreCase) || command.Contains("dsh.cmd", StringComparison.OrdinalIgnoreCase)
            || command.Contains("bin.js", StringComparison.OrdinalIgnoreCase));

    // The session keeps only the request ID. "deepseek-flash" has no version in it: DSH's own catalog
    // (dsh-llm-deepseek, 0.2.1) names it DeepSeek-V4.1-Flash, so that name is shown. Other IDs read as they are:
    // "deepseek-v4-pro" → "V4 Pro".
    internal static string? ShortModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (model.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase)) return "V4.1 Flash";
        var name = model.StartsWith("deepseek-", StringComparison.OrdinalIgnoreCase) ? model["deepseek-".Length..] : model;
        return string.Join(' ', name.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length > 1 && (part[0] is 'v' or 'V') && char.IsDigit(part[1]) ? part.ToUpperInvariant()
                : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private void Scan(DateTimeOffset now)
    {
        _scannedAt = DateTime.UtcNow;
        // Only sessions written to while a task could still be shown; a file is read again only when it changed.
        var since = now.UtcDateTime - ShowFor;
        foreach (var root in _roots)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(root)) continue;
                files = Directory.EnumerateFiles(root, SessionFile,
                    new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true }).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in files)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < since) continue;
                    if (_files.TryGetValue(path, out var known) && known.Length == info.Length && known.Write == info.LastWriteTimeUtc) continue;
                    _files[path] = (info.Length, info.LastWriteTimeUtc, LastTurn(ReadShared(path)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    // The session's lines, frame after frame. A frame still being written ends the read; the lines before it count.
    internal static IEnumerable<string> Lines(byte[] compressed)
    {
        using var stream = new ZstdSharp.DecompressionStream(new MemoryStream(compressed));
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        while (true)
        {
            string? line;
            try { line = reader.ReadLine(); }
            catch (Exception e) when (e is ZstdSharp.ZstdException or IOException or InvalidDataException) { yield break; }
            if (line is null) yield break;
            yield return line;
        }
    }

    // Earliest streamed piece of a message: a chunk's "time", or a reasoning run's "time0".
    private static long? FirstPiece(JsonElement data)
    {
        if (!data.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Array) return null;
        long? first = null;
        foreach (var piece in stream.EnumerateArray())
        {
            if (piece.ValueKind != JsonValueKind.Object) continue;
            var key = piece.TryGetProperty("type", out var kind) && kind.GetString() == "reasoning-chunks" ? "time0" : "time";
            if (piece.TryGetProperty(key, out var t) && t.TryGetInt64(out var at) && (first is null || at < first)) first = at;
        }
        return first;
    }

    internal static DeepSeekTurn? LastTurn(byte[] compressed)
    {
        DeepSeekTurn? last = null;
        long? turnStart = null, stepStart = null;
        long output = 0, modelMs = 0, requestMs = 0, cacheRead = 0, cacheMiss = 0;
        var requests = 0;
        string? model = null;
        var providers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Lines(compressed))
        {
            // Cheap filter before parsing: the type comes first on each line.
            var head = line.Length > 40 ? line[..40] : line;
            if (!head.Contains("\"turn/", StringComparison.Ordinal) && !head.Contains("\"step/start\"", StringComparison.Ordinal)
                && !head.Contains("\"assistant/message\"", StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("time", out var t) || !t.TryGetInt64(out var time)) continue;
                switch (e.TryGetProperty("type", out var type) ? type.GetString() : null)
                {
                    case "turn/start":
                        turnStart = time; stepStart = null; output = 0; modelMs = 0; requestMs = 0; requests = 0; model = null; cacheRead = 0; cacheMiss = 0;
                        providers.Clear();
                        break;
                    case "step/start":
                        stepStart = time;
                        break;
                    case "assistant/message" when turnStart is not null && e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object:
                        if (data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                            && usage.TryGetProperty("outputTokens", out var o) && o.TryGetInt64(out var tokens))
                            output += tokens;
                        if (usage.ValueKind == JsonValueKind.Object)
                        {
                            if (usage.TryGetProperty("cacheReadTokens", out var read) && read.TryGetInt64(out var r)) cacheRead += r;
                            if (usage.TryGetProperty("inputTokens", out var miss) && miss.TryGetInt64(out var i)) cacheMiss += i;
                        }
                        // From the first streamed piece; a message without stream times counts from its step's start.
                        var from = FirstPiece(data) ?? stepStart;
                        if (from is long started && time >= started)
                        {
                            modelMs += time - started;
                            requestMs += time - (stepStart is long step && step <= started ? step : started);
                            requests++;
                        }
                        stepStart = null;
                        if (data.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                            && message.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object)
                        {
                            if (source.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                                model = m.GetString();
                            if (source.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } provider)
                                providers[provider] = providers.GetValueOrDefault(provider) + 1;
                        }
                        break;
                    case "turn/end" when turnStart is long began:
                        // Too short to time gives no rate rather than a wild one; the earlier task stays on screen.
                        if (output > 0 && modelMs >= 200 && time - began >= 200)
                            last = new(output, (time - began) / 1000.0, requestMs / 1000.0, modelMs / 1000.0, requests, model, DateTimeOffset.FromUnixTimeMilliseconds(time))
                                { Started = DateTimeOffset.FromUnixTimeMilliseconds(began), CacheRead = cacheRead, CacheMiss = cacheMiss,
                                    Providers = providers.OrderByDescending(route => route.Value).Select(route => route.Key).ToArray() };
                        turnStart = null;
                        break;
                }
            }
            catch (JsonException) { }
        }
        return last;
    }
}
