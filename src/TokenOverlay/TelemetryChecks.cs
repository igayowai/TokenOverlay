using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

internal static class TelemetryChecks
{
    public static object Run(string scratch)
    {
        var results = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("FAIL: " + name);
            results.Add(name);
        }
        static object Attr(string key, object value) => new { key, value = value is string s ? (object)new { stringValue = s } : new { intValue = value.ToString() } };
        static JsonElement Doc(object o) => JsonSerializer.SerializeToElement(o);
        static JsonElement Spans(params object[] spans) => Doc(new { resourceSpans = new[] { new { scopeSpans = new[] { new { spans } } } } });
        static JsonElement Logs(params object[] logRecords) => Doc(new { resourceLogs = new[] { new { scopeLogs = new[] { new { logRecords } } } } });
        static object ClaudeRequest(string? requestId, int ms, string source = "sdk", int? ttft = null) => new
        {
            name = "claude_code.llm_request", spanId = Guid.NewGuid().ToString("N")[..16],
            attributes = new List<object> { Attr("duration_ms", ms), Attr("output_tokens", 1), Attr("query_source_safe", source),
                Attr("user.email", "someone@example.com") }.Concat(requestId is null ? [] : new[] { Attr("request_id", requestId) })
                .Concat(ttft is null ? [] : new[] { Attr("ttft_ms", ttft) }).ToArray()
        };

        // ----- Claude: the measured desktop case (79 + 223 tokens, 1305 + 2919 ms, 13.689 s turn), of which 305 + 919 ms
        // waited for the first piece: 모델 counts the 4.224 s of requests, 생성 the 1 + 2 s that generated.
        var store = new TelemetryStore();
        Check(store.ClaudeTurn(["req_a", "req_b"]) is { Seconds: null, Note: ModelRate.Waiting }, "no telemetry: model rate stays empty");
        store.Ingest(Spans(ClaudeRequest("req_a", 1305, ttft: 305)));
        Check(store.ClaudeTurn(["req_a", "req_b"]) is { Seconds: null, Requests: 1 } partial && partial.Note!.Contains("1/2"),
            "partially collected turn is not presented as an average");
        store.Ingest(Spans(ClaudeRequest("req_b", 2919, ttft: 919), ClaudeRequest("req_classifier", 1098, "agent_classifier"), ClaudeRequest(null, 10)));
        store.Ingest(Spans(ClaudeRequest("req_a", 1305, ttft: 305)));
        var claude = store.ClaudeTurn(["req_a", "req_b", "req_a"]);
        Check(claude is { Requests: 2 } && Math.Abs(claude.Seconds!.Value - 4.224) < 1e-9 && Math.Abs(claude.PureSeconds!.Value - 3.0) < 1e-9,
            "late/duplicate records counted once; auxiliary request absent from the session log is excluded");
        Check(store.ClaudeTurn(["req_other"]).Seconds is null, "another turn's requests are never substituted");
        store.Ingest(Spans(ClaudeRequest("req_nowait", 1500)));
        Check(store.ClaudeTurn(["req_a", "req_nowait"]) is { Seconds: 2.805, PureSeconds: null, Requests: 2 } untimed && untimed.PureNote!.Contains("첫 토큰 대기"),
            "a request without its first-piece wait leaves 생성 empty (모델 still shown) rather than counting the wait in");
        var logOnly = new TelemetryStore();
        logOnly.Ingest(Logs(new { attributes = new[] { Attr("event.name", "api_request"), Attr("request_id", "req_l"), Attr("duration_ms", 1300), Attr("ttft_ms", 300) } }));
        Check(logOnly.ClaudeTurn(["req_l"]) is { Seconds: 1.3, PureSeconds: 1.0 }, "api_request log is accepted when the span is missing");
        logOnly.Ingest(Spans(ClaudeRequest("req_l", 1305)));
        Check(logOnly.ClaudeTurn(["req_l"]) is { Seconds: 1.305 } spanned && Math.Abs(spanned.PureSeconds!.Value - 1.005) < 1e-9, "span duration replaces the log duration for the same request");
        // The event carries the first-piece wait; the span that replaces its duration does not, and must not drop it.
        logOnly.Ingest(Logs(new { attributes = new[] { Attr("event.name", "api_request"), Attr("request_id", "req_t"), Attr("duration_ms", 2000), Attr("ttft_ms", 800) } }));
        logOnly.Ingest(Spans(ClaudeRequest("req_t", 2010)));
        Check(logOnly.ClaudeRequestsWithTtft == 2 && logOnly.ClaudeTurn(["req_t"]) is { Seconds: 2.01 } waited && Math.Abs(waited.PureSeconds!.Value - 1.21) < 1e-9,
            "first-piece wait kept from the event when the span replaces the duration");
        var ttftCache = Path.Combine(scratch, "telemetry-ttft", "model-rate-cache.json");
        logOnly.SaveTo(ttftCache);
        var ttftRestored = new TelemetryStore();
        Check(ttftRestored.LoadFrom(ttftCache) && ttftRestored.ClaudeRequestsWithTtft == 2 && ttftRestored.ClaudeTurn(["req_t"]).PureSeconds == logOnly.ClaudeTurn(["req_t"]).PureSeconds,
            "first-piece wait survives a restart");

        string At(double seconds) => DateTimeOffset.Parse("2026-10-05T00:00:00Z").AddSeconds(seconds).ToString("O");
        string User(string prompt, double at) => JsonSerializer.Serialize(new { type = "user", promptId = prompt, timestamp = At(at),
            message = new { role = "user", content = "hi" } });
        string Assistant(string id, string? requestId, double at, string stop, long output) => JsonSerializer.Serialize(new { type = "assistant",
            timestamp = At(at), requestId, message = new { id, stop_reason = stop, usage = new { input_tokens = 2, output_tokens = output, speed = "standard" } } });
        var reader = new ClaudeTurnReader();
        reader.ProcessLine(User("t1", 0));
        reader.ProcessLine(Assistant("m1", "req_a", 1.331, "tool_use", 79));
        reader.ProcessLine(Assistant("m1", "req_a", 1.331, "tool_use", 79));
        reader.ProcessLine(Assistant("m2", "req_b", 13.689, "end_turn", 223));
        var turn = reader.Snapshot()!.With(store.ClaudeTurn(reader.DisplayedRequestIds));
        Check(turn is { Output: 302, Seconds: 13.689 } && Math.Abs(turn.TokensPerSecond!.Value - 22.06) < 0.01
            && Math.Abs(turn.ModelTokensPerSecond!.Value - 71.50) < 0.01 && Math.Abs(turn.PureTokensPerSecond!.Value - 100.67) < 0.01,
            "Claude turn: 22.06 TPS whole turn, 71.50 TPS over its requests (모델), 100.67 TPS while the model generated (생성)");
        reader.ProcessLine(User("t2", 20));
        reader.ProcessLine(Assistant("m3", "req_c", 21, "tool_use", 50));
        var running = reader.Snapshot()!.With(store.ClaudeTurn(reader.DisplayedRequestIds));
        Check(running is { TurnId: "t1", Running: true } && Math.Abs(running.ModelTokensPerSecond!.Value - 71.50) < 0.01 && Math.Abs(running.PureTokensPerSecond!.Value - 100.67) < 0.01,
            "while a new turn runs both lines keep the previous completed turn");

        // ----- Codex: the measured builtin-sleep case (19 + 203 tokens, 2914.3498 + 8935.1818 ms, 16.825 s turn)
        const string turnId = "turn-1";
        var start = DateTimeOffset.Parse("2026-10-05T01:00:00Z");
        long Nano(double seconds) => (start.ToUnixTimeMilliseconds() + (long)(seconds * 1000)) * 1_000_000L;
        object Span(string id, string? parent, string name, double from, double to, string? turn = null) => new
        {
            spanId = id, parentSpanId = parent ?? "", name,
            startTimeUnixNano = Nano(from).ToString(), endTimeUnixNano = (Nano(from) + (long)Math.Round((to - from) * 1e9)).ToString(),
            attributes = turn is null ? new[] { Attr("thread.id", 18) } : new[] { Attr("thread.id", 18), Attr("turn_id", turn) }
        };
        // What the session log counted: a token increase written right after each response ended.
        CountedResponse Counted(double at, long output, double? lastItem = null) =>
            new(start.AddSeconds(at), output, lastItem is double item ? start.AddSeconds(item) : null);
        var counted = new[] { Counted(3.916, 19), Counted(16.737, 203) };
        var codex = new TelemetryStore();
        // Children arrive before the ancestors that name the turn.
        codex.Ingest(Spans(Span("s1", "w1", "responses_websocket.stream_request", 1.0, 3.9143498),
            Span("w1", "t1", "stream_request", 0.99, 1.0), Span("pre", "boot", "responses_websocket.stream_request", -5, -4.5)));
        Check(codex.CodexTurn(turnId, counted).Seconds is null, "ancestor not received yet: no rate");
        codex.Ingest(Spans(Span("t1", "run", "try_run_sampling_request", 0.9, 6.79, turnId), Span("run", null, "run_turn", 0, 16.825)));
        Check(codex.CodexTurn(turnId, counted) is { Seconds: null, Requests: 1 },
            "one of two responses has its request: partial, not an average");
        codex.Ingest(Spans(Span("s2", "w2", "responses_websocket.stream_request", 7.8, 16.7351818),
            Span("w2", "t2", "stream_request", 7.79, 7.8), Span("t2", "run", "try_run_sampling_request", 7.7, 16.74, turnId)));
        var rate = codex.CodexTurn(turnId, counted);
        Check(rate is { Requests: 2, WorstOffsetMs: < 5 } && Math.Abs(rate.Seconds!.Value - 11.8495316) < 1e-6,
            "Codex turn: each counted response paired with the request ending at that moment; prewarm and tool wait excluded");
        Check(Math.Abs(222 / rate.Seconds!.Value - 18.73) < 0.01 && Math.Abs(222 / 16.825 - 13.19) < 0.01, "Codex rates 13.19 / 18.73 TPS reproduced");
        codex.Ingest(Spans(Span("s2", "w2", "responses_websocket.stream_request", 7.8, 16.7351818)));
        Check(Math.Abs(codex.CodexTurn(turnId, counted).Seconds!.Value - 11.8495316) < 1e-6, "duplicate span export ignored");
        Check(codex.CodexTurn("turn-2", counted).Seconds is null, "other turn of the same chat gets no value");
        Check(codex.CodexTurn(turnId, [.. counted, Counted(30, 50)]) is { Seconds: null, Requests: 2 }, "a counted response with no request near it keeps the turn partial");
        Check(codex.CodexTurn(turnId, []).Seconds is null, "no counted response: nothing to rate");
        Check(codex.CodexTurn(turnId, [Counted(3.9143498 + 3.7, 19), Counted(16.7351818 + 0.7, 203)]) is { Requests: 2, WorstOffsetMs: > 3600 and < 3800 } lagged
            && Math.Abs(lagged.Seconds!.Value - 11.8495316) < 1e-6,
            "token record written after the tools that followed the request (measured 3.7 s) is still paired with it");
        Check(codex.CodexTurn(turnId, [Counted(3.916, 19), Counted(5.5, 40), Counted(16.737, 203)]) is { Seconds: null, Requests: 2 },
            "a record whose own request was not collected is not handed an earlier request");
        Check(codex.CodexTurn(turnId, [Counted(0.5, 19), Counted(16.737, 203)]).Seconds is null, "a record before any request ended is not paired");
        // Measured 2026-10-18: the span closed 2.7 s into a request whose reply the log kept writing for 40 s.
        var shortSpan = new TelemetryStore();
        shortSpan.Ingest(Spans(Span("q1", "qw", "responses_websocket.stream_request", 1.0, 3.7), Span("qw", "qt", "stream_request", 0.99, 1.0),
            Span("qt", null, "try_run_sampling_request", 0.9, 41.0, "turn-short")));
        Check(shortSpan.CodexTurn("turn-short", [Counted(40.9, 1415, lastItem: 40.2)]) is { Seconds: double longReply, Requests: 1 }
            && Math.Abs(longReply - 39.2) < 0.01, "a span that closed early runs on to the model's last output in the log");
        Check(shortSpan.CodexTurn("turn-short", [Counted(40.9, 1415, lastItem: 2.0)]) is { Seconds: double within } && Math.Abs(within - 2.7) < 1e-6
            && shortSpan.CodexTurn("turn-short", [Counted(40.9, 1415)]) is { Seconds: double noItem } && Math.Abs(noItem - 2.7) < 1e-6,
            "output written before the span ended, or none at all: the span's own length");
        Check(shortSpan.CodexTurn("turn-short", [Counted(40.9, 1415, lastItem: 60)]) is { Seconds: double capped } && Math.Abs(capped - 40.1) < 0.01,
            "output stamped after its own token record is held to that record's time");
        // The real long-turn cases: a request cut off before completing and a context compaction both run under
        // the turn, but the session log counts no output for them.
        codex.Ingest(Spans(Span("cut", "wc", "responses_websocket.stream_request", 4.0, 6.5), Span("wc", "t1", "stream_request", 3.99, 4.0),
            Span("compact", "wk", "responses_websocket.stream_request", 20, 115.9), Span("wk", "t2", "stream_request", 19.99, 20)));
        var withExtras = codex.CodexTurn(turnId, counted);
        Check(withExtras is { Requests: 2, Note: null } && Math.Abs(withExtras.Seconds!.Value - 11.8495316) < 1e-6,
            "requests the log counted no response for (cut off, compaction) add neither tokens nor time");
        Check(codex.CodexTurn(turnId, [Counted(3.916, 19), Counted(6.52, 40), Counted(16.737, 203)]) is { Requests: 3 } three
            && Math.Abs(three.Seconds!.Value - 14.3495316) < 1e-6, "the same request does count once the log has a response for it");

        // Codex's wait for the first piece: a log record written as each request ends (measured 2026-10-21), with no span id.
        object CodexWait(double at, int ttft, int output) => new
        {
            timeUnixNano = "0", observedTimeUnixNano = Nano(at).ToString(),
            attributes = new[] { Attr("event.name", "codex.sse_event"), Attr("event.kind", "response.completed"), Attr("ttft_ms", ttft),
                Attr("output_token_count", output), Attr("conversation.id", "thread-1") }
        };
        Check(codex.CodexTurn(turnId, counted) is { PureSeconds: null, PureNote: { } noWait } && noWait.Contains("첫 토큰 대기"),
            "Codex: no first-piece wait received yet leaves 생성 empty");
        codex.Ingest(Logs(CodexWait(3.9144, 800, 19)));
        Check(codex.CodexTurn(turnId, counted) is { PureSeconds: null, PureNote: { } oneWait } && oneWait.Contains("(1/2)"),
            "one request's wait of two: 생성 stays empty, not a partial value");
        // Another chat's request ended 50 ms later with a different output.
        codex.Ingest(Logs(CodexWait(16.7851818, 5000, 999), CodexWait(16.7351818, 1500, 203)));
        var codexPure = codex.CodexTurn(turnId, counted);
        Check(Math.Abs(codexPure.PureSeconds!.Value - 9.5495316) < 1e-6 && Math.Abs(codexPure.Seconds!.Value - 11.8495316) < 1e-6,
            "Codex 생성: each request less its own wait, the other chat's wait at the same moment left out");
        codex.Ingest(Logs(CodexWait(16.7351818, 1500, 203)));
        Check(Math.Abs(codex.CodexTurn(turnId, counted).PureSeconds!.Value - 9.5495316) < 1e-6, "a re-sent wait record is not counted twice");

        // Heavy use: the span tree is evicted long before the requests are; a resolved request must survive that.
        var busy = new TelemetryStore(maxSpans: 40);
        busy.Ingest(Spans(Span("b1", "bw", "responses_websocket.stream_request", 1, 3), Span("bw", "bt", "stream_request", 0.99, 1)));
        busy.Ingest(Spans(Span("bt", null, "try_run_sampling_request", 0.9, 3.1, "turn-busy")));
        for (var batch = 0; batch < 10; batch++)
            busy.Ingest(Spans(Enumerable.Range(0, 30).Select(n => Span($"fill-{batch}-{n}", null, "fs.get_metadata", 5, 5.001)).ToArray()));
        Check(busy.CodexTurn("turn-busy", [Counted(3.01, 100)]) is { Seconds: 2, Requests: 1 }, "request keeps its turn after the surrounding spans are evicted");
        var busyCache = Path.Combine(scratch, "telemetry-busy-cache.json");
        busy.SaveTo(busyCache);
        var busyRestored = new TelemetryStore(maxSpans: 40);
        busyRestored.LoadFrom(busyCache);
        for (var batch = 0; batch < 10; batch++)
            busyRestored.Ingest(Spans(Enumerable.Range(0, 30).Select(n => Span($"again-{batch}-{n}", null, "fs.get_metadata", 5, 5.001)).ToArray()));
        busyRestored.SaveTo(busyCache);
        Check(new TelemetryStore().LoadFrom(busyCache) && File.ReadAllText(busyCache).Contains("turn-busy"), "and stays in the cache across restarts under heavy use");

        // ----- presentation and two-line measurement
        TokenSnapshot Snap(TurnStatistics t) => new("id", "", 0, 0, 0, 0, 0, 0, 0, default) { Turn = t, App = HostApp.Claude };
        var two = TurnPresentation.Create(Snap(turn));
        var missing = TurnPresentation.Create(Snap(turn with { ModelSeconds = null, ModelNote = ModelRate.Waiting }));
        Check(two.SubLine == "모델 71.50 TPS" && two.SubLine2 == "생성 100.67 TPS" && two.Secondary.Value == "22.06",
            "upper line keeps whole-turn average, then 모델 (requests) and 생성 (generating only)");
        Check(TurnPresentation.Create(Snap(turn with { PureSeconds = null })).SubLine2 == "생성 —" && TurnPresentation.Create(Snap(turn) with { App = HostApp.Codex }).SubLine2 == "생성 100.67 TPS"
            && TurnPresentation.Create(Snap(turn) with { App = HostApp.None }).SubLine2 is null,
            "생성 shows — while missing, and is drawn for Codex too");
        Check(TurnPresentation.ChooseLines(two, false, false) is { SubLine: "모델 71.50 TPS", SubLine2: "생성 100.67 TPS" }
            && TurnPresentation.ChooseLines(two) == two, "settings: the rate lines cannot be switched off");
        Check(missing.SubLine == "모델 —" && missing.Secondary.Value == "22.06", "missing telemetry shows 모델 — and leaves the upper line alone");
        Check(missing.ExpandedRows.Any(r => r.Value == ModelRate.Waiting) && two.ExpandedRows.Any(r => r.Value == "71.50 TPS")
            && two.ExpandedRows.Any(r => r.Value == "100.67 TPS") && two.ExpandedRows.Any(r => r.Value.EndsWith("요청마다 첫 응답 대기 제외")),
            "details explain the value or why it is missing");
        Check(TurnPresentation.Create(Snap(turn with { Seconds = null })).SubLine is null, "status-only capsule has no second line");
        foreach (var dpi in new uint[] { 96, 144, 216 })
        foreach (var scale in new[] { 75, 100, 130 })
        {
            var one = AutoCapsule.Measure(two with { SubLine = null }, dpi, scale);
            var both = AutoCapsule.Measure(two, dpi, scale);
            var wide = AutoCapsule.Measure(two with { SubLine = "모델 123456789.12 TPS · 아주 긴 아래 줄 아주 긴 아래 줄 아주 긴 아래 줄" }, dpi, scale);
            Check(both.SubHeight > 0 && both.Size.Height >= both.MainHeight + both.SubHeight && both.Size.Height > one.Size.Height
                && both.Size.Width >= one.Size.Width, $"second line adds measured height at {dpi} DPI / {scale}%");
            Check(wide.Size.Width > both.Size.Width, $"longer lower line widens the capsule at {dpi} DPI / {scale}%");
        }
        var host = new CodexWindowInfo(new IntPtr(1), new IntRect(0, 0, 1920, 1080), new IntRect(0, 0, 1920, 1080), null,
            new IntRect(0, 0, 1920, 1080), 96, new WindowChromeMetrics(36, 24, 4, 4, 4));
        foreach (var (name, presentation) in new[] { ("two-line", two), ("missing", missing) })
        {
            using var form = new TokenStripForm();
            form.SetPresentation(presentation);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            var layout = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.InsideTopRight, false, 0, false, Presentation: presentation));
            form.ApplyLayout(layout);
            Check(layout.CapsuleBounds.Height == AutoCapsule.Measure(presentation, 96, layout.ScalePercent).Size.Height, $"layout uses the two-line height ({name})");
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(scratch, $"dual-rate-{name}.png"));
        }

        // ----- receiver end to end (test port), and privacy of what is kept
        var received = new TelemetryStore();
        using (var receiver = new TelemetryReceiver(received, 43917))
        {
            Check(receiver.Start(), "loopback receiver starts");
            using var http = new HttpClient();
            HttpResponseMessage Post(string path, string body, string type = "application/json") =>
                http.PostAsync(TelemetryReceiver.EndpointFor(43917) + path, new StringContent(body, Encoding.UTF8, type)).GetAwaiter().GetResult();
            Check((int)Post("/v1/traces", Spans(ClaudeRequest("req_http", 2000, ttft: 500)).GetRawText()).StatusCode == 200, "trace export accepted");
            Check(received.ClaudeTurn(["req_http"]) is { Seconds: 2, PureSeconds: 1.5 }, "exported request reaches the store");
            Check((int)Post("/v1/metrics", "{}").StatusCode == 404, "unneeded signal rejected");
            Check((int)Post("/v1/logs", "x", "application/x-protobuf").StatusCode == 415, "non-JSON export rejected instead of misread");
            Check((int)Post("/v1/traces", "{}", "text/plain").StatusCode == 415 && TelemetryReceiver.IsJson("application/json; charset=utf-8")
                && !TelemetryReceiver.IsJson("text/plain; x=json"), "receiver: only application/json is taken, so a web page cannot post to it");
            Check((int)Post("/v1/logs", "{ broken").StatusCode == 400, "malformed body rejected");
            using var second = new TelemetryReceiver(new TelemetryStore(), 43917);
            Check(!second.Start() && second.Error is not null, "port already in use is reported, not silently moved");
        }

        // ----- settings wiring on scratch copies
        const string endpoint = "http://127.0.0.1:43189";
        var claudeHome = Path.Combine(scratch, "telemetry-claude"); Directory.CreateDirectory(claudeHome);
        var settings = Path.Combine(claudeHome, "settings.json");
        File.WriteAllText(settings, "{\n  \"autoUpdatesChannel\": \"latest\",\n  \"env\": { \"MY_VAR\": \"keep\" }\n}\n");
        Check(TelemetrySetup.ClaudeState(settings, endpoint) == TelemetryLinkState.Off, "Claude: off before connecting");
        Check(TelemetrySetup.SetClaude(settings, endpoint, true) == TelemetryLinkState.On, "Claude: connect adds the exporter keys");
        var enabled = JsonDocument.Parse(File.ReadAllText(settings)).RootElement;
        Check(enabled.GetProperty("autoUpdatesChannel").GetString() == "latest" && enabled.GetProperty("env").GetProperty("MY_VAR").GetString() == "keep"
            && enabled.GetProperty("env").GetProperty("OTEL_LOG_USER_PROMPTS").GetString() == "0", "Claude: other settings kept, prompt logging off");
        Check(TelemetrySetup.SetClaude(settings, endpoint, false) == TelemetryLinkState.Off, "Claude: disconnect");
        var disabled = JsonDocument.Parse(File.ReadAllText(settings)).RootElement;
        Check(disabled.GetProperty("env").EnumerateObject().Select(p => p.Name).SequenceEqual(["MY_VAR"]), "Claude: only the added keys are removed");
        File.WriteAllText(settings, "{ \"env\": { \"OTEL_EXPORTER_OTLP_ENDPOINT\": \"http://collector.example:4318\" } }");
        Check(TelemetrySetup.SetClaude(settings, endpoint, true) == TelemetryLinkState.Conflict
            && File.ReadAllText(settings).Contains("collector.example"), "Claude: existing exporter is not overwritten");
        Check(TelemetrySetup.ClaudeState(Path.Combine(scratch, "no-such-app", "settings.json"), endpoint) == TelemetryLinkState.AppMissing, "missing app is not created");

        var codexHome = Path.Combine(scratch, "telemetry-codex"); Directory.CreateDirectory(codexHome);
        var toml = Path.Combine(codexHome, "config.toml");
        const string original = "model = \"gpt\"\r\n\r\n[features]\r\nfoo = true\r\n";
        File.WriteAllText(toml, original);
        Check(TelemetrySetup.SetCodex(toml, endpoint, true) == TelemetryLinkState.On, "Codex: connect appends a managed [otel] block");
        var withBlock = File.ReadAllText(toml);
        Check(withBlock.StartsWith(original, StringComparison.Ordinal) && withBlock.Contains("[otel]") && withBlock.Contains("log_user_prompt = false")
            && withBlock.Contains(endpoint + "/v1/traces") && !withBlock.Contains("metrics_exporter"), "Codex: existing config kept; no prompt or metrics export");
        Check(TelemetrySetup.SetCodex(toml, endpoint, true) == TelemetryLinkState.On && File.ReadAllText(toml) == withBlock, "Codex: connecting twice changes nothing");
        Check(TelemetrySetup.SetCodex(toml, endpoint, false) == TelemetryLinkState.Off && File.ReadAllText(toml) == original, "Codex: disconnect restores the file exactly");
        // Codex puts a table it adds later ahead of the closing marker comment, i.e. inside the block.
        TelemetrySetup.SetCodex(toml, endpoint, true);
        const string codexOwn = "[tui]\r\nscreen_reader_detection_done = true\r\n";
        var block = File.ReadAllText(toml);
        var end = block.LastIndexOf("# <<<", StringComparison.Ordinal);
        File.WriteAllText(toml, block[..end] + "\r\n" + codexOwn + block[end..]);
        Check(TelemetrySetup.CodexState(toml, endpoint) == TelemetryLinkState.On
            && TelemetrySetup.SetCodex(toml, endpoint, false) == TelemetryLinkState.Off
            && File.ReadAllText(toml) == original + "\r\n" + codexOwn,
            "Codex: disconnect keeps what Codex wrote inside the block, only the block's own lines go");
        File.WriteAllText(toml, original);
        File.WriteAllText(toml, original + "[otel]\r\nexporter = \"none\"\r\n");
        Check(TelemetrySetup.SetCodex(toml, endpoint, true) == TelemetryLinkState.Conflict && !File.ReadAllText(toml).Contains("TokenOverlay"),
            "Codex: existing [otel] section is not overwritten");
        // ----- cache that survives an overlay restart
        var cachePath = Path.Combine(scratch, "telemetry-cache", "model-rate-cache.json");
        if (Directory.Exists(Path.GetDirectoryName(cachePath)!)) Directory.Delete(Path.GetDirectoryName(cachePath)!, recursive: true);
        Check(store.Dirty && codex.Dirty, "new telemetry marks the store for saving");
        var waitCache = Path.Combine(scratch, "telemetry-codex-wait-cache.json");
        File.WriteAllText(waitCache, codex.Export());
        var waitRestored = new TelemetryStore();
        Check(waitRestored.LoadFrom(waitCache) && waitRestored.CodexTurn(turnId, counted).PureSeconds is double restoredPure
            && Math.Abs(restoredPure - 9.5495316) < 1e-6, "Codex 생성 survives an overlay restart");
        store.SaveTo(cachePath);
        var saved = File.ReadAllText(cachePath);
        Check(!store.Dirty && saved.Contains("req_a") && !saved.Contains("example.com") && !saved.Contains("user.") && !saved.Contains("prompt"),
            "cache holds request ids and durations, nothing identifying");
        var restoredClaude = new TelemetryStore();
        Check(restoredClaude.LoadFrom(cachePath) && !restoredClaude.Dirty, "cache loads into a fresh store");
        Check(Math.Abs(restoredClaude.ClaudeTurn(["req_a", "req_b"]).PureSeconds!.Value - 3.0) < 1e-9 && restoredClaude.ClaudeTurn(["req_other"]).Seconds is null,
            "Claude model rate is the same after a restart");
        codex.SaveTo(cachePath);
        static bool Cached(string path, string section, string id)
        {
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            return saved.RootElement.GetProperty(section).EnumerateArray().Any(row => row[0].GetString() == id);
        }
        Check(!Cached(cachePath, "codexStreams", "pre"), "requests not tied to a turn (prewarm) are not saved as a turn's requests");

        // Restart in the middle of a turn: its requests do not know their turn yet and must still survive.
        var midTurn = new TelemetryStore();
        midTurn.Ingest(Spans(Span("m1", "mw", "responses_websocket.stream_request", 1, 3), Span("mw", "mt", "stream_request", 0.99, 1)));
        var midCache = Path.Combine(scratch, "telemetry-midturn-cache.json");
        midTurn.SaveTo(midCache);
        var midRestored = new TelemetryStore();
        midRestored.LoadFrom(midCache);
        Check(Cached(midCache, "codexPending", "m1") && midRestored.CodexTurn("turn-mid", [Counted(3.01, 100)]).Seconds is null,
            "request of a running turn is cached without a turn");
        midRestored.Ingest(Spans(Span("mt", null, "try_run_sampling_request", 0.9, 3.1, "turn-mid")));
        Check(midRestored.CodexTurn("turn-mid", [Counted(3.01, 100)]) is { Seconds: 2, Requests: 1 }, "and is counted once the turn's span arrives after the restart");
        midRestored.SaveTo(midCache);
        Check(Cached(midCache, "codexStreams", "m1") && !Cached(midCache, "codexPending", "m1"), "then it moves to the turn's saved requests");
        var restoredCodex = new TelemetryStore();
        restoredCodex.LoadFrom(cachePath);
        var restoredRate = restoredCodex.CodexTurn(turnId, counted);
        Check(restoredRate is { Requests: 2 } && Math.Abs(restoredRate.Seconds!.Value - 11.8495316) < 1e-6, "Codex model rate is the same after a restart");
        restoredCodex.Ingest(Spans(Span("s2", "w2", "responses_websocket.stream_request", 7.8, 16.7351818)));
        Check(Math.Abs(restoredCodex.CodexTurn(turnId, counted).Seconds!.Value - 11.8495316) < 1e-6,
            "records re-sent after a restart are still counted once");
        restoredCodex.LoadFrom(cachePath);
        Check(restoredCodex.CodexTurn(turnId, counted) is { Requests: 2 }, "loading the same cache twice changes nothing");
        var olderCache = Path.Combine(Path.GetDirectoryName(cachePath)!, "older.json");
        File.WriteAllText(olderCache, "{\"version\":1,\"claude\":[],\"codexStreams\":[[\"a\",\"turn-old\",2000],[\"b\",\"turn-old\",3000]],\"codexCompletions\":[[\"c\",1,5,\"k\"]]}");
        var older = new TelemetryStore();
        Check(older.LoadFrom(olderCache) && older.CodexTurn("turn-old", [Counted(1, 10), Counted(2, 20)]) is { Seconds: 5, Requests: 2 },
            "entries cached before end times were kept still rate a turn when the counts agree");
        Check(older.CodexTurn("turn-old", [Counted(1, 10)]).Seconds is null, "and stay empty when they do not");
        File.WriteAllText(cachePath, "{ broken");
        Check(!new TelemetryStore().LoadFrom(cachePath) && !new TelemetryStore().LoadFrom(cachePath + ".missing"), "damaged or missing cache just starts empty");

        // ----- recent-turn list (Ctrl + pointer over the capsule)
        var many = new ClaudeTurnReader();
        var manyStore = new TelemetryStore();
        for (var n = 1; n <= 7; n++)
        {
            many.ProcessLine(User($"h{n}", n * 100));
            many.ProcessLine(Assistant($"hm{n}", $"hreq{n}", n * 100 + 10, "end_turn", n * 100));
            if (n != 4) manyStore.Ingest(Spans(ClaudeRequest($"hreq{n}", 5000, ttft: 1000)));
        }
        many.ProcessLine(User("h8", 900));
        many.ProcessLine(Assistant("hm8", "hreq8", 905, "tool_use", 50));
        var recent = many.Recent(TurnHistory.Size);
        Check(recent.Select(r => r.Turn.TurnId).SequenceEqual(["h7", "h6", "h5", "h4", "h3"]), "Claude: five most recent completed turns, newest first, running turn excluded");
        var listed = new TurnHistory(recent.Select(r => r.Turn.With(manyStore.ClaudeTurn(r.RequestIds))).ToArray());
        var rows = TurnPresentation.HistoryRows(listed);
        Check(rows.Count == 5 && rows[0] is { When: "1턴 전", Seconds: "10초", Rate: "70.00", Model: "140.00" }, "history row carries that turn's own whole-turn and model rates");
        Check(OverlayPresentationBuilder.FormatDuration(4.4) == "4초" && OverlayPresentationBuilder.FormatDuration(4.5) == "5초"
            && OverlayPresentationBuilder.FormatDuration(59.6) == "1분 0초" && OverlayPresentationBuilder.FormatDuration(62.3) == "1분 2초"
            && OverlayPresentationBuilder.FormatDuration(3725) == "1시간 2분" && OverlayPresentationBuilder.FormatDuration(3599.4) == "59분 59초" && OverlayPresentationBuilder.FormatDuration(-1) == "0초",
            "durations: whole seconds rounded, minutes and hours spelled out");
        Check(rows[3] is { When: "4턴 전", Rate: "40.00", Model: "—" }, "turn without telemetry shows — in its own row only");
        Check(listed == new TurnHistory(many.Recent(TurnHistory.Size).Select(r => r.Turn.With(manyStore.ClaudeTurn(r.RequestIds))).ToArray())
            && listed != new TurnHistory(listed.Turns.Take(4).ToArray()), "unchanged history compares equal, changed history does not");
        var fastRows = TurnPresentation.HistoryRows(new TurnHistory([
            listed.Turns[0] with { FastMode = "on" }, listed.Turns[1] with { FastMode = "mixed" }, listed.Turns[2] with { FastMode = "off" }]));
        Check(fastRows[0].Rate == "⚡ 70.00" && fastRows[1].Rate == "⚡* 60.00" && fastRows[2].Rate == "50.00" && !fastRows[0].Model.Contains('⚡'),
            "history rows carry each turn's own Fast mark on the whole-turn rate");
        string CodexEvent(string type, string? id = null, long output = 0, long? ms = null) => JsonSerializer.Serialize(new
        {
            timestamp = "2026-10-05T00:00:00Z", type = "event_msg",
            payload = new { type, turn_id = id, duration_ms = ms, info = new { total_token_usage = new { input_tokens = output * 10, output_tokens = output } } }
        });
        var codexReader = new TurnStatisticsReader();
        for (var n = 1; n <= 6; n++)
        {
            codexReader.ProcessLine(CodexEvent("task_started", $"c{n}"));
            codexReader.ProcessLine(CodexEvent("token_count", output: n * 100));
            codexReader.ProcessLine(CodexEvent("task_complete", $"c{n}", ms: 2000));
        }
        codexReader.ProcessLine(CodexEvent("task_started", "c7"));
        var codexRecent = codexReader.Recent(TurnHistory.Size);
        Check(codexRecent.Select(r => r.Turn.TurnId).SequenceEqual(["c6", "c5", "c4", "c3", "c2"]) && codexRecent.All(r => r.Turn is { Output: 100, Seconds: 2 }),
            "Codex: five most recent completed turns with per-turn token deltas");
        var withHistory = TurnPresentation.Create(Snap(turn) with { History = listed });
        Check(withHistory.History.Count == 5 && TurnPresentation.Create(Snap(turn)).History.Count == 0, "presentation carries the history rows");
        var compact = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.InsideTopRight, true, 6, false, Presentation: withHistory, CompactPanel: true));
        var full = OverlayLayoutCalculator.Calculate(new(host, AnchorMode.InsideTopRight, true, 6, false, Presentation: withHistory));
        Check(compact.State == OverlayVisualState.Expanded && compact.PanelBounds.Height < full.PanelBounds.Height
            && compact.PanelBounds.Width >= compact.CapsuleBounds.Width, "history panel opens below the capsule without the details header area");
        using (var historyForm = new TokenStripForm())
        {
            historyForm.SetPresentation(withHistory);
            historyForm.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            historyForm.SetShowHistory(true);
            historyForm.ApplyLayout(compact);
            using var bitmap = new Bitmap(historyForm.Width, historyForm.Height);
            historyForm.DrawToBitmap(bitmap, new Rectangle(0, 0, historyForm.Width, historyForm.Height));
            bitmap.Save(Path.Combine(scratch, "dual-rate-history.png"));
        }

        // ----- review regressions: re-sent Codex records, and requests that look alike
        var resend = new TelemetryStore();
        var firstRequest = Spans(Span("d1", "dt", "responses_websocket.stream_request", 1, 3), Span("dt", null, "try_run_sampling_request", 0.9, 3.1, "turn-dup"));
        resend.Ingest(firstRequest);
        var single = new[] { Counted(3.01, 100) };
        Check(resend.CodexTurn("turn-dup", single) is { Seconds: 2, Requests: 1 }, "single request: 2 s, 1 request");
        resend.Ingest(firstRequest);
        resend.Ingest(firstRequest);
        Check(resend.CodexTurn("turn-dup", single) is { Seconds: 2, Requests: 1, Note: null }, "re-sent request record is not counted twice");
        var secondRequest = Spans(Span("d2", "dt2", "responses_websocket.stream_request", 4, 7), Span("dt2", null, "try_run_sampling_request", 3.9, 7.1, "turn-dup"));
        var pair = new[] { Counted(3.01, 100), Counted(7.01, 100) };
        Check(resend.CodexTurn("turn-dup", pair).Seconds is null, "second response without its request is still partial");
        resend.Ingest(secondRequest);
        Check(resend.CodexTurn("turn-dup", pair) is { Seconds: 5, Requests: 2, Note: null },
            "two real requests with the same token count are both counted and summed");
        resend.Ingest(secondRequest);
        resend.Ingest(firstRequest);
        Check(resend.CodexTurn("turn-dup", pair) is { Seconds: 5, Requests: 2 }, "late re-sends of both records change nothing");

        // ----- review regressions: Claude keys that were there before connecting
        string Fresh(string name, string json)
        {
            var home = Path.Combine(scratch, name);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
            Directory.CreateDirectory(home);
            var file = Path.Combine(home, "settings.json");
            File.WriteAllText(file, json);
            return file;
        }
        static Dictionary<string, string> Env(string file) => JsonDocument.Parse(File.ReadAllText(file)).RootElement.TryGetProperty("env", out var e)
            ? e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : new();
        var pre = Fresh("telemetry-claude-pre", "{ \"env\": { \"OTEL_LOG_USER_PROMPTS\": \"0\", \"MY_VAR\": \"keep\" } }");
        Check(TelemetrySetup.SetClaude(pre, endpoint, true) == TelemetryLinkState.On, "pre-existing key: connect");
        var record = JsonDocument.Parse(File.ReadAllText(TelemetrySetup.ClaudeOwnershipPath(pre))).RootElement.GetProperty("added");
        Check(record.EnumerateObject().Count() == 11 && !record.TryGetProperty("OTEL_LOG_USER_PROMPTS", out _), "only the 11 keys actually added are recorded as ours");
        // The record is a file, so a later run of the program (a restart) disconnects from it.
        Check(TelemetrySetup.SetClaude(pre, endpoint, false) == TelemetryLinkState.Off, "pre-existing key: disconnect after restart");
        Check(Env(pre).OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value).SequenceEqual(["MY_VAR=keep", "OTEL_LOG_USER_PROMPTS=0"]),
            "key that existed before connecting survives disconnecting");
        Check(!File.Exists(TelemetrySetup.ClaudeOwnershipPath(pre)), "ownership record removed with the keys");

        var edited = Fresh("telemetry-claude-edited", "{ \"theme\": \"dark\" }");
        TelemetrySetup.SetClaude(edited, endpoint, true);
        File.WriteAllText(edited, File.ReadAllText(edited).Replace("\"OTEL_LOGS_EXPORT_INTERVAL\": \"1000\"", "\"OTEL_LOGS_EXPORT_INTERVAL\": \"5000\""));
        TelemetrySetup.SetClaude(edited, endpoint, false);
        Check(Env(edited).Select(p => p.Key + "=" + p.Value).SequenceEqual(["OTEL_LOGS_EXPORT_INTERVAL=5000"]), "value the user changed afterwards is kept; the rest is removed");
        var created = Fresh("telemetry-claude-created", "{ \"theme\": \"dark\" }");
        TelemetrySetup.SetClaude(created, endpoint, true);
        TelemetrySetup.SetClaude(created, endpoint, false);
        Check(!JsonDocument.Parse(File.ReadAllText(created)).RootElement.TryGetProperty("env", out _), "env block created by connecting is removed again");
        var emptyEnv = Fresh("telemetry-claude-empty", "{ \"env\": {} }");
        TelemetrySetup.SetClaude(emptyEnv, endpoint, true);
        TelemetrySetup.SetClaude(emptyEnv, endpoint, false);
        Check(JsonDocument.Parse(File.ReadAllText(emptyEnv)).RootElement.TryGetProperty("env", out var kept) && !kept.EnumerateObject().Any(), "env block that existed before is left in place");

        var legacyJson = "{ \"env\": { " + string.Join(", ", TelemetrySetup.ClaudeEnvironment(endpoint).Select(p => $"\"{p.Key}\": \"{p.Value}\"")) + " } }";
        var legacy = Fresh("telemetry-claude-legacy", legacyJson);
        Check(TelemetrySetup.ClaudeState(legacy, endpoint) == TelemetryLinkState.On && TelemetrySetup.SetClaude(legacy, endpoint, true) == TelemetryLinkState.On
            && !File.Exists(TelemetrySetup.ClaudeOwnershipPath(legacy)), "connection made by an older build is not claimed as ours");
        Check(TelemetrySetup.SetClaude(legacy, endpoint, false) == TelemetryLinkState.Off, "older build's connection: disconnect");
        Check(Env(legacy).Count == 11 && !Env(legacy).ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT") && Env(legacy)["OTEL_LOG_USER_PROMPTS"] == "0",
            "without a record only this receiver's endpoint is removed; other keys are not guessed at");
        Check(TelemetrySetup.SetClaude(legacy, endpoint, false) == TelemetryLinkState.Off && Env(legacy).Count == 11, "disconnecting again changes nothing");
        return new { Passed = results.Count, Checks = results };
    }
}
