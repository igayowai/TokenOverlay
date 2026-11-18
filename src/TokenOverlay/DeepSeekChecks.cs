namespace TokenOverlay;

// DeepSeek rows: reading DSH's multi-frame session files, a task's rates counted like a Claude turn, and the capsule's
// two worker rows (drawn for review).
internal static class DeepSeekChecks
{
    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        static byte[] Frame(string text) { using var c = new ZstdSharp.Compressor(); return c.Wrap(System.Text.Encoding.UTF8.GetBytes(text)).ToArray(); }
        static string Message(long time, long output, string? model = "deepseek-flash", long? firstPiece = null) =>
            "{\"type\":\"assistant/message\",\"seq\":0,\"time\":" + time + ",\"data\":{\"message\":{\"role\":\"assistant\""
            + (model is null ? "" : ",\"source\":{\"kind\":\"model\",\"provider\":\"" + (model == "deepseek-flash" ? "deepseek-official" : "openrouter") + "\",\"model\":\"" + model + "\"}")
            + "},\"usage\":{\"outputTokens\":" + output + ",\"inputTokens\":100,\"cacheReadTokens\":900},\"stream\":[{\"type\":\"reasoning-chunks\",\"time0\":" + (firstPiece ?? time - 1000) + "},{\"type\":\"chunk\",\"time\":" + (time - 100) + "}]}}";
        static string Event(string type, long time) => "{\"type\":\"" + type + "\",\"seq\":0,\"time\":" + time + ",\"data\":{}}";

        // Task 1, 0 → 10 s: two requests, 1 → 3 s (400 tokens) and 6 → 8 s (600 tokens); tools between them. Each
        // waits 1 s for its first piece and then generates for 1 s. TPS 1000 ÷ 10 s = 100; 모델 1000 ÷ 4 s = 250;
        // 생성 1000 ÷ 2 s = 500.
        var task1 = string.Join("\n", Event("turn/start", 0), Event("step/start", 1000), Message(3000, 400), Event("step/end", 3000),
            Event("step/start", 6000), Message(8000, 600), Event("step/end", 8000), Event("turn/end", 10000)) + "\n";
        // Task 2, 20 → 25 s: one request 21 → 23 s (300 tokens) on another model, generating 22 → 23 s. TPS 60, 모델 150, 생성 300.
        var task2 = string.Join("\n", Event("turn/start", 20000), Event("step/start", 21000), Message(23000, 300, "deepseek-v4-pro"),
            Event("turn/end", 25000)) + "\n";
        var one = DeepSeekRateSource.LastTurn(Frame(task1));
        Check(one is { Output: 1000, Requests: 2 } && Math.Abs(one.TokensPerSecond - 100) < 0.01 && Math.Abs(one.RequestTokensPerSecond - 250) < 0.01
            && Math.Abs(one.ModelTokensPerSecond - 500) < 0.01,
            "a task: output over the whole turn, over its requests (모델, first-piece wait in) and over their generating time (생성)");
        var two = Frame(task1).Concat(Frame(task2)).ToArray();
        var last = DeepSeekRateSource.LastTurn(two);
        Check(last is { Output: 300, Model: "deepseek-v4-pro" } && Math.Abs(last.TokensPerSecond - 60) < 0.01 && Math.Abs(last.RequestTokensPerSecond - 150) < 0.01 && Math.Abs(last.ModelTokensPerSecond - 300) < 0.01,
            "the last finished task of a two-frame file is read (frames are joined, not just the first)");
        // A task still running (no turn/end yet) leaves the finished one on screen.
        var running = two.Concat(Frame(Event("turn/start", 30000) + "\n" + Event("step/start", 30100) + "\n" + Message(31000, 900) + "\n")).ToArray();
        Check(DeepSeekRateSource.LastTurn(running)?.Output == 300, "a task still running leaves the last finished one");
        Check(DeepSeekRateSource.LastTurn(two.Concat(Frame(task1).Take(12)).ToArray())?.Output == 300, "a frame still being written leaves the lines before it");
        Check(last!.Line == "DeepSeek V4 Pro 60.00 TPS" && last.TtftLine == "모델 150.00 TPS" && last.PureLine == "생성 300.00 TPS"
            && (last with { Model = "deepseek-flash" }).Line == "DeepSeek V4.1 Flash 60.00 TPS" && (last with { Model = null }).Line == "DeepSeek 60.00 TPS",
            "row text; deepseek-flash reads as DSH's catalog name V4.1 Flash");
        Check(one is { CacheRead: 1800, CacheMiss: 200 } && one.CacheLine == "캐시히트 90.0%" && last is { CacheRead: 900, CacheMiss: 100 }
            && (last with { CacheRead = 41_280, CacheMiss = 1_410 }).CacheLine == "캐시히트 96.7%"
            && (last with { CacheRead = 99_999, CacheMiss = 1 }).CacheLine == "캐시히트 99.9%" && (last with { CacheMiss = 0 }).CacheLine == "캐시히트 100.0%"
            && (last with { CacheRead = 0, CacheMiss = 0 }).CacheLine is null,
            "cache hit: read ÷ (read + missed) over the task to one decimal, 100.0% only with no miss, no line without cache numbers");
        // A third task on two routes: two requests through one, one through the other.
        var task3 = string.Join("\n", Event("turn/start", 40000), Event("step/start", 41000), Message(43000, 100, "deepseek-v4-pro"),
            Event("step/start", 44000), Message(46000, 100), Event("step/start", 47000), Message(49000, 100, "deepseek-v4-pro"), Event("turn/end", 50000)) + "\n";
        Check(one!.ProviderLines == "deepseek-official" && last.ProviderLines == "openrouter"
            && DeepSeekRateSource.LastTurn(two.Concat(Frame(task3)).ToArray())?.ProviderLines == "openrouter\ndeepseek-official"
            && (last with { Providers = [] }).ProviderLines is null,
            "provider: the task's routes as DSH names them, the most used first, one per line");
        var stacked = AutoCapsule.WorkerRows(TurnPresentation.Create(Live(null)) with { WorkerSubLine = "모델 1.00 TPS", WorkerThirdLine = "생성 2.00 TPS", WorkerProvider = "a\nb\nc\nd" });
        Check(stacked is [("a", "모델 1.00 TPS"), ("b", "생성 2.00 TPS"), ("c", ""), ("d", "")]
            && AutoCapsule.WorkerRows(TurnPresentation.Create(Live(null)) with { WorkerSubLine = "모델 1.00 TPS", WorkerCacheLine = "캐시히트 9.0%" }) is [("", "모델 1.00 TPS"), ("", "캐시히트 9.0%")],
            "worker rows: providers at the left beside the rate lines, extra rows for more providers than lines");
        Check(AutoCapsule.SplitSubLine("DeepSeek V4 Pro 60.00 TPS") == ("DeepSeek V4 Pro", "60.00", "TPS")
            && AutoCapsule.SplitSubLine("모델 71.50 TPS") == ("모델", "71.50", "TPS") && AutoCapsule.SplitSubLine("모델 —") == ("모델", "—", ""),
            "drawn as label, bold number, unit; the model line splits as before");

        // A scratch folder laid out like DSH's: the rows show while the task is recent, and go after 10 minutes.
        var root = Path.Combine(Path.GetTempPath(), "TokenOverlay-deepseek-check");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var session = Path.Combine(root, "--C-work--", "session-1");
        Directory.CreateDirectory(session);
        File.WriteAllBytes(Path.Combine(session, "session.v4.jsonl.zstd"), two);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(25000);
        // Task 2 began at 20 s; the session that started the worker logged its call at 19 s.
        IReadOnlyList<DateTimeOffset> called = [DateTimeOffset.FromUnixTimeMilliseconds(-300_000), DateTimeOffset.FromUnixTimeMilliseconds(19000)];
        Check(new DeepSeekRateSource(root, Path.Combine(root, "missing")).Latest(at.AddMinutes(1), called) is { Output: 300, Started.Second: 20 },
            "a recent task is shown on the session that started the worker (a missing folder is skipped)");
        Check(new DeepSeekRateSource(root).Latest(at.AddMinutes(11), called) is null, "a task older than 10 minutes is not shown");
        Check(new DeepSeekRateSource(root).Latest(at.AddMinutes(1), []) is null, "a session that never started the worker shows no worker rows");
        Check(new DeepSeekRateSource(root).Latest(at.AddMinutes(1), [DateTimeOffset.FromUnixTimeMilliseconds(-60_000)]) is null
            && new DeepSeekRateSource(root).Latest(at.AddMinutes(1), [DateTimeOffset.FromUnixTimeMilliseconds(21000)]) is null,
            "a task that began more than a minute after the call, or before it, belongs to another session");
        Check(DeepSeekRateSource.StartsWorker(@"pwsh -NoProfile -File 'C:\Users\x\Documents\Claude\dsh-worker\run-worker.ps1' -WorkDir 'C:\w' -TaskFile 't.txt'")
            && DeepSeekRateSource.StartsWorker(@"& 'C:\Users\x\Documents\Claude\dsh-worker\node_modules\.bin\dsh.cmd' --profile headless --json ""fix""")
            && !DeepSeekRateSource.StartsWorker(@"Get-Content C:\Users\x\Documents\Claude\dsh-worker\README.md")
            && !DeepSeekRateSource.StartsWorker("pwsh -File run-worker.ps1"),
            "a worker call names the worker folder and one of its entry points; reading its files is no call");
        // A sub-agent of the session launched the worker: its log sits in <session>/subagents/; another session has none.
        var claudeLogs = Path.Combine(root, "claude");
        Directory.CreateDirectory(Path.Combine(claudeLogs, "s1", "subagents"));
        var agentCall = System.Text.Json.JsonSerializer.Serialize(new { type = "assistant", isSidechain = true, timestamp = "2026-10-20T10:11:01Z",
            message = new { role = "assistant", content = new object[] { new { type = "tool_use", name = "PowerShell",
                input = new { command = @"pwsh -NoProfile -File 'C:\Users\x\Documents\Claude\dsh-worker\run-worker.ps1' -WorkDir 'C:\w' -TaskFile 't.txt'" } } } } });
        File.WriteAllText(Path.Combine(claudeLogs, "s1", "subagents", "agent-a1.jsonl"), "{\"type\":\"user\"}\n" + agentCall + "\n");
        var subagentNow = DateTimeOffset.Parse("2026-10-20T10:12:00Z");
        var sources = new DeepSeekRateSource(root);
        Check(sources.SubagentCalls(Path.Combine(claudeLogs, "s1.jsonl"), subagentNow) is [var agentAt] && agentAt == DateTimeOffset.Parse("2026-10-20T10:11:01Z")
            && sources.SubagentCalls(Path.Combine(claudeLogs, "s2.jsonl"), subagentNow).Count == 0,
            "a worker launched by one of the session's sub-agents counts for that session, not for another");
        Directory.Delete(root, true);

        // The DSH desktop capsule (user, 2026-11-16): the chat in front read from a scratch ~/.dsh, turns counted as
        // OpenCode's are, the ring against DSH's automatic compaction point.
        Check(DeepSeekEvents.FrameEnds(two) is [var firstEnd, var bothEnd] && firstEnd == Frame(task1).Length && bothEnd == two.Length
            && DeepSeekEvents.FrameEnds(two.Concat(Frame(task1).Take(12)).ToArray()).Count == 2 && DeepSeekEvents.FrameEnds([]).Count == 0,
            "frame walk: each whole frame's end, a frame still being written left for later");
        var defaults = DeepSeekCompaction.Default;
        Check(DeepSeekCompaction.Point(1_000_000, defaults, 256_000) == 678_464 && DeepSeekCompaction.Point(1_000_000, defaults, 384_000) == 550_464
            && DeepSeekCompaction.Point(1_048_576, defaults, 256_000) == 727_040
            && DeepSeekCompaction.Point(1_048_576, defaults with { Ratio = 0.476837158203125 }, 256_000) == 500_000
            && DeepSeekCompaction.Point(1_000_000, defaults with { Ratio = 0.476837158203125 }, 384_000) == 476_837
            && DeepSeekCompaction.Point(1_000_000, defaults with { Auto = false }, 256_000) == 1_000_000 && DeepSeekCompaction.Point(0, defaults, 256_000) == 0
            && DeepSeekCompaction.OutputCap("opencode-go", "deepseek-v4.1-flash") == 384_000 && DeepSeekCompaction.OutputCap("deepseek-official", "deepseek-flash") == 256_000,
            "compaction point: floor(min(W × ratio, W − O − 65,536)), the window when auto is off (727,040 and 500,000 as DSH's own notes give)");
        Check(DeepSeekCompaction.Parse("- id: agent-default-model\n  config:\n    thresholdRatio: 0.3\n# note: thresholdRatio: 0.2\n"
                + "- id: \"compaction-basic\"\n  name: \"@deepseek-ai/dsh-compaction-basic\"\n  config:\n    thresholdRatio: 0.476837158203125 # 500k\n"
                + "    headroomTokens: 32768\n    auto: false\n- id: locale\n  config:\n    auto: true\n")
                == new DeepSeekCompaction.Setting(false, 0.476837158203125, 32_768)
            && DeepSeekCompaction.Parse("- id: compaction-basic\r\n  config:\r\n    thresholdRatio: 1.5\r\n") == defaults && DeepSeekCompaction.Parse("") == defaults,
            "profile: only the compaction-basic entry's ratio, headroom and auto are read; comments, other entries and a ratio out of range are not");
        // The first token as DSH times it: a block start or usage before it is no token; a folded text or tool-call run
        // counts from its first piece, a single delta from its own time.
        long? stepAt = 1000;
        long noWindow = 0;
        static string Streamed(string pieces) => "{\"type\":\"assistant/message\",\"seq\":0,\"time\":5000,\"data\":{\"usage\":{\"outputTokens\":10},\"stream\":[" + pieces + "]}}";
        var folded = DeepSeekEvents.Row(Streamed("{\"type\":\"chunk\",\"time\":1500,\"chunk\":{\"type\":\"block-start\",\"index\":0}},"
            + "{\"type\":\"tool-call-chunks\",\"time0\":2500,\"index\":0},{\"type\":\"text-chunks\",\"time0\":3000,\"index\":1}"), 1, ref stepAt, ref noWindow);
        stepAt = 1000;
        var single = DeepSeekEvents.Row(Streamed("{\"type\":\"chunk\",\"time\":1500,\"chunk\":{\"type\":\"text-delta\",\"text\":\"\"}},"
            + "{\"type\":\"chunk\",\"time\":1800,\"chunk\":{\"type\":\"usage\"}},{\"type\":\"chunk\",\"time\":2200,\"chunk\":{\"type\":\"reasoning-delta\",\"text\":\"a\"}}"), 2, ref stepAt, ref noWindow);
        Check(folded is { FirstToken: 2500, Created: 1000, Completed: 5000 } && single is { FirstToken: 2200 },
            "first token: block starts, usage and empty deltas are no token; a folded run counts from its first piece");
        Check(DeepSeekSessionSource.SessionTitle("긴 문서 요약 — DeepSeek Harness") == "긴 문서 요약" && DeepSeekSessionSource.SessionTitle("DeepSeek Harness") is null
            && DeepSeekSessionSource.SessionTitle(" — DeepSeek Harness") is null && DeepSeekSessionSource.SessionTitle("x - DeepSeek Harness") is null,
            "the window title less \" — DeepSeek Harness\" is the chat's title");

        var dsh = Path.Combine(Path.GetTempPath(), "TokenOverlay-dsh-capsule-check");
        if (Directory.Exists(dsh)) Directory.Delete(dsh, true);
        var projcache = Path.Combine(dsh, "storages", "session_projcache", "sessions");
        Directory.CreateDirectory(projcache);
        static string Cached(string title) => "{\"version\":7,\"record\":{\"rows\":{\"title\":{\"ver\":1,\"seq\":3,\"val\":\"" + title + "\"}}}}";
        File.WriteAllText(Path.Combine(projcache, "session-a.json"), Cached("긴 문서 요약"));
        File.WriteAllText(Path.Combine(projcache, "session-b.json"), Cached("두 개"));
        File.WriteAllText(Path.Combine(projcache, "c.json"), Cached("두 개"));
        File.WriteAllText(Path.Combine(projcache, "broken.json"), "{ not json");
        var chat = Path.Combine(dsh, "sessions", "--C-work--", "session-a");
        Directory.CreateDirectory(chat);
        var logFile = Path.Combine(chat, "session.v4.jsonl.zstd");
        var context = "{\"type\":\"request/context\",\"seq\":0,\"time\":500,\"data\":{\"provider\":\"deepseek-official\",\"model\":\"deepseek-flash\",\"contextWindow\":1000000}}\n";
        File.WriteAllBytes(logFile, Frame(context).Concat(Frame(task1)).ToArray());
        var profile = Path.Combine(dsh, "cordis.patch.yml");
        File.WriteAllText(profile, "- id: compaction-basic\n  config:\n    thresholdRatio: 0.476837158203125\n");
        var harness = new DeepSeekSessionSource(dsh, profile);
        var shownChat = harness.Read("긴 문서 요약");
        Check(shownChat is { Id: "session-a", Error: null, Snapshot: { App: HostApp.DeepSeekHarness, ThreadTitle: "긴 문서 요약", Turn: { Output: 1000, Running: false } turnA } }
            && Math.Abs(turnA.TokensPerSecond!.Value - 100) < 0.01 && Math.Abs(turnA.ModelTokensPerSecond!.Value - 250) < 0.01
            && Math.Abs(turnA.PureTokensPerSecond!.Value - 500) < 0.01,
            "a DSH chat: TPS over the turn, 모델 over its requests from their step start, 생성 from their first piece, both to the message");
        Check(shownChat.Snapshot!.ContextUsedTokens == 1000 && shownChat.Snapshot.ContextWindowTokens == 1_000_000
            && shownChat.Snapshot.RingNote == "Deepseek V4.1 Flash\n477K" && Math.Abs(shownChat.Snapshot.ContextFill!.Value - 1000 / 476_837.0) < 1e-12,
            "the ring: the last request's input with its cache reads against the compaction point (deepseek-flash named V4.1 Flash)");
        Check(TurnPresentation.Create(shownChat.Snapshot) is { SubLine: "모델 250.00 TPS", SubLine2: "생성 500.00 TPS" } dshShown && dshShown.Secondary.Value == "100.00",
            "the capsule shows TPS, 모델 and 생성 for DSH");
        // A new turn runs: the finished one stays, marked running; half a frame is left for the next read.
        var runningFrame = Frame(Event("turn/start", 30000) + "\n" + Event("step/start", 30100) + "\n");
        using (var append = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            append.Write(runningFrame.Concat(Frame(task2).Take(10)).ToArray());
        Check(harness.Read("긴 문서 요약").Snapshot?.Turn is { Output: 1000, Running: true },
            "a turn still running keeps the finished one on screen, marked running; a frame being written waits");
        var rest = Frame(Message(32000, 50) + "\n" + "{\"type\":\"turn/end\",\"seq\":0,\"time\":33000,\"data\":{\"turn\":3,\"reason\":{\"kind\":\"aborted\"}}}\n");
        File.WriteAllBytes(logFile, Frame(context).Concat(Frame(task1)).Concat(runningFrame).Concat(rest).ToArray());
        var stopped = harness.Read("긴 문서 요약").Snapshot;
        Check(stopped?.Turn is { Output: 50, State: "중단", Running: false } && stopped.History?.Turns.Count == 2,
            "new frames are read on; a turn the user stopped (aborted) reads 중단; the earlier turn moves to the list");
        File.WriteAllText(profile, "- id: compaction-basic\n  config:\n    auto: false\n");
        File.SetLastWriteTimeUtc(profile, DateTime.UtcNow.AddMinutes(1));
        Check(harness.Read("긴 문서 요약").Snapshot?.RingNote == "Deepseek V4.1 Flash\n1M", "a changed profile moves the ring without a new line");
        Check(harness.Read("두 개") is { Id: null, Error: "같은 제목의 DSH 세션이 여럿임" }
            && harness.Read("없는 제목") is { Id: null, Error: "현재 화면 제목과 일치하는 DSH 세션 없음" },
            "a title shared by two chats and a missing one are not guessed");
        Directory.Delete(dsh, true);

        // This PC's real sessions (numbers only): each one's last finished task as the overlay counts it.
        var real = new List<object>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var dir in new[] { Path.Combine(home, ".dsh", "sessions"), Path.Combine(home, "Documents", "Claude", "dsh-worker", "home", "sessions") })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "session.v4.jsonl.zstd", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
                if (DeepSeekRateSource.LastTurn(File.ReadAllBytes(file)) is { } task)
                    real.Add(new { Session = Path.GetFileName(Path.GetDirectoryName(file)), task.Output, task.Seconds, task.ModelSeconds, task.Requests,
                        task.Line, task.TtftLine, task.PureLine, task.CacheLine, task.ProviderLines, task.Started, task.At });
        }

        // The capsule as on a Claude window, with and without the worker rows, at two DPIs.
        try { if (System.Diagnostics.Process.GetProcessesByName("claude").Select(p => { try { return p.MainModule?.FileName; } catch { return null; } })
            .FirstOrDefault(f => f is not null) is { } claudeExe) AppIconCache.Remember(HostApp.Claude, claudeExe); } catch { }
        CodexWindowInfo Host(uint dpi) => new(new IntPtr(1), new IntRect(0, 0, 4000, 1200), new IntRect(0, 0, 4000, 1200), null,
            new IntRect(0, 0, 4000, 1200), dpi, new WindowChromeMetrics(36, 24, 4, 4, 4));
        TokenSnapshot Live(DeepSeekTurn? task) => new("x", "x", 0, 0, 0, 0, 0, 0, 0, default) { App = HostApp.Claude,
            Turn = new TurnStatistics("t", 4_700_800, 19_200, 0, 290, "완료", false, null) { ModelSeconds = 251.2, ModelRequests = 30, PureSeconds = 205.6 },
            CacheNote = "캐시 47분 남음", CacheBar = new CacheBar(0.78, CacheLevel.Fresh), ContextFill = 0.31, DeepSeek = task };
        // The worker run of 2026-10-20 20:02: 2,277 tokens, 16.2 s, requests 14.0 s, generating 10.4 s; cache numbers made up
        // to read 96.7% as on that day's sessions.
        var sample = new DeepSeekTurn(2277, 16.18, 14.0, 10.42, 6, "deepseek-flash", DateTimeOffset.UtcNow) { CacheRead = 41_280, CacheMiss = 1_410, Providers = ["deepseek-official"] };
        var twoRoutes = sample with { Providers = ["deepseek-official", "openrouter-key2"] };
        var fourRoutes = sample with { Providers = ["deepseek-official", "openrouter-key2", "siliconflow", "deepseek-key2"] };
        foreach (var (dpi, task, cache, provider, name) in new (uint, DeepSeekTurn?, bool, bool, string)[] {
            (96, null, true, true, "off"), (96, sample, true, true, "on"), (144, null, true, true, "off"), (144, sample, true, true, "on"),
            (144, sample, false, true, "no-cache"), (144, sample with { CacheRead = 0, CacheMiss = 0 }, true, true, "no-cache-numbers"),
            (144, sample, true, false, "no-provider"), (144, twoRoutes, true, true, "two-routes"),
            (144, fourRoutes, true, true, "four-routes"), (144, sample, false, false, "rates-only") })
        {
            var shown = TurnPresentation.ChooseLines(TurnPresentation.Create(Live(task)), cache, provider);
            var plain = AutoCapsule.Measure(TurnPresentation.ChooseLines(TurnPresentation.Create(Live(null)), cache, provider), dpi, 100);
            var measured = AutoCapsule.Measure(shown, dpi, 100);
            Check(measured.SubLines == 2, $"Claude always shows both rate lines ({name}, {dpi} DPI)");
            var workerRows = 2 + (cache && task?.CacheLine is not null ? 1 : 0);
            // Providers share those rows, one each, and add rows when there are more of them.
            if (provider && task is not null) workerRows = Math.Max(workerRows, task.Providers.Count);
            if (task is not null)
                Check(measured.ExtraHeight == (1 + workerRows) * measured.SubHeight + workerRows * measured.LineGap
                    && measured.Size.Height == plain.Size.Height + measured.ExtraHeight + measured.ExtraGap
                    && measured.Size.Width >= plain.Size.Width, $"worker rows add their lines of height and never narrow the capsule ({name}, {dpi} DPI)");
            Check(TurnPresentation.Create(Live(task) with { Turn = null }).WorkerLine is null, "no worker rows on a waiting capsule");
            using var form = new TokenStripForm();
            _ = form.Handle;
            form.SetPresentation(shown);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Light));
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(dpi), AnchorMode.InsideTopRight, false, 0, false, Presentation: shown)));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            using (var g = Graphics.FromImage(bitmap))
                for (var py = 0; py < bitmap.Height; py++)
                for (var px = 0; px < bitmap.Width; px++)
                    if (form.Region is { } cut && !cut.IsVisible(px, py, g)) bitmap.SetPixel(px, py, Color.Transparent);
            bitmap.Save(Path.Combine(directory, $"deepseek-{dpi}-{name}.png"));
        }
        // The DSH desktop capsule on this PC's real chats (numbers only), by the titles in DSH's own cache.
        var capsules = new List<object>();
        var realCache = Path.Combine(home, ".dsh", "storages", "session_projcache", "sessions");
        if (Directory.Exists(realCache))
        {
            var reader = new DeepSeekSessionSource();
            foreach (var file in new DirectoryInfo(realCache).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                if (DeepSeekSessionSource.TitleIn(File.ReadAllBytes(file.FullName)) is { Length: > 0 } title
                    && reader.Read(title) is var read)
                    capsules.Add(new { Session = Path.GetFileNameWithoutExtension(file.Name), read.Error, read.Snapshot?.Turn?.Total,
                        read.Snapshot?.Turn?.Cached, read.Snapshot?.Turn?.Output,
                        read.Snapshot?.Turn?.Seconds, read.Snapshot?.Turn?.ModelSeconds, read.Snapshot?.Turn?.PureSeconds, read.Snapshot?.Turn?.ModelRequests,
                        read.Snapshot?.Turn?.State, read.Snapshot?.Turn?.Model, read.Snapshot?.ContextUsedTokens, read.Snapshot?.ContextWindowTokens,
                        read.Snapshot?.ContextFill, read.Snapshot?.RingNote, Recent = read.Snapshot?.History?.Turns.Count });
        }
        return new { Passed = checks.Count, Checks = checks, Real = real, Capsules = capsules };
    }
}
