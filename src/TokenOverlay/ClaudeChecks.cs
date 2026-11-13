using System.Globalization;
using System.Text.Json;

namespace TokenOverlay;

internal static class ClaudeChecks
{
    // Live check: requires the Claude desktop window to be foreground when it runs.
    public static object Probe()
    {
        if (!CodexWindowLocator.TryGetForegroundCodexTarget(out var target))
            return new { Foreground = HostApp.None.ToString() };
        if (target.App != HostApp.Claude) return new { Foreground = target.App.ToString() };
        var source = new ClaudeDesktopSource();
        var result = Task.Run(() => source.Poll(target)).GetAwaiter().GetResult();
        return new { Foreground = target.App.ToString(), source.VisibleTitle, result.Route,
            result.Snapshot?.ThreadTitle, result.Snapshot?.LogPath, result.Snapshot?.Turn };
    }

    public static object Run(string scratch)
    {
        var results = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("FAIL: " + name);
            results.Add(name);
        }
        string At(double seconds) => DateTimeOffset.Parse("2026-10-05T00:00:00Z").AddSeconds(seconds).ToString("O");
        string User(string prompt, double at, string text = "hello", bool side = false, bool meta = false) =>
            JsonSerializer.Serialize(new { type = "user", isSidechain = side, isMeta = meta, promptId = prompt,
                timestamp = At(at), message = new { role = "user", content = new object[] { new { type = "text", text } } } });
        string Tool(string prompt, double at) =>
            JsonSerializer.Serialize(new { type = "user", isSidechain = false, promptId = prompt, timestamp = At(at),
                message = new { role = "user", content = new object[] { new { type = "tool_result", content = "x" } } } });
        string Assistant(string id, double at, string? stop, long input, long create, long read, long output,
            string? speed = "standard", bool side = false) =>
            JsonSerializer.Serialize(new { type = "assistant", isSidechain = side, timestamp = At(at),
                message = new { id, stop_reason = stop, usage = new { input_tokens = input,
                    cache_creation_input_tokens = create, cache_read_input_tokens = read, output_tokens = output, speed } } });
        string Duration(double at) => JsonSerializer.Serialize(new { type = "system", subtype = "turn_duration",
            durationMs = 1, timestamp = At(at) });

        // Split view: pane sessions come from header button labels, pane positions become a per-pane host rectangle.
        var known = new[] { new ClaudeSessionEntry("1", "토큰"), new ClaudeSessionEntry("2", "토큰 오버레이"), new ClaudeSessionEntry("3", "headphone") };
        Check(ClaudeSessionIndex.MatchLabelTitle(known, "토큰 오버레이, 세션 이름 바꾸기") == "토큰 오버레이"
            && ClaudeSessionIndex.MatchLabelTitle(known, "토큰, rename session") == "토큰", "split pane: longest known title at the start of the label");
        Check(ClaudeSessionIndex.MatchLabelTitle(known, "headphone-lottery") == "headphone"
            && ClaudeSessionIndex.MatchLabelTitle(known, "headphones") is null
            && ClaudeSessionIndex.MatchLabelTitle(known, "토큰") is null
            && ClaudeSessionIndex.MatchLabelTitle(known, "터미널") is null, "split pane: label must continue past the title at a word break");
        Check(ClaudePane.IsPaneClass("dframe-pane dframe-pane-extra min-w-0") && !ClaudePane.IsPaneClass("dframe-pane-extra")
            && !ClaudePane.IsPaneClass(null), "split pane: recognised by its own class only");
        var wide = new CodexWindowInfo(new IntPtr(1), new IntRect(-14, -14, 2076, 1132), new IntRect(0, 0, 2048, 1104),
            new IntRect(1900, 0, 140, 30), new IntRect(0, 0, 2048, 1104), 96, new WindowChromeMetrics(36, 24, 4, 4, 4));
        var leftPane = new ClaudePane(280, -1057, "a", null, false).Host(wide);
        var rightPane = new ClaudePane(991, 0, "b", null, true).Host(wide);
        Check(leftPane.ExtendedFrameBounds == new IntRect(280, 0, 711, 1104) && leftPane.WindowBounds.Right == 2062 - 1057
            && leftPane.CaptionButtonBounds!.Value.X == 843, "split pane: left pane host ends at that pane's right edge");
        Check(rightPane.ExtendedFrameBounds.Right == 2048 && rightPane.WindowBounds.Right == wide.WindowBounds.Right
            && rightPane.ExtendedFrameBounds.Left == 991, "split pane: rightmost pane keeps the window's right edge");

        var lowerPane = (new ClaudePane(991, 0, "c", null, false) { TopInset = 540 }).Host(wide);
        Check(lowerPane.ExtendedFrameBounds == new IntRect(991, 540, 1057, 564) && lowerPane.CaptionButtonBounds!.Value.Y == 540,
            "split pane: a pane below another starts at its own top");
        // Side panel, as measured: the pane holds the chat column (648..2352) and the panel (2353..3841), plus a title line.
        var sidePane = new IntRect(630, 81, 3211, 1971);
        Check(ClaudeDesktopSource.ChatColumn(sidePane, [new(648, 81, 1704, 1953), new(2353, 81, 1488, 1971), new(627, 79, 1977, 43)])
                == new IntRect(630, 81, 1723, 1971)
            && ClaudeDesktopSource.ChatColumn(sidePane, [new(648, 81, 3193, 1953), new(627, 79, 1977, 43)]) == sidePane
            && ClaudeDesktopSource.ChatColumn(sidePane, []) == sidePane,
            "side panel: the chat column ends where the panel begins; without a panel the pane is kept whole");
        Check(OverlayContext.InnerAnchor(AnchorMode.InsideBottomRight, new ClaudePane(630, -1488, null, null, true)) == AnchorMode.InsideTopRight
            && OverlayContext.InnerAnchor(AnchorMode.InsideBottomRight, new ClaudePane(280, -1057, "a", null, false)) == AnchorMode.InsideTopRight
            && OverlayContext.InnerAnchor(AnchorMode.InsideBottomRight, new ClaudePane(991, 0, "b", null, true)) == AnchorMode.InsideBottomRight
            && OverlayContext.InnerAnchor(AnchorMode.InsideBottomRight, null) == AnchorMode.InsideBottomRight
            && OverlayContext.InnerAnchor(AnchorMode.TitleBarTopRight, new ClaudePane(280, -1057, "a", null, false)) == AnchorMode.TitleBarTopRight,
            "bottom-right capsule moved inward (left pane, chat beside a side panel) goes to the top right; the rightmost pane keeps its corner");
        Check(OverlayContext.CodexAnchor(AnchorMode.InsideBottomRight, home: false, maximized: false) == AnchorMode.InsideTopRight
            && OverlayContext.CodexAnchor(AnchorMode.InsideBottomRight, home: false, maximized: true) == AnchorMode.InsideBottomRight
            && OverlayContext.CodexAnchor(AnchorMode.InsideBottomRight, home: true, maximized: true) == AnchorMode.InsideTopRight
            && OverlayContext.CodexAnchor(AnchorMode.TitleBarTopRight, home: false, maximized: false) == AnchorMode.TitleBarTopRight
            && OverlayContext.CodexAnchor(AnchorMode.Auto, home: true, maximized: false) == AnchorMode.Auto,
            "Codex: bottom right goes top right on the home screen and in a window that is not maximized; other choices kept");
        // The user's Codex window as measured on 2026-11-08: 1920x1500 at 250%, not maximized.
        var codexSmall = new CodexWindowInfo(new IntPtr(2), new IntRect(0, 0, 1920, 1500), new IntRect(0, 0, 1920, 1500),
            null, new IntRect(0, 0, 3840, 2100), 240, default);
        var underHeader = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(codexSmall, AnchorMode.InsideTopRight, false, 0, false,
            HeaderOffsetDip: OverlayLayoutCalculator.CodexHeaderOffsetDip));
        var plainTop = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(codexSmall, AnchorMode.InsideTopRight, false, 0, false));
        Check(underHeader.State == OverlayVisualState.Collapsed && underHeader.WindowBounds.Y == 230 && plainTop.WindowBounds.Y == 140
            && underHeader.WindowBounds.Right == plainTop.WindowBounds.Right,
            $"Codex top right starts under the header row, 92 dip down (230 px at 250%; was {underHeader.WindowBounds.Y}, default {plainTop.WindowBounds.Y})");

        Check(ModelNames.Short("claude-opus-5-5") == "Opus 5.5" && ModelNames.Short("claude-haiku-4-5-20251001") == "Haiku 4.5"
            && ModelNames.Short("gpt-6-astra") == "6 Astra" && ModelNames.Short("claude-fable-5-1[1m]+") == "Fable 5.1+"
            && ModelNames.Short(null) is null && ModelNames.Short("<synthetic>") == "<synthetic>", "model ids are shortened for the list");

        var r = new ClaudeTurnReader();
        Check(r.Snapshot() is null, "empty session has no turn");
        r.ProcessLine(User("a", 0));
        Check(r.Snapshot() is { TurnId: "a", Running: true, Seconds: null, State: "집계 대기" }, "first running turn shows no partial rate");
        r.ProcessLine(Assistant("m1", 2, "tool_use", 2, 100, 1000, 50));
        r.ProcessLine(Assistant("m1", 2, "tool_use", 2, 100, 1000, 50));
        r.ProcessLine(Tool("a", 3));
        r.ProcessLine(Assistant("side", 4, "end_turn", 9, 9, 9, 999, side: true));
        Check(r.Snapshot() is { Running: true, Seconds: null }, "tool call and sub-agent reply do not end the turn");
        r.ProcessLine(Assistant("m2", 10, "end_turn", 3, 0, 1100, 150));
        Check(r.Snapshot() is { TurnId: "a", Input: 2205, Cached: 2100, Output: 200, Total: 2405, Seconds: 10,
            TokensPerSecond: 20, State: "완료", Running: false, FastMode: "off" },
            "per-response usage counted once, cache included in input, whole-turn seconds");
        r.ProcessLine(User("a", 10.5, meta: true));
        Check(r.Snapshot() is { TurnId: "a", Running: false }, "meta record is not a prompt");
        r.ProcessLine(User("b", 20));
        Check(r.Snapshot() is { TurnId: "a", Running: true, TokensPerSecond: 20 }, "previous completed turn visible while running");
        r.ProcessLine(Assistant("m3", 21, "tool_use", 1, 0, 10, 5, "fast"));
        r.ProcessLine(User("b", 22, "[Request interrupted by user]"));
        Check(r.Snapshot() is { TurnId: "b", State: "중단", Output: 5, Seconds: 2, FastMode: "on" }, "interrupted turn distinguished, fast speed read from that turn");
        r.ProcessLine(User("c", 30));
        r.ProcessLine(Assistant("m4", 31, "tool_use", 1, 0, 10, 5, "fast"));
        r.ProcessLine(Tool("c", 32));
        r.ProcessLine(Assistant("m5", 34, null, 1, 0, 10, 15, "standard"));
        Check(r.Snapshot() is { TurnId: "b", Running: true }, "unknown stop reason keeps turn open");
        r.ProcessLine(Duration(34.1));
        Check(r.Snapshot() is { TurnId: "c", State: "완료", Output: 20, Seconds: 4, FastMode: "mixed" }, "turn_duration closes turn; mixed speed flagged");
        r.ProcessLine(User("d", 40));
        r.ProcessLine(Assistant("m6", 41, "end_turn", 1, 0, 10, 5, null));
        Check(r.Snapshot() is { TurnId: "d", FastMode: "unknown" }, "missing speed is unknown, not OFF");
        r.ProcessLine(User("e", 50));
        r.ProcessLine(Assistant("m7", 50, "end_turn", 1, 0, 10, 5));
        Check(r.Snapshot() is { TurnId: "e", TokensPerSecond: null, State: "기록 불완전" }, "zero duration cannot produce infinite rate");
        var peer = new ClaudeTurnReader();
        peer.ProcessLine(User("p1", 0));
        peer.ProcessLine(Assistant("pm1", 10, "end_turn", 1, 0, 0, 100));
        peer.ProcessLine(JsonSerializer.Serialize(new { type = "user", isMeta = true, promptId = "p2", origin = new { kind = "peer" },
            timestamp = At(100), message = new { role = "user", content = "from another session" } }));
        peer.ProcessLine(Assistant("pm2", 104, "end_turn", 1, 0, 0, 40));
        Check(peer.Snapshot() is { TurnId: "p2", Output: 40, Seconds: 4 }, "message from another session starts its own turn");
        var broken = new ClaudeTurnReader();
        broken.ProcessLine(User("z", 0));
        broken.ProcessLine("{ broken record");
        broken.ProcessLine(Assistant("m", 1, "end_turn", 1, 0, 0, 5));
        Check(broken.Snapshot()?.Input is null, "malformed record is flagged");
        broken.ProcessLine(User("y", 2));
        broken.ProcessLine(Assistant("m2", 3, "end_turn", 1, 0, 0, 7));
        Check(broken.Snapshot() is { Problem: null, Output: 7 }, "a damaged line spoils only its own turn; the next turn counts again");
        var lost = new ClaudeTurnReader();
        lost.ProcessLine(User("p", 0));
        lost.ProcessLine(Assistant("m", 1, "tool_use", 1, 0, 0, 5));
        lost.ProcessLine(User("q", 9));
        lost.ProcessLine(Assistant("n", 10, "end_turn", 1, 0, 0, 5));
        lost.ProcessLine(User("r", 20));
        Check(lost.Snapshot() is { TurnId: "q", Running: true, Output: 5 }, "turn without end record does not leak into the next");
        var compact = new ClaudeTurnReader();
        compact.ProcessLine(User("k1", 0));
        compact.ProcessLine(Assistant("k1m", 10, "end_turn", 1, 0, 0, 100));
        compact.ProcessLine(JsonSerializer.Serialize(new { type = "user", isCompactSummary = true, promptId = "ks", timestamp = At(20),
            message = new { role = "user", content = "summary" } }));
        compact.ProcessLine(User("kc", 21, "<command-name>/compact</command-name>"));
        compact.ProcessLine(User("ko", 21, "<local-command-stdout>Compacted</local-command-stdout>"));
        Check(compact.Snapshot() is { TurnId: "k1", Running: false, Output: 100 }, "after /compact the last real turn stays on display");
        compact.ProcessLine(User("k2", 30));
        Check(compact.Snapshot() is { TurnId: "k1", Running: true, Seconds: 10 } && compact.Recent(5).Count == 1,
            "turn after /compact shows the last real turn; the command adds no history row");
        compact.ProcessLine(Assistant("k2m", 31, "tool_use", 1, 0, 0, 5));
        compact.ProcessLine(JsonSerializer.Serialize(new { type = "user", isCompactSummary = true, promptId = "ks2", timestamp = At(32),
            message = new { role = "user", content = "summary" } }));
        compact.ProcessLine(Assistant("k2n", 34, "end_turn", 1, 0, 0, 15));
        Check(compact.Snapshot() is { TurnId: "k2", Output: 20, Seconds: 4 }, "automatic compaction inside a turn does not split it");
        // Seen 2026-10-21: a background task's notice sat unanswered for 8 hours, then the app wrote a 0-token stand-in reply.
        string Synthetic(string id, double at) => JsonSerializer.Serialize(new { type = "assistant", isSidechain = false, timestamp = At(at),
            message = new { id, model = "<synthetic>", stop_reason = "stop_sequence", content = new object[] { new { type = "text", text = "No response requested." } },
                usage = new { input_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = 0, output_tokens = 0 } } });
        var notice = new ClaudeTurnReader();
        notice.ProcessLine(User("n1", 0));
        notice.ProcessLine(Assistant("n1m", 10, "end_turn", 1, 0, 0, 100));
        notice.ProcessLine(User("n2", 100, "<task-notification> <task-id>x</task-id>"));
        Check(notice.Snapshot() is { TurnId: "n1", Running: false }, "an unanswered task notice is not shown running");
        notice.ProcessLine(Synthetic("n2s", 100 + 8 * 3600));
        Check(notice.Snapshot() is { TurnId: "n1", Running: false, Seconds: 10 } && notice.Recent(5).Count == 1
            && notice.Cache?.LastRequest == DateTimeOffset.Parse(At(10)), "a stand-in reply adds no turn, history row or cache refresh");
        notice.ProcessLine(User("n3", 100 + 8 * 3600, "<task-notification> <task-id>y</task-id>"));
        Check(notice.Snapshot() is { TurnId: "n1", Running: false }, "a second unanswered notice stays idle");
        notice.ProcessLine(User("n4", 40000, "<task-notification> <task-id>z</task-id>"));
        notice.ProcessLine(Assistant("n4m", 40002, "tool_use", 1, 0, 0, 5));
        Check(notice.Snapshot() is { TurnId: "n1", Running: true }, "a notice the model answers runs like any turn");
        notice.ProcessLine(Tool("n4", 40003));
        notice.ProcessLine(Assistant("n4n", 40005, "end_turn", 1, 0, 0, 15));
        Check(notice.Snapshot() is { TurnId: "n4", Output: 20, Seconds: 5, Running: false }, "an answered notice is timed from the notice");
        var standIn = new ClaudeTurnReader();
        standIn.ProcessLine(User("f1", 0));
        standIn.ProcessLine(Assistant("f1m", 3, "tool_use", 1, 0, 0, 30));
        standIn.ProcessLine(Tool("f1", 4));
        standIn.ProcessLine(Synthetic("f1s", 60));
        Check(standIn.Snapshot() is { TurnId: "f1", Running: false, Output: 30, Seconds: 3 }, "a stand-in reply after real work ends the turn at its last real reply");
        // Seen 2026-10-20: at a compaction Claude Code wrote 207 earlier records again, so an old prompt opened a 3-hour turn.
        static string Once(string line, string uuid) => "{\"uuid\":\"" + uuid + "\"," + line[1..];
        var replay = new ClaudeTurnReader();
        string[] replayed = [Once(User("r1", 0), "u1"), Once(Assistant("r1m", 10, "end_turn", 1, 0, 0, 100), "u2")];
        foreach (var line in replayed) replay.ProcessLine(line);
        replay.ProcessLine(Once(User("r2", 9000), "u3"));
        replay.ProcessLine(Once(Assistant("r2m", 9100, "tool_use", 1, 0, 0, 5), "u4"));
        foreach (var line in replayed) replay.ProcessLine(line);
        replay.ProcessLine(Once(JsonSerializer.Serialize(new { type = "user", isCompactSummary = true, promptId = "r2",
            timestamp = At(9200), message = new { role = "user", content = "summary" } }), "u5"));
        replay.ProcessLine(Once(Assistant("r2n", 9300, "end_turn", 1, 0, 0, 15), "u6"));
        Check(replay.Snapshot() is { TurnId: "r2", Output: 20, Seconds: 300 } && replay.Recent(5).Count == 2,
            "records written again later are read once: an old prompt does not reopen as the current turn");
        // The DeepSeek worker started from this session: the call's time is kept; reading the worker's files is no call.
        var caller = new ClaudeTurnReader();
        caller.ProcessLine(User("w1", 0));
        string ToolCall(string id, int second, string command) => JsonSerializer.Serialize(new { type = "assistant", timestamp = At(second),
            message = new { id, role = "assistant", model = "claude-opus-5-5", stop_reason = "tool_use",
                usage = new { input_tokens = 1, output_tokens = 5 },
                content = new object[] { new { type = "tool_use", id = id + "t", name = "PowerShell", input = new { command } } } } });
        caller.ProcessLine(ToolCall("w1a", 5, @"Get-Content 'C:\Users\x\Documents\Claude\dsh-worker\README.md'"));
        caller.ProcessLine(ToolCall("w1b", 8, @"pwsh -NoProfile -File 'C:\Users\x\Documents\Claude\dsh-worker\run-worker.ps1' -WorkDir 'C:\w' -TaskFile 't.txt' -Name 'r1'"));
        Check(caller.WorkerCalls is [var call] && call == DateTimeOffset.Parse(At(8)) && new ClaudeTurnReader().WorkerCalls.Count == 0,
            "a tool call that starts the DeepSeek worker is remembered with its time, for that session only");

        var path = Path.Combine(scratch, "claude-turn-fixture.jsonl");
        File.WriteAllText(path, User("disk", 0) + "\n");
        var disk = new ClaudeTurnReader();
        disk.Read(path);
        var reply = Assistant("m", 4, "end_turn", 10, 0, 90, 20);
        File.AppendAllText(path, reply[..(reply.Length / 2)]);
        Check(disk.Read(path)?.Input is null, "partial appended JSON retained until newline");
        File.AppendAllText(path, reply[(reply.Length / 2)..] + "\n");
        Check(disk.Read(path) is { Total: 120, TokensPerSecond: 5 }, "incremental completion parsed");
        Check(new ClaudeTurnReader().Read(path) == disk.Read(path), "restart reconstruction equals incremental result");
        File.WriteAllText(path, User("replaced", 0) + "\n");
        Check(disk.Read(path)?.TurnId == "replaced", "file truncation resets cursor");

        Check(ClaudeDesktopSource.SessionTitleFromDocument("작업 - Claude Code") == "작업", "Code tab document title yields session title");
        Check(ClaudeDesktopSource.SessionTitleFromDocument("일반 채팅 - Claude") is null, "regular chat is not treated as a Code session");
        Check(ClaudeDesktopSource.SessionTitleFromDocument(" - Claude Code") is null, "empty title rejected");
        ClaudeSessionEntry[] entries = [new("1", "same"), new("2", "same"), new("3", "only")];
        Check(ClaudeSessionIndex.MatchUniqueTitle(entries, "only") == "3", "unique title connects");
        Check(ClaudeSessionIndex.MatchUniqueTitle(entries, "same") is null, "duplicate title waits instead of guessing");
        Check(ClaudeSessionIndex.MatchUniqueTitle(entries, "none") is null, "unknown title waits");

        const string store = @"C:\Program Files\WindowsApps\";
        Check(CodexWindowLocator.ClassifyExecutablePath(store + @"Claude_2.0_x64__p\app\Claude.exe") == HostApp.Claude, "Claude desktop identified");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Roaming\Claude\claude-code\2.1\x\claude.exe") == HostApp.None, "Claude Code CLI is not the desktop app");
        Check(CodexWindowLocator.ClassifyExecutablePath(store + @"OpenAI.Codex_1_x64__p\app\ChatGPT.exe") == HostApp.Codex, "Codex still identified");
        Check(CodexWindowLocator.ClassifyExecutablePath(store + @"OpenAI.ChatGPT_1_x64__p\app\ChatGPT.exe") == HostApp.None, "ChatGPT app still excluded");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Windows\explorer.exe") == HostApp.None, "other app excluded");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Local\Programs\ai.opencode.desktop\OpenCode.exe") == HostApp.OpenCode,
            "OpenCode desktop identified");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Roaming\npm\node_modules\@opencode\cli\bin\opencode.exe") == HostApp.None,
            "OpenCode CLI is not the desktop app");
        // OpenCode's main frame as read on this PC (2026-11-12): Chromium class, WS_EX_APPWINDOW|WS_EX_WINDOWEDGE, not layered.
        var openCode = new WindowCandidateFacts(new IntPtr(7), 7, true, true, false, IntPtr.Zero,
            0x200100, new IntRect(122, 8, 1292, 806), "Chrome_WidgetWin_1") { App = HostApp.OpenCode };
        Check(CodexWindowClassifier.Select([openCode], new IntPtr(7)) is not null, "OpenCode main frame accepted as a host");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Local\Programs\DeepSeek Harness\DeepSeek Harness.exe") == HostApp.DeepSeekHarness,
            "DeepSeek Harness desktop identified");
        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\Downloads\DeepSeek Harness.exe") == HostApp.None,
            "DeepSeek Harness outside its install folder is not the desktop app");
        // DeepSeek Harness's main frame as read on this PC (2026-11-13): maximized, Chromium class, 0x200100, not layered.
        var deepSeek = openCode with { Handle = new IntPtr(8), ProcessId = 8, Bounds = new IntRect(-7, -7, 1295, 767), App = HostApp.DeepSeekHarness };
        Check(CodexWindowClassifier.Select([deepSeek], new IntPtr(8)) is not null, "DeepSeek Harness main frame accepted as a host");
        var layered = new WindowCandidateFacts(new IntPtr(1), 1, true, true, false, IntPtr.Zero,
            0x280100, new IntRect(0, 0, 1200, 900), "Chrome_WidgetWin_1");
        Check(CodexWindowClassifier.Select([layered with { App = HostApp.Claude }], new IntPtr(1)) is not null, "layered Claude main frame accepted");
        Check(CodexWindowClassifier.Select([layered], new IntPtr(1)) is null, "layered Codex window still rejected");
        var claudeMain = layered with { App = HostApp.Claude, Bounds = new IntRect(1161, 162, 536, 728), ExtendedStyle = 0x280108 };
        var screenLayer = new WindowCandidateFacts(new IntPtr(2), 1, true, true, false, IntPtr.Zero,
            0x8280028, new IntRect(0, 0, 1920, 1080), "Chrome_WidgetWin_1") { App = HostApp.Claude };
        Check(CodexWindowClassifier.Select([claudeMain, screenLayer], new IntPtr(1)) is not null, "Claude click-through screen layer does not displace the main window");
        Check(CodexWindowClassifier.Select([claudeMain, screenLayer], new IntPtr(2)) is null, "Claude click-through screen layer is not a host");
        var popOut = claudeMain with { Handle = new IntPtr(3), Bounds = new IntRect(552, 216, 916, 708) };
        var wideMain = claudeMain with { Bounds = new IntRect(-8, -8, 1936, 1048) };
        Check(CodexWindowClassifier.Select([wideMain, popOut], new IntPtr(3))?.Host.Handle == new IntPtr(3), "Claude pop-out session window is accepted when it is in front");
        Check(CodexWindowClassifier.Select([wideMain, popOut], new IntPtr(1))?.Host.Handle == new IntPtr(1), "Claude main window still selected when it is in front");
        var main0 = layered with { ExtendedStyle = 0 };
        Check(CodexWindowClassifier.Select([main0, main0 with { Handle = new IntPtr(4), Bounds = new IntRect(0, 0, 600, 500) }], new IntPtr(4)) is null, "Codex secondary window is still not a host");
        Check(ScreenCaptureTools.IsCaptureProcess("ScreenClippingHost") && ScreenCaptureTools.IsCaptureProcess("snippingtool")
            && !ScreenCaptureTools.IsCaptureProcess("explorer") && !ScreenCaptureTools.IsCaptureProcess("chrome"),
            "Windows snipping surfaces count as capture tools; ordinary apps do not");
        var claudeSnapshot = new TokenSnapshot("id", "", 0, 0, 0, 0, 0, 0, 0, default) { Turn = r.Snapshot(), App = HostApp.Claude };
        Check(TurnPresentation.Create(claudeSnapshot).Primary.Value != "—", "Claude turn renders through the shared presentation");
        var shown = TurnPresentation.Create(claudeSnapshot);
        Check(shown.App == HostApp.Claude && shown.Secondary.LabelAfterValue, "presentation carries the app and value-then-unit rate");
        Check(AutoCapsule.SplitLabel(shown.Secondary with { CompactLabel = "⚡* TPS" }) == ("⚡*", "TPS"), "Fast mark leads, unit trails the rate");
        Check(AutoCapsule.SplitLabel(shown.Primary) == (shown.Primary.CompactLabel, ""), "token label still leads its value");
        var plain = AutoCapsule.Measure(shown with { App = HostApp.None, Running = false }, 96, 100);
        var marked = AutoCapsule.Measure(shown with { Running = false }, 96, 100);
        var busy = AutoCapsule.Measure(shown with { Running = true }, 96, 100);
        Check(marked.Icon > 0 && marked.Size.Width == plain.Size.Width + marked.Icon + marked.Gap, "app mark widens the capsule by exactly its own space");
        Check(busy.Dot > 0 && busy.Size.Width > marked.Size.Width, "running dot gets its own space");

        // ----- Weekly limit share of this PC: rises in the app's weekly percent are credited to this PC
        // when this PC's session logs show responses in that stretch.
        const long H = 3600_000L;
        WeeklySample S(double hours, int percent) => new((long)(hours * H), percent);
        WeeklyActivity A(double hours, long tokens = 1000) => new((long)(hours * H), tokens);
        var weeks = WeeklyUsageMath.Windows(
            [S(0, 5), S(1, 8), S(2, 8), S(3, 14), S(4, 20), S(5, 2), S(6, 7)],
            [A(0.5), A(0.9), A(3.5, 2000), A(4.8), A(5.5)]);
        Check(weeks.Count == 2 && weeks[0] is { StartMs: 0, EndMs: 5 * H, Account: 20, ThisPc: 9, Tokens: 5000 },
            "weekly: rise during this PC's activity is this PC's (3+6), rise while idle is not, first reading is nobody's");
        Check(weeks[1] is { StartMs: 5 * H, EndMs: null, Account: 7, ThisPc: 7, Tokens: 1000 },
            "weekly: a falling percent starts a new week; what it already shows is this PC's if this PC was active");
        var lagged = WeeklyUsageMath.Windows([S(0, 0), S(1, 0), S(2, 4)], [A(0.98)]);
        Check(lagged[0].ThisPc == 4, "weekly: a rise reported within the lag after the last response still counts");
        var chat = WeeklyUsageMath.Windows([S(0, 0), S(1, 0), S(2, 4), S(3, 9)], [], presence: [(long)(1.5 * H)]);
        Check(chat[0] is { ThisPc: 4, Tokens: 0 }, "weekly: the Claude window in front (a chat, no Code log) credits the rise to this PC without tokens");
        var late = WeeklyUsageMath.Windows([S(0, 0), S(1, 0), S(2, 4)], [A(0.8)]);
        Check(late[0].ThisPc == 0 && late[0].Tokens == 1000, "weekly: a rise long after the last response is someone else's");
        Check(WeeklyUsageMath.Windows([], [A(1)]).Count == 0 && WeeklyUsageMath.Windows([S(0, 3)], []) is [{ Account: 3, ThisPc: 0 }],
            "weekly: no readings means no week; one reading means nothing attributable yet");
        var history = JsonSerializer.Serialize(new { version = 2, samples = new object[] {
            new { t = 3 * H, org = "a", u = new { fh = 1, sd = 9.6 } },
            new { t = 1 * H, org = "a", u = new { fh = 0, sd = 2 } },
            new { t = 2 * H, org = "b", u = new { fh = 0, sd = 50 } },
            new { t = 4 * H, org = "a", u = new { fh = 0 } } } });
        var parsed = WeeklyUsageMath.ParseHistory(history);
        Check(parsed.Count == 2 && parsed[0] == new WeeklySample(1 * H, 2) && parsed[1] == new WeeklySample(3 * H, 10),
            "weekly: readings sorted by time, rounded, other accounts and readings without a weekly value dropped");
        Check(WeeklyUsageMath.ParseHistory("{}").Count == 0 && WeeklyUsageMath.ParseHistory("[]").Count == 0, "weekly: empty record is tolerated");
        var logged = WeeklyUsageMath.ParseLogLine(Assistant("w1", 60, "end_turn", 2, 100, 1000, 50));
        Check(logged is { Activity.Tokens: 1152 } && logged.Value.Activity.AtMs == DateTimeOffset.Parse("2026-10-05T00:01:00Z").ToUnixTimeMilliseconds(),
            "weekly: a logged response counts input, cache write, cache read and output at its own time");
        Check(WeeklyUsageMath.ParseLogLine(User("u", 1)) is null && WeeklyUsageMath.ParseLogLine("not json") is null
            && WeeklyUsageMath.ParseLogLine(Assistant("w2", 1, "end_turn", 1, 0, 0, 1)) is { Key: "w2|" }, "weekly: prompts and broken lines are skipped; responses keyed by id");
        var root = Path.Combine(scratch, "weekly-check-" + Guid.NewGuid().ToString("N")[..8]);
        var projects = Path.Combine(root, "projects", "p");
        Directory.CreateDirectory(projects);
        var now = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
        long T(double hoursAgo) => now.AddHours(-hoursAgo).ToUnixTimeMilliseconds();
        string Sample(double hoursAgo, int sd) => JsonSerializer.Serialize(new { t = T(hoursAgo), org = "o", u = new { fh = 0, sd } });
        var historyFile = Path.Combine(root, "plan-usage-history.json");
        File.WriteAllText(historyFile, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(30, 40), Sample(20, 46), Sample(10, 3), Sample(1, 9)) + "]}");
        // Session log: a response 25 h ago (last week, while 40→46) and one 2 h ago (this week, while 3→9).
        var log = Path.Combine(projects, "s.jsonl");
        File.WriteAllText(log, Assistant("x1", 10 * 3600 - 25 * 3600, "end_turn", 1, 0, 0, 100) + "\n" + Assistant("x2", 10 * 3600 - 2 * 3600, "end_turn", 1, 0, 0, 200) + "\n");
        var weeklyStore = Path.Combine(root, "weekly-usage.json");
        var monitor = new WeeklyUsageMonitor(() => [historyFile], Path.Combine(root, "projects"), weeklyStore);
        monitor.Update(now);
        Check(monitor.LastWindows.Count == 2 && monitor.LastWindows[0] is { Account: 46, ThisPc: 6, Tokens: 101 }
            && monitor.LastWindows[1] is { Account: 9, ThisPc: 6, Tokens: 201 } && monitor.Line == $"이번주 {WeeklySplit.Since(monitor.LastWindows[1].StartMs)}~ · 나 6% · 다른 곳 3% · 계정 9%",
            "weekly: real files → last week and this week (the 3% at the reset reading was spent while this PC was idle), line names the running one");
        Check(File.Exists(weeklyStore) && File.ReadAllText(weeklyStore).Contains("\"thisPcPercent\":6"), "weekly: the finished week is written to the overlay's own file");
        File.AppendAllText(log, Assistant("x3", 10 * 3600 - 1800, "end_turn", 1, 0, 0, 300) + "\n");
        File.WriteAllText(historyFile, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(30, 40), Sample(20, 46), Sample(10, 3), Sample(1, 9), Sample(0.1, 12)) + "]}");
        monitor.Update(now);
        Check(monitor.LastWindows[1] is { Account: 12, ThisPc: 9, Tokens: 502 }, "weekly: new log lines and readings extend the running week");
        File.Delete(log);
        var reopened = new WeeklyUsageMonitor(() => [historyFile], Path.Combine(root, "projects"), weeklyStore);
        reopened.Update(now);
        Check(reopened.LastWindows[0] is { ThisPc: 6, Tokens: 101 } && reopened.LastWindows[1].ThisPc == 0 && reopened.Line is not null,
            "weekly: after the logs are gone the finished week keeps its stored value, only the running week is recomputed");
        reopened.Update(now.AddDays(8));
        Check(reopened.Line is null, "weekly: readings older than a week show nothing");
        // Presence survives a restart through the store: a chat 4 h ago is credited again after reopening.
        File.WriteAllText(historyFile, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(30, 40), Sample(20, 46), Sample(10, 3), Sample(5, 3), Sample(1, 9)) + "]}");
        var chatting = new WeeklyUsageMonitor(() => [historyFile], Path.Combine(root, "projects"), weeklyStore);
        chatting.NotePresence(now.AddHours(-4));
        chatting.Update(now);
        Check(chatting.LastWindows[1] is { Account: 9, ThisPc: 6, Tokens: 0 }, "weekly: presence alone credits the running week");
        var afterRestart = new WeeklyUsageMonitor(() => [historyFile], Path.Combine(root, "projects"), weeklyStore);
        afterRestart.Update(now);
        Check(afterRestart.LastWindows[1].ThisPc == 6 && File.ReadAllText(weeklyStore).Contains("\"presence\""), "weekly: presence minutes are stored and reloaded");
        // The user's laptop wrote that it was busy 4 h ago (the 3→9 stretch): with the share that rise is "me".
        var shareDir = Path.Combine(root, "share");
        Directory.CreateDirectory(shareDir);
        var laptopMinute = now.AddHours(-4).ToUnixTimeMilliseconds() / 60_000;
        File.WriteAllText(Path.Combine(shareDir, "LAPTOP.claude.json"),
            JsonSerializer.Serialize(new { device = "LAPTOP", minutes = new[] { new[] { laptopMinute, laptopMinute + 2 } } }));
        var shared = new WeeklyUsageMonitor(() => [historyFile], Path.Combine(root, "projects"), Path.Combine(root, "shared-store.json"),
            share: () => new DriveShare(shareDir, "PC1"));
        shared.NotePresence(now.AddHours(-9));
        shared.Update(now);
        Check(shared.LastWindows[1] is { ThisPc: 6, ThisPcOnly: 0 } && shared.Line!.EndsWith("~ · PC1 0% · LAPTOP 6% · 다른 곳 3% · 계정 9%")
            && File.ReadAllText(Path.Combine(shareDir, "PC1.claude.json")).Contains("\"minutes\":[["),
            "weekly share: another device's busy minutes make a rise \"my other device\", not this PC's; this PC's own minutes are written for the others");
        new DriveShare(shareDir, "PC1").Rename("LAPTOP", "랩탑");
        shared.Update(now.AddSeconds(1));
        Check(shared.Line!.EndsWith("~ · PC1 0% · 랩탑 6% · 다른 곳 3% · 계정 9%"), "weekly share: a name given in the device manager shows in the line");
        // Both devices busy in the 3→9 stretch (group.json, one folder up, still names LAPTOP 랩탑): this PC made a response of weight 100 three hours ago, the laptop 200.
        var bothDir = Path.Combine(root, "share-both");
        var bothProjects = Path.Combine(root, "projects-both");
        Directory.CreateDirectory(bothDir);
        Directory.CreateDirectory(Path.Combine(bothProjects, "p"));
        File.WriteAllText(Path.Combine(bothProjects, "p", "s.jsonl"), Assistant("y1", 10 * 3600 - 3 * 3600, "end_turn", 100, 0, 0, 0) + "\n");
        File.WriteAllText(Path.Combine(bothDir, "LAPTOP.claude.json"),
            JsonSerializer.Serialize(new { device = "LAPTOP", minutes = new[] { new[] { laptopMinute, laptopMinute + 2 } } }));
        var both = new WeeklyUsageMonitor(() => [historyFile], bothProjects, Path.Combine(root, "both-store.json"),
            share: () => new DriveShare(bothDir, "PC1"));
        both.Update(now);
        Check(both.LastWindows[1] is { ThisPc: 6, ThisPcOnly: 6 } && both.Line!.EndsWith("~ · PC1 6% · 랩탑 0% · 다른 곳 3% · 계정 9%"),
            "weekly share: a device that writes no weights (an older overlay) leaves a shared rise with this PC as before");
        File.WriteAllText(Path.Combine(bothDir, "LAPTOP.claude.json"), JsonSerializer.Serialize(new { device = "LAPTOP",
            minutes = new[] { new[] { laptopMinute, laptopMinute + 2 } }, weights = new[] { new[] { laptopMinute, 200L } } }));
        both.Update(now.AddSeconds(2));
        Check(both.LastWindows[1] is { ThisPc: 6, ThisPcOnly: 2 } && both.Line!.EndsWith("~ · PC1 2% · 랩탑 4% · 다른 곳 3% · 계정 9%")
            && File.ReadAllText(Path.Combine(bothDir, "PC1.claude.json")).Contains("\"weights\":[["),
            "weekly share: with both devices busy the rise is shared by weight (100 : 200 → 2% : 4%), and this PC writes its own weights");
        // A third device busy in the same stretch (weight 300, a minute later) gets its own part by name, the last busy
        // first; one busy only before this week (a sold laptop) is left off the line.
        File.WriteAllText(Path.Combine(bothDir, "TABLET.claude.json"), JsonSerializer.Serialize(new { device = "TABLET",
            minutes = new[] { new[] { laptopMinute + 1, laptopMinute + 3 } }, weights = new[] { new[] { laptopMinute + 1, 300L } } }));
        var oldMinute = now.AddDays(-8).ToUnixTimeMilliseconds() / 60_000;
        File.WriteAllText(Path.Combine(bothDir, "OLD.claude.json"), JsonSerializer.Serialize(new { device = "OLD",
            minutes = new[] { new[] { oldMinute, oldMinute + 5 } } }));
        both.Update(now.AddSeconds(3));
        Check(both.LastWindows[1] is { ThisPc: 6, ThisPcOnly: 1 } && both.Line!.EndsWith("~ · PC1 1% · TABLET 3% · 랩탑 2% · 다른 곳 3% · 계정 9%")
            && WeeklySplit.ParseLine(both.Line) is { Parts.Count: 4 },
            "weekly share: each of my other devices gets its own part by weight (100 : 200 : 300 → 1% : 2% : 3%); one not busy this week is left off");
        Check(WeeklySplit.DeviceParts(6, 2, [("A", 1.5, true), ("B", 1.5, true), ("C", 0, false)]) is [("A", 2), ("B", 2)]
            && WeeklySplit.DeviceParts(5, 2, [("A", 0, true)]) is [("A", 3)]
            && WeeklySplit.DeviceParts(6, 1, [("A", 3, true), ("B", 3, true)]) is [("A", 2), ("B", 3)] or [("A", 3), ("B", 2)]
            && WeeklySplit.DeviceParts(9, 1, [.. Enumerable.Range(1, 5).Select(i => ($"D{i}", 1.0, true))]) is null
            && WeeklyUsageMath.Line(new WeeklyWindow(0, null, 9, 6, 0, ThisPcOnly: 1), new("PC", "내 다른 기기")).EndsWith("~ · PC 1% · 내 다른 기기 5% · 다른 곳 3% · 계정 9%"),
            "weekly share: device parts add up to what is not this PC's alone; with more than four devices they are summed as before");
        // A device that joined mid-week (a new laptop): its own record starts at 9%, but the PC it shares with read 3% → 9%
        // while the PC was busy, so that rise is "my other device", not "다른 곳".
        var joinDir = Path.Combine(root, "share-join");
        Directory.CreateDirectory(joinDir);
        var lateHistory = Path.Combine(root, "late-history.json");
        File.WriteAllText(lateHistory, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(1, 9), Sample(0.5, 9)) + "]}");
        File.WriteAllText(Path.Combine(joinDir, "PC0.claude.json"), JsonSerializer.Serialize(new { device = "PC0",
            minutes = new[] { new[] { laptopMinute, laptopMinute + 2 } },
            readings = new[] { new[] { T(10), 3L }, new[] { T(5), 3L }, new[] { T(1), 9L } } }));
        var lonely = new WeeklyUsageMonitor(() => [lateHistory], Path.Combine(root, "projects-none"), null);
        lonely.Update(now);
        var joining = new WeeklyUsageMonitor(() => [lateHistory], Path.Combine(root, "projects-none"), null, share: () => new DriveShare(joinDir, "NEW"));
        joining.Update(now);
        Check(lonely.Line!.EndsWith("~ · 나 0% · 다른 곳 9% · 계정 9%") && joining.Line!.EndsWith("~ · NEW 0% · PC0 6% · 다른 곳 3% · 계정 9%")
            && File.ReadAllText(Path.Combine(joinDir, "NEW.claude.json")).Contains("\"readings\":[["),
            "weekly share: a device that joins mid-week takes the other devices' readings, so what they spent before is theirs; it writes its own readings too");
        // Someone else on the account sharing the folder: their device has them as owner, and a rise while only they
        // were busy is theirs ("@친구1"), not "다른 곳". This PC was in front 4.5 h ago (3→5), 친구1 busy 2 h ago (5→9).
        Check(WeeklyUsageMath.Windows([S(0, 0), S(1, 3), S(2, 3), S(3, 8), S(4, 10)], [A(0.5)], people: [new([(long)(2.5 * H)], [])])
                is [{ Account: 10, ThisPc: 3, People: [5] }]
            && WeeklyUsageMath.Windows([S(0, 0), S(1, 6)], [new((long)(0.5 * H), 100, 100)], people: [new([(long)(0.6 * H)], [((long)(0.6 * H), 200)])])
                is [{ ThisPc: 2, People: [4] }]
            && WeeklyUsageMath.Windows([S(0, 0), S(1, 6)], [], presence: [(long)(0.5 * H)], people: [new([(long)(0.6 * H)], [((long)(0.6 * H), 200)])])
                is [{ ThisPc: 6, People: [0] }]
            && WeeklyUsageMath.Windows([S(0, 0), S(1, 6)], [], people: [new([(long)(0.5 * H)], []), new([(long)(0.6 * H)], [])])
                is [{ ThisPc: 0, People: [3, 3] }],
            "weekly people: a rise while only someone else was busy is theirs; both weighted → by weight; this PC busy without a weight keeps it; two people without weights share evenly");
        var personLine = WeeklyUsageMath.Line(new WeeklyWindow(0, null, 38, 10, 0, People: [20]), null, ["친구1"]);
        Check(personLine.EndsWith("~ · 나 10% · @친구1 20% · 다른 곳 8% · 계정 38%")
            && WeeklySplit.ParseLine(personLine) is { Account: 38, People: 1, Parts: [("나", 10), ("친구1", 20), ("다른 곳", 8)] }
            && WeeklySplit.ParseLine(WeeklyUsageMath.Line(new WeeklyWindow(0, null, 38, 31, 0, ThisPcOnly: 22, People: [5, 1]), new("PC", "@랩탑"), ["친구1", "친구2"]))
                is { People: 2, Parts: [("PC", 22), ("랩탑", 9), ("친구1", 5), ("친구2", 1), ("다른 곳", 1)] },
            "weekly people: the line marks other people for the bar; a device name cannot pass for one");
        var friendDir = Path.Combine(root, "share-friend");
        Directory.CreateDirectory(friendDir);
        var friendHistory = Path.Combine(root, "friend-history.json");
        File.WriteAllText(friendHistory, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(10, 3), Sample(5, 3), Sample(3, 5), Sample(1, 9)) + "]}");
        var friendMinute = now.AddHours(-2).ToUnixTimeMilliseconds() / 60_000;
        File.WriteAllText(Path.Combine(friendDir, "FRIEND.claude.json"), JsonSerializer.Serialize(new { device = "FRIEND",
            minutes = new[] { new[] { friendMinute, friendMinute + 1 } } }));
        new DriveShare(friendDir, "PC1").SetOwner("FRIEND", "친구1");
        var withFriend = new WeeklyUsageMonitor(() => [friendHistory], Path.Combine(root, "projects-none"), null, share: () => new DriveShare(friendDir, "PC1"));
        withFriend.NotePresence(now.AddHours(-4.5));
        withFriend.Update(now);
        Check(withFriend.Line!.EndsWith("~ · 나 2% · @친구1 4% · 다른 곳 3% · 계정 9%"),
            "weekly people: another person's device read through the share gets its own part");
        Check(withFriend.ShareNote.StartsWith("wrote minutes ") && withFriend.ShareNote.Contains("| @친구1 ") && withFriend.ShareNote.Contains(" readings "),
            "weekly people: the bug report names what each shared device gave");
        var withFooter = TurnPresentation.Create(claudeSnapshot with { History = new TurnHistory([r.Snapshot()!]), HistoryFooter = monitor.Line });
        Check(withFooter.HistoryPanelRows == 4 && TurnPresentation.Create(claudeSnapshot with { History = new TurnHistory([r.Snapshot()!]) }).HistoryPanelRows == 2,
            "weekly: the footer takes two more panel rows (its line and its bar) only when present");
        Check(TokenStripForm.HistoryFooterWidth(withFooter, 96, 100) > 0 && TokenStripForm.HistoryFooterWidth(withFooter with { HistoryFooter = null }, 96, 100) == 0,
            "weekly: the footer asks for width only when present");
        // Chat not known yet: Ctrl still opens a list holding only the weekly line, as wide as that line.
        var waiting = OverlayPresentationBuilder.CreateWaiting("현재 채팅 확인 대기", DisplayField.Total, DisplayField.ContextPercent, DisplayField.Total)
            with { HistoryFooter = monitor.Line };
        var waitingPanel = OverlayLayoutCalculator.Calculate(new(new CodexWindowInfo(new IntPtr(1), new IntRect(0, 0, 1600, 1200), new IntRect(0, 0, 1600, 1200), null,
            new IntRect(0, 0, 1600, 1200), 96, new WindowChromeMetrics(36, 24, 4, 4, 4)), AnchorMode.InsideTopRight,
            true, waiting.HistoryPanelRows, false, Presentation: waiting, CompactPanel: true));
        Check(waiting.HasHistoryPanel && waiting.HistoryPanelRows == 2 && !(waiting with { HistoryFooter = null }).HasHistoryPanel
            && waitingPanel.State == OverlayVisualState.Expanded
            && waitingPanel.PanelBounds.Width ==TokenStripForm.HistoryFooterWidth(waiting, 96, 100),
            "waiting: the list opens with the weekly line alone (no column header), as wide as that line; nothing to open without it");
        // ----- One-minute readings through the usage API (token read-only; none of this touches the network).
        var fixture = Path.Combine(scratch, "claude-usage-api-fixture.json");
        Check(!File.Exists(fixture) || ClaudeUsageApi.ParseUsage(File.ReadAllText(fixture)) == 22, "usage api: the recorded real response yields the weekly percent");
        var resetAt = DateTimeOffset.Parse("2026-10-17T10:59:59.566209+00:00", CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
        Check(!File.Exists(fixture) || ClaudeUsageApi.ParseReset(File.ReadAllText(fixture)) == resetAt, "usage api: the recorded real response yields the weekly reset time");
        Check(ClaudeUsageApi.ParseReset("{\"seven_day\":{\"utilization\":1,\"resets_at\":\"2026-10-17T10:59:59.566209+00:00\"}}") == resetAt
            && ClaudeUsageApi.ParseReset("{\"limits\":[{\"kind\":\"session\",\"resets_at\":\"2026-10-10T16:19:59+00:00\"},{\"kind\":\"weekly_all\",\"resets_at\":\"2026-10-17T10:59:59.566209+00:00\"}]}") == resetAt
            && ClaudeUsageApi.ParseReset("{\"seven_day\":{\"utilization\":1}}") is null && ClaudeUsageApi.ParseReset("{\"seven_day\":{\"resets_at\":5}}") is null
            && ClaudeUsageApi.ParseReset("nope") is null, "usage api: weekly reset time from either shape, nothing when it is missing or malformed");
        var seoul = TimeZoneInfo.CreateCustomTimeZone("check+9", TimeSpan.FromHours(9), "check+9", "check+9");
        DateTimeOffset Clock(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        Check(WeeklySplit.Since(Clock("2026-10-09T11:00:00+00:00").ToUnixTimeMilliseconds(), seoul) == "9(금)"
            && WeeklySplit.Since(Clock("2026-10-04T15:30:00+00:00").ToUnixTimeMilliseconds(), seoul) == "5(월)",
            "week line: the start is the local day of the month and weekday, no month");
        Check(WeeklyUsageMath.ResetLine(resetAt, Clock("2026-10-12T12:00:00+00:00"), seoul) == "초기화 10-17 (토) 20:00 · 4일 23시간 남음",
            "reset line: local day and time rounded to the minute, with the time left");
        Check(WeeklyUsageMath.ResetLine(resetAt, Clock("2026-10-17T05:30:00+00:00"), seoul) == "초기화 10-17 (토) 20:00 · 5시간 30분 남음"
            && WeeklyUsageMath.ResetLine(resetAt, Clock("2026-10-17T10:50:00+00:00"), seoul) == "초기화 10-17 (토) 20:00 · 10분 남음", "reset line: hours and minutes on the last day");
        Check(WeeklyUsageMath.ResetLine(resetAt, Clock("2026-10-17T12:00:00+00:00"), seoul) == "초기화 10-24 (토) 20:00 (추정) · 6일 23시간 남음"
            && WeeklyUsageMath.ResetLine(resetAt, Clock("2026-11-01T00:00:00+00:00"), seoul).StartsWith("초기화 11-07 (토) 20:00 (추정)"),
            "reset line: a stored time that has passed moves on by whole weeks and is marked as an estimate");
        var blank = new OverlayMetric(default, "", "", "", false);
        var noFooter = new OverlayPresentation(blank, blank, [], 0, false, null) { History = [new HistoryRow("1턴 전", "1k", "1초", "1", "1")] };
        Check((noFooter with { HistoryFooter = "a\nb" }).HistoryPanelRows == 4 && (noFooter with { HistoryFooter = "a" }).HistoryPanelRows == 3
            && noFooter.HistoryPanelRows == 2, "list panel gives each footer line its own row");
        // The weekly line is drawn as a bar on a second row; the line read back gives the shares the bar is made of.
        var weekLine = WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(), null, 38, 31, 0));
        var sharedLine = WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(), null, 38, 31, 0, ThisPcOnly: 22));
        Check(WeeklySplit.ParseLine(weekLine) is { Account: 38, Parts: [("나", 31), ("다른 곳", 7)] } week && week.Since.EndsWith('~')
            && WeeklySplit.ParseLine(sharedLine) is { Account: 38, Parts: [("나1", 22), ("나2", 9), ("다른 곳", 7)] }
            && WeeklySplit.ParseLine("초기화 10-24 (토) 20:00 · 3일 22시간 남음") is null && WeeklySplit.ParseLine("캐시 유지 · 58분 남음") is null,
            "the weekly line reads back into its shares (one device, two devices); other footer lines are left as text");
        var namedLine = WeeklyUsageMath.Line(new WeeklyWindow(DateTimeOffset.Parse("2026-10-17T11:00:00Z").ToUnixTimeMilliseconds(), null, 38, 31, 0, ThisPcOnly: 22),
            new WeeklySplit.Names("PC", "랩탑 · 2"));
        Check(namedLine.EndsWith("~ · PC 22% · 랩탑 - 2 9% · 다른 곳 7% · 계정 38%")
            && WeeklySplit.ParseLine(namedLine) is { Account: 38, Parts: [("PC", 22), ("랩탑 - 2", 9), ("다른 곳", 7)] }
            && WeeklySplit.LineName("아주아주아주아주아주긴기기이름입니다") == "아주아주아주아주아주긴기기이름…",
            "the weekly line names this PC and the other device as the device manager does, and still reads back");
        var weekly = noFooter with { History = [new HistoryRow("1턴 전", "1k", "1초", "1", "1")], HistoryFooter = "캐시 유지 · 58분 남음\n" + weekLine };
        Check(weekly.HistoryPanelRows == 1 + 1 + 2 + 1 && TokenStripForm.HistoryFooterWidth(weekly, 96, 100) > 0,
            "the weekly bar takes a row of its own under its line");
        Check(ClaudeUsageApi.ParseUsage("{\"seven_day\":{\"utilization\":21.6}}") == 22
            && ClaudeUsageApi.ParseUsage("{\"limits\":[{\"kind\":\"session\",\"percent\":80},{\"kind\":\"weekly_all\",\"percent\":7}]}") == 7
            && ClaudeUsageApi.ParseUsage("{}") is null && ClaudeUsageApi.ParseUsage("nope") is null, "usage api: weekly percent from either shape, nothing from an unknown one");
        string Credentials(long expiresAt) => JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "test-token", refreshToken = "never-used", expiresAt } });
        Check(ClaudeUsageApi.ReadToken(Credentials(now.AddHours(1).ToUnixTimeMilliseconds()), now) is ("test-token", null), "usage api: a valid access token is read");
        Check(ClaudeUsageApi.ReadToken(Credentials(now.AddMinutes(-1).ToUnixTimeMilliseconds()), now) is (null, { } expired) && expired.Contains("만료")
            && ClaudeUsageApi.ReadToken("{}", now) is (null, ClaudeUsageApi.NoLogin) && ClaudeUsageApi.ReadToken("broken", now).Token is null,
            "usage api: an expired or missing token is not used");
        var mergedSamples = WeeklyUsageMath.Merge([S(0, 5), S(2, 9)], [new((long)(1 * H), 7), new((long)(1 * H) + 60_000, 6), new((long)(2 * H), 9), new((long)(3 * H), 1)]);
        Check(mergedSamples.Select(m => m.Percent).SequenceEqual([5, 7, 9, 1]), "usage api: both sources on one time line; a small close dip is noise, a real drop is a reset");
        var run = new List<WeeklySample>();
        foreach (var value in new[] { 3, 3, 3, 3, 4, 4, 4 }) WeeklyUsageMath.AddCompressed(run, new WeeklySample(run.Count * 100 + value, value));
        Check(run.Select(m => m.Percent).SequenceEqual([3, 3, 4, 4]), "usage api: only the first and last reading of an unchanged run are kept");
        // The app's record is 50 minutes old at 9 %. Readings: 10 % while this PC is idle, then 12 % right after a response here.
        var apiRoot = Path.Combine(root, "api"); Directory.CreateDirectory(Path.Combine(apiRoot, "projects", "p"));
        var apiHistory = Path.Combine(apiRoot, "plan-usage-history.json");
        File.WriteAllText(apiHistory, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(3, 9), Sample(0.84, 9)) + "]}");
        var apiLog = Path.Combine(apiRoot, "projects", "p", "s.jsonl");
        File.WriteAllText(apiLog, "");
        var apiStore = Path.Combine(apiRoot, "weekly-usage.json");
        var liveReset = now.AddDays(3).ToUnixTimeMilliseconds();
        var answers = new Queue<UsageApiResult>([new(10, "정상"), new(12, "정상", false, liveReset), new(null, "사용량 조회 거절 (HTTP 401)", true)]);
        var calls = 0;
        var live = new WeeklyUsageMonitor(() => [apiHistory], Path.Combine(apiRoot, "projects"), apiStore, _ => { calls++; return answers.Dequeue(); });
        live.Update(now.AddMinutes(-30));
        Check(calls == 1 && live.LastWindows[^1] is { Account: 10, ThisPc: 0 } && live.ApiStatus == "정상", "usage api: a rise while this PC is idle goes elsewhere");
        Check(live.ResetLine is null && live.Footer == live.Line, "reset line: absent until the service has reported a reset time");
        live.Update(now.AddMinutes(-29));
        Check(calls == 1, "usage api: an idle PC is not asked again a minute later");
        File.AppendAllText(apiLog, Assistant("y1", 10 * 3600 - 60, "end_turn", 1, 0, 0, 500) + "\n");
        live.Update(now);
        Check(calls == 2 && live.LastWindows[^1] is { Account: 12, ThisPc: 2, Tokens: 501 }, "usage api: activity resumes, it is asked at once, and only the rise since then is this PC's");
        live.Update(now.AddMinutes(2));
        Check(calls == 3 && live.ApiStatus.Contains("401") && live.LastWindows[^1].Account == 12, "usage api: a refusal keeps the last known value");
        Check(live.ResetLine is { } resetShown && resetShown.StartsWith("초기화 ") && resetShown.EndsWith("2일 23시간 남음") && live.Footer == live.Line + "\n" + resetShown,
            "reset line: the reported time becomes the second footer line and stays through a refusal");
        live.Update(now.AddMinutes(4));
        Check(calls == 3, "usage api: after a refusal it backs off");
        live.SetApiEnabled(false);
        live.Update(now.AddMinutes(30));
        Check(calls == 3 && live.ApiStatus.StartsWith("꺼짐"), "usage api: switched off means no request at all");
        live.Dispose();
        var liveAgain = new WeeklyUsageMonitor(() => [apiHistory], Path.Combine(apiRoot, "projects"), apiStore, _ => { calls++; return new(12, "정상"); });
        liveAgain.Update(now.AddMinutes(31));
        Check(calls == 3 && !liveAgain.ApiEnabled && liveAgain.ApiSamples.Count == 2 && liveAgain.LastWindows[^1].ThisPc == 2,
            "usage api: readings and the off switch survive a restart");
        Check(liveAgain.ResetLine is { } kept && kept.StartsWith("초기화 ") && !kept.Contains("추정"), "reset line: the stored time is used after a restart with the API off");
        // The login is gone (11-05: Claude Code emptied it when its refresh token ran out): the last reading, 50 minutes old, stands still.
        var goneRoot = Path.Combine(root, "gone"); Directory.CreateDirectory(Path.Combine(goneRoot, "projects", "p"));
        var goneHistory = Path.Combine(goneRoot, "plan-usage-history.json");
        File.WriteAllText(goneHistory, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(3, 9), Sample(0.84, 9)) + "]}");
        var goneAnswer = new UsageApiResult(null, ClaudeUsageApi.NoLogin);
        var gone = new WeeklyUsageMonitor(() => [goneHistory], Path.Combine(goneRoot, "projects"), null, _ => goneAnswer);
        gone.Update(now);
        var lastRead = WeeklyUsageMath.ParseHistory(File.ReadAllText(goneHistory))[^1].AtMs;
        Check(gone.StaleLine == WeeklyUsageMath.StaleLine(lastRead, ClaudeUsageApi.NoLogin) && gone.StaleLine!.EndsWith(" 이후 갱신 안 됨 · 눌러서 다시 로그인")
                && gone.Footer == gone.Line + "\n" + gone.StaleLine && WeeklySplit.IsStale(gone.StaleLine) && WeeklySplit.IsLogin(gone.StaleLine)
                && !WeeklySplit.IsStale(gone.Line!) && !WeeklySplit.IsLogin(gone.Line!),
            "stale week: with no login and no reading for 50 minutes, a line under the week says since when and to log in again");
        Check(ClaudeTokenRenewal.ParseLoginEnd("{\"claudeAiOauth\":{\"accessToken\":\"\",\"refreshToken\":\"\",\"refreshTokenExpiresAt\":5}}") is null
                && ClaudeTokenRenewal.ParseLoginEnd("{\"claudeAiOauth\":{\"refreshToken\":\"r\",\"refreshTokenExpiresAt\":5}}") == 5
                && ClaudeTokenRenewal.ParseLoginEnd("{\"claudeAiOauth\":{\"refreshToken\":\"r\"}}") == 0 && ClaudeTokenRenewal.ParseLoginEnd("{") is null,
            "login end: an emptied login (11-05) counts as none, so the tray menu offers to log in again");
        Check(WeeklyUsageMath.LoginLine(now.AddDays(2).AddHours(5).AddMinutes(1).ToUnixTimeMilliseconds(), now) == "로그인 2일 5시간 후 만료 · 눌러서 다시 로그인"
                && WeeklyUsageMath.LoginLine(now.AddMinutes(90).ToUnixTimeMilliseconds(), now) == "로그인 1시간 30분 후 만료 · 눌러서 다시 로그인"
                && WeeklyUsageMath.LoginLine(now.AddMinutes(-1).ToUnixTimeMilliseconds(), now) == "로그인 만료됨 · 눌러서 다시 로그인",
            "login line: time left in days and hours, hours and minutes, or run out");
        var endsAt = now.AddDays(4).ToUnixTimeMilliseconds();
        var warned = new WeeklyUsageMonitor(() => [goneHistory], Path.Combine(goneRoot, "projects"), null, _ => new(9, "정상"), loginEnd: () => endsAt);
        warned.Update(now);
        var fourDaysLeft = warned.StaleLine;
        warned.Update(now.AddDays(1.5));
        Check(fourDaysLeft is null && warned.StaleLine is { } warning && warning.StartsWith("로그인 2일 ") && WeeklySplit.IsLogin(warning) && !WeeklySplit.IsStale(warning)
                && warned.Footer == warned.Line + "\n" + warning,
            "login line: shown under the week in the login's last three days, not before; the bar is not greyed for it");
        Check(WeeklyUsageMath.StaleLine(0, "사용량 조회 거절 (HTTP 401)", TimeZoneInfo.Utc) == "01-01 00:00 이후 갱신 안 됨 · 조회 실패",
            "stale week: any other failure reads 조회 실패");
        goneAnswer = new UsageApiResult(11, "정상");
        gone.Update(now.AddMinutes(16));
        Check(gone.StaleLine is null && gone.LastWindows[^1].Account == 11 && gone.Footer == gone.Line, "stale week: the line goes once a reading comes in");
        var offline = new WeeklyUsageMonitor(() => [goneHistory], Path.Combine(goneRoot, "projects"), null, _ => new(null, "사용량 조회 실패 · 네트워크"));
        File.WriteAllText(goneHistory, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(3, 9), Sample(0.1, 9)) + "]}");
        offline.Update(now);
        Check(offline.StaleLine is null, "stale week: a failure with a reading 6 minutes old says nothing yet");
        Check(ClaudeTokenRenewal.LoginArguments(@"C:\A B\claude.exe").Contains("\"C:\\A B\\claude.exe\" auth login --claudeai")
            && ClaudeTokenRenewal.LoginArguments("x").StartsWith("/c \""), "login: the sign-in window runs Claude Code's own browser sign-in, path quoted");
        using (var small = TrayIcon.Load(new Size(16, 16)))
        using (var large = TrayIcon.Load(new Size(32, 32)))
            Check(small is { Width: 16 } && large is { Width: 32 }, "tray icon: the embedded icon loads at the small and the large size");
        Check(OverlayContext.TrayText("0.6.9+42eaab05df44d8e33db82b459e8fd487bfaf4554", AppUpdater.TrayLineFor(true, null, false, false, false, new DateTime(2026, 10, 20, 22, 30, 0)))
                == "TokenOverlay 0.6.9\n최신 버전 · 22:30 확인"
            && OverlayContext.TrayText("0.6.9", AppUpdater.TrayLineFor(true, "0.7.0", false, false, false, null)) == "TokenOverlay 0.6.9\n새 버전 0.7.0 받음 · 메뉴 맨 위를 눌러 업데이트",
            "tray tooltip: the version without the build's commit, then the update state");
        Check(AppUpdater.TrayLineFor(true, "0.7.0", true, false, false, null) == "새 버전 0.7.0 나옴 · 메뉴 맨 위를 눌러 설치"
            && AppUpdater.TrayLineFor(true, null, false, true, false, null) == "업데이트 확인 중…"
            && AppUpdater.TrayLineFor(true, null, false, false, true, DateTime.Now) == "업데이트 확인 실패 · 나중에 다시 시도"
            && AppUpdater.TrayLineFor(true, null, false, false, false, null) == "업데이트 확인 전"
            && AppUpdater.TrayLineFor(false, null, false, false, false, null) == "설치본이 아님 · 업데이트 확인 안 함"
            && OverlayContext.TrayText(new string('9', 300), "x").Length <= 127,
            "tray tooltip: blocked updater, checking, failed, not yet checked, not installed; stays within the length Windows shows");
        Check(OverlayContext.VersionMenuLine("0.6.9+42eaab0", true, null, false, false) == ("버전 0.6.9 · 최신 버전 (눌러서 확인)", true, false)
            && OverlayContext.VersionMenuLine("0.6.9", true, "0.7.0", false, false) == ("새 버전 0.7.0 · 눌러서 업데이트", true, true)
            && OverlayContext.VersionMenuLine("0.6.9", true, "0.7.0", true, false) == ("새 버전 0.7.0 · 눌러서 설치 파일로 업데이트", true, true)
            && OverlayContext.VersionMenuLine("0.6.9", true, null, false, true) == ("버전 0.6.9 · 업데이트 확인 중…", false, false)
            && OverlayContext.VersionMenuLine("0.6.9", false, null, false, false) is (_, false, false),
            "menu top line: the version; a waiting update in bold that a click puts in; otherwise a click checks now");
        Check(OverlayContext.TelemetryMenuText(TelemetryLinkState.On, TelemetryLinkState.AppMissing) == "모델 요청 속도 계측 · Claude 켜짐 · Codex 앱 없음"
            && OverlayContext.TelemetryMenuText(TelemetryLinkState.Conflict, null) == "모델 요청 속도 계측 · Claude 다른 설정 있음 · Codex 확인 못 함",
            "telemetry menu item states each app's connection in words");
        // ----- Prompt cache: session read share, rebuilt turns, and whether the cache is still warm (by the last request).
        string Cached(string id, double at, long input, long create, long read, long oneHour, long fiveMin) =>
            JsonSerializer.Serialize(new { type = "assistant", timestamp = At(at), message = new { id, stop_reason = "end_turn",
                usage = new { input_tokens = input, cache_creation_input_tokens = create, cache_read_input_tokens = read, output_tokens = 10,
                    cache_creation = new { ephemeral_1h_input_tokens = oneHour, ephemeral_5m_input_tokens = fiveMin } } } });
        var cacheReader = new ClaudeTurnReader();
        cacheReader.ProcessLine(User("c1", 0));
        cacheReader.ProcessLine(Cached("r1", 2, 10, 100_000, 0, 100_000, 0));
        cacheReader.ProcessLine(Cached("r1", 2, 10, 100_000, 0, 100_000, 0));
        cacheReader.ProcessLine(User("c2", 60));
        cacheReader.ProcessLine(Cached("r2", 62, 10, 1_000, 100_000, 1_000, 0));
        var cacheState = cacheReader.Cache!;
        var lastAt = DateTimeOffset.Parse("2026-10-05T00:01:02Z");
        Check(cacheState is { SessionInput: 201_020, SessionCached: 100_000, TtlMinutes: 60, LastContext: 101_010 } && cacheState.LastRequest == lastAt,
            "cache: session sums count each response once; lifetime from the 1-hour write; last request and its context");
        var cacheTurns = cacheReader.Recent(5).Select(r => r.Turn).ToArray();
        Check(cacheTurns[0] is { Restored: false } && cacheTurns[1] is { Restored: true }, "cache: a turn that wrote most of its context is marked rebuilt");
        var cacheRows = TurnPresentation.HistoryRows(new TurnHistory(cacheTurns));
        Check(cacheRows[0].Cache == "99%" && cacheRows[1].Cache == "↻ 0%", "cache: list column shows each turn's read share, rebuilt turns marked");
        Check(CacheStatus.Line(cacheState, lastAt.AddMinutes(13)) == "캐시 유지 · 47분 남음 · 세션 캐시 읽기 50%"
            && CacheStatus.Line(cacheState, lastAt.AddMinutes(52.5)) == "캐시 곧 만료 · 8분 남음 · 세션 캐시 읽기 50%"
            && CacheStatus.Line(cacheState, lastAt.AddMinutes(61)) == "캐시 만료(추정) · 다시 보내면 101.0k 전체 재저장 · 세션 캐시 읽기 50%",
            "cache: footer line counts down, warns near the end, and estimates the rebuild once lapsed");
        Check(CacheStatus.CapsuleNote(cacheState, lastAt.AddMinutes(13)) == "캐시 47분 남음" && !CacheStatus.CapsuleAlert(cacheState, lastAt.AddMinutes(13))
            && CacheStatus.CapsuleNote(cacheState, lastAt.AddMinutes(52.5)) == "캐시 8분 남음" && CacheStatus.CapsuleAlert(cacheState, lastAt.AddMinutes(52.5))
            && CacheStatus.CapsuleNote(cacheState, lastAt.AddMinutes(61)) == "캐시 만료 101.0k" && CacheStatus.CapsuleAlert(cacheState, lastAt.AddMinutes(61))
            && CacheStatus.CapsuleNote(new CacheState(1000, 960), lastAt) is null, "cache: capsule shows minutes left, alert near the end and once lapsed");
        Check(CacheStatus.CapsuleBar(cacheState, lastAt.AddMinutes(13)) is { Level: CacheLevel.Fresh } fresh && Math.Abs(fresh.Fill - 47.0 / 60) < 1e-9
            && CacheStatus.CapsuleBar(cacheState, lastAt.AddMinutes(30)) is { Level: CacheLevel.Middle }
            && CacheStatus.CapsuleBar(cacheState, lastAt.AddMinutes(44.5)) is { Level: CacheLevel.Middle }
            && CacheStatus.CapsuleBar(cacheState, lastAt.AddMinutes(45)) is { Level: CacheLevel.Soon }
            && CacheStatus.CapsuleBar(cacheState, lastAt.AddMinutes(61)) is null && CacheStatus.CapsuleBar(new CacheState(1000, 960), lastAt) is null,
            "cache: capsule bar fills with the time left; green above 30 minutes, amber above 15, red after, none once lapsed");
        // Lapse notice: only an uncompacted session with a large context, 10 minutes or less before its cache lapses, once.
        Check(CacheWatch.Due(cacheState with { LastContext = 200_000 }, lastAt.AddMinutes(51)) is double soon && Math.Abs(soon - 9) < 1e-9
            && CacheWatch.Due(cacheState with { LastContext = 200_000 }, lastAt.AddMinutes(49)) is null
            && CacheWatch.Due(cacheState with { LastContext = 200_000 }, lastAt.AddMinutes(61)) is null
            && CacheWatch.Due(cacheState, lastAt.AddMinutes(51)) is null
            && CacheWatch.Due(cacheState with { LastContext = 200_000, Compacted = true }, lastAt.AddMinutes(51)) is null
            && CacheWatch.Due(cacheState with { LastContext = 200_000, TtlMinutes = 5 }, lastAt.AddMinutes(1)) is null,
            "cache watch: due 10 minutes before the lapse, not for a small, compacted or 5-minute cache, not once lapsed");
        var watchRoot = Directory.CreateTempSubdirectory("cachewatch-");
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(watchRoot.FullName, "C--work-big"));
            var wallNow = DateTimeOffset.UtcNow;
            string Line(string id, double minutesAgo, long read, string? subtype = null) => subtype is not null
                ? JsonSerializer.Serialize(new { type = "system", subtype, uuid = id, timestamp = wallNow.AddMinutes(-minutesAgo).ToString("o") })
                : JsonSerializer.Serialize(new { type = "assistant", uuid = id, cwd = @"C:\work\big-project", timestamp = wallNow.AddMinutes(-minutesAgo).ToString("o"),
                    message = new { id, stop_reason = "end_turn", usage = new { input_tokens = 10, cache_creation_input_tokens = 1_000, cache_read_input_tokens = read,
                        output_tokens = 5, cache_creation = new { ephemeral_1h_input_tokens = 1_000, ephemeral_5m_input_tokens = 0 } } } });
            string Prompt(string id, double minutesAgo) => JsonSerializer.Serialize(new { type = "user", uuid = id, promptId = id,
                timestamp = wallNow.AddMinutes(-minutesAgo).ToString("o"), message = new { role = "user", content = "x" } });
            var big = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(project.FullName, big + ".jsonl"), [Prompt("u1", 53), Line("a1", 52, 300_000)]);
            File.WriteAllLines(Path.Combine(project.FullName, Guid.NewGuid() + ".jsonl"), [Prompt("u2", 53), Line("a2", 52, 300_000), Line("b2", 51, 0, "compact_boundary")]);
            File.WriteAllLines(Path.Combine(project.FullName, Guid.NewGuid() + ".jsonl"), [Prompt("u3", 53), Line("a3", 52, 50_000)]);
            File.WriteAllLines(Path.Combine(project.FullName, Guid.NewGuid() + ".jsonl"), [Prompt("u4", 23), Line("a4", 22, 300_000)]);
            var watch = new CacheWatch(start: false);
            var first = watch.Check(wallNow, watchRoot.FullName, id => id == big ? "토큰 오버레이 캐시 알림 만들기 아주 긴 세션 제목" : null);
            var secondScan = watch.Check(wallNow.AddMinutes(1), watchRoot.FullName, _ => null);
            var untitled = new CacheWatch(start: false).Check(wallNow, watchRoot.FullName, _ => null);
            File.AppendAllLines(Path.Combine(project.FullName, big + ".jsonl"), [Prompt("u5", 0.5), Line("a5", 0.4, 301_000)]);
            var afterNew = watch.Check(wallNow.AddMinutes(51), watchRoot.FullName, _ => null);
            var lapseNotice = CacheWatch.Notice(first);
            Check(first is [{ Name: "토큰 오버레이 캐시 알림 만들기 아주 긴 …", Context: 301_010 } one] && Math.Abs(one.MinutesLeft - 8) < 0.1
                && secondScan.Count == 0 && afterNew is [{ Name: "big-project", Context: 302_010 }] && untitled is [{ Name: "big-project" }]
                && lapseNotice.Text == "토큰 오버레이 캐시 알림 만들기 아주 긴 … · 8분 뒤 301.0k 다시 저장\n자리를 비울 거면 그 세션에서 /compact",
                "cache watch: across all session logs, only the large uncompacted one near its lapse, told once until its next request; app title or folder name");

            // With the partner mod: its ping keeps an old log's cache alive, its compaction ends the count, its ping requests.
            var bridge = Directory.CreateDirectory(Path.Combine(watchRoot.FullName, "bridge")).FullName;
            var other = Directory.CreateDirectory(Path.Combine(watchRoot.FullName, "C--work-mod"));
            var pinged = Guid.NewGuid().ToString();
            var pingedLog = Path.Combine(other.FullName, pinged + ".jsonl");
            File.WriteAllLines(pingedLog, [Prompt("m1", 80), Line("n1", 79, 300_000)]);
            File.SetLastWriteTimeUtc(pingedLog, wallNow.UtcDateTime.AddMinutes(-79));
            long Ms(double minutesAgo) => wallNow.AddMinutes(-minutesAgo).ToUnixTimeMilliseconds();
            File.WriteAllText(Path.Combine(bridge, pinged + ".json"), JsonSerializer.Serialize(new
            {
                v = 1, sessionId = pinged, aliveAt = Ms(0.2), busy = false, lastAt = Ms(30), context = 301_010, compacted = false, handled = "r1",
                ping = new { request = "r1", at = Ms(30), ok = true, read = 300_000, write = 0 }
            }));
            var folded = Guid.NewGuid().ToString();
            File.WriteAllLines(Path.Combine(other.FullName, folded + ".jsonl"), [Prompt("m2", 53), Line("n2", 52, 300_000)]);
            File.WriteAllText(Path.Combine(bridge, folded + ".json"), JsonSerializer.Serialize(new
            {
                v = 1, sessionId = folded, aliveAt = Ms(5), lastAt = Ms(52), context = 0, compacted = true, handled = "", compactedAt = Ms(1)
            }));
            var modWatch = new CacheWatch(start: false);
            var early = modWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var listed = modWatch.Sessions;
            var lateScan = modWatch.Check(wallNow.AddMinutes(21), watchRoot.FullName, _ => null, bridge);
            var read = CacheWatch.ReadBridge(Path.Combine(bridge, pinged + ".json"));
            Check(listed.Any(x => x.Id == pinged && x.Partner && Math.Abs((x.LapsesAt - wallNow).TotalMinutes - 30) < 0.1 && x.Bridge is { PingOk: true, PingRead: 300_000 })
                && listed.Any(x => x.Id == folded && x.Compaction == CacheWatch.Compaction.None) && listed.First(x => x.Id == big) is { Partner: false, Bridge: null }
                && !early.Any(x => x.Id == pinged) && lateScan.Any(x => x.Id == pinged && !x.Partner)
                && read is { Handled: "r1", PingOk: true } && CacheWatch.ReadBridge(Path.Combine(bridge, "none.json")) is null
                && CacheWatch.Notice([new("a", 8, 300_000, pinged, true)]).Text.EndsWith("\n눌러서 핑할 세션 고르기 · 그냥 두면 만료 5분 전 자동 압축"),
                "cache watch: the mod's ping keeps an old log alive, a compaction it only reports does not end the count, and the notice says a click pings");
            var request = CacheWatch.RequestPing(pinged, bridge);
            var refused = false;
            try { CacheWatch.RequestPing(@"..\x", bridge); } catch (ArgumentException) { refused = true; }
            Check(File.ReadAllText(Path.Combine(bridge, pinged + ".ping")) == request && request.Length == 32 && refused,
                "cache watch: a ping request is a fresh id in <session>.ping, only for a session id");
            var quiet = Path.Combine(watchRoot.FullName, "quiet-bridge");
            Directory.CreateDirectory(quiet);
            var noneYet = CacheWatch.PartnerRunning(DateTimeOffset.Now, quiet) || CacheWatch.PartnerRunning(DateTimeOffset.Now, Path.Combine(quiet, "missing"));
            File.WriteAllText(Path.Combine(quiet, "old.json"), "{}");
            File.SetLastWriteTimeUtc(Path.Combine(quiet, "old.json"), DateTime.UtcNow.AddMinutes(-5));
            var staleOnly = CacheWatch.PartnerRunning(DateTimeOffset.Now, quiet);
            File.WriteAllText(Path.Combine(quiet, "new.json"), "{}");
            Check(!noneYet && !staleOnly && CacheWatch.PartnerRunning(DateTimeOffset.Now, quiet),
                "cache watch: the mod counts as running on this PC only while some session's report is fresh (else the Ctrl list keeps the turns)");
            // The Claude Code mod switch: read from the Claude settings, done through Claude Code's own commands.
            var modSettings = Path.Combine(watchRoot.FullName, "mod-settings.json");
            var ours = Path.Combine(watchRoot.FullName, "install", "claude-mod");
            string Registered(string path, bool enabled) => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["extraKnownMarketplaces"] = new { tokenoverlay = new { source = new { source = "directory", path } } },
                ["enabledPlugins"] = new Dictionary<string, bool> { [ClaudeModSetup.Plugin] = enabled }
            });
            var missingFile = ClaudeModSetup.Read(modSettings, ours);
            File.WriteAllText(modSettings, Registered(ours + "\\", true));
            var mine = ClaudeModSetup.Read(modSettings, ours);
            File.WriteAllText(modSettings, Registered(@"D:\dev\claude-mod", true));
            var elsewhere = ClaudeModSetup.Read(modSettings, ours);
            File.WriteAllText(modSettings, Registered(ours, false));
            var disabled = ClaudeModSetup.Read(modSettings, ours);
            File.WriteAllText(modSettings, "{ not json");
            var unreadable = ClaudeModSetup.Read(modSettings, ours);
            Check(missingFile == ClaudeModSetup.State.Off && mine == ClaudeModSetup.State.On && elsewhere == ClaudeModSetup.State.OtherFolder
                && disabled == ClaudeModSetup.State.Off && unreadable == ClaudeModSetup.State.Off,
                "claude mod: on only when this overlay's folder is registered and the mod enabled; another folder is told apart");
            Check(ClaudeModSetup.Steps(true, ours).Select(step => string.Join(' ', step)).SequenceEqual(
                    [$"plugin marketplace add {ours}", "plugin install tokenoverlay-cache@tokenoverlay"])
                && ClaudeModSetup.Steps(false, ours).Select(step => string.Join(' ', step)).SequenceEqual(
                    ["plugin uninstall tokenoverlay-cache@tokenoverlay", "plugin marketplace remove tokenoverlay"])
                && ClaudeModSetup.Set(true, "unused.exe", Path.Combine(watchRoot.FullName, "no-mod")) == "확장 파일이 오버레이 폴더에 없음",
                "claude mod: switched on by adding the folder as a marketplace and installing from it, off in the reverse order");
            Check(File.Exists(Path.Combine(ClaudeModSetup.BundledFolder, ".claude-plugin", "marketplace.json"))
                && File.Exists(Path.Combine(ClaudeModSetup.BundledFolder, "tokenoverlay-cache", "hooks", "register.ts"))
                && File.Exists(Path.Combine(ClaudeModSetup.BundledFolder, "tokenoverlay-cache", ".claude-plugin", "plugin.json")),
                "claude mod: the build carries the mod beside the program");
            Check(File.ReadAllText(Path.Combine(ClaudeModSetup.BundledFolder, "tokenoverlay-cache", "hooks", "register.ts"))
                    .Contains($"code: {CacheWatch.ModCode},"),
                "claude mod: the build the overlay compares sessions against is the one it carries");

            var pingForm = new CachePings(bridge);
            CacheWatch.Session Row(CacheWatch.Bridge? b, bool partner) => new(pinged, "x", wallNow.AddMinutes(30), 301_010, b, partner);
            var alive = new CacheWatch.Bridge(wallNow, null, "", null, false, null, 0, 0, null, Code: CacheWatch.ModCode);
            var older = alive with { Code = 2 };
            var compactAt = wallNow.AddMinutes(25).ToLocalTime().ToString("HH:mm");
            var pingAt = wallNow.AddMinutes(-3);
            Check(pingForm.Line(Row(null, false), wallNow) == new CachePings.RowText("30분 남음 · 301K", false, "확장 없음", false)
                && pingForm.Line(Row(alive, false), wallNow).State == "확장이 멈춰 있음"
                && pingForm.Line(Row(alive, true), wallNow) == new CachePings.RowText("30분 남음 · 301K", false, compactAt + " 자동 압축 예정", true, CanKeep: true, CanCompact: true)
                && pingForm.Line(Row(alive with { PingAt = pingAt, PingOk = true }, true), wallNow).State == pingAt.ToLocalTime().ToString("HH:mm") + " 핑함"
                && pingForm.Line(Row(alive with { PingAt = pingAt, PingReason = "busy" }, true), wallNow).State.EndsWith("핑 못 함 · 작업 중이었음")
                && pingForm.Line(Row(alive, true) with { LapsesAt = wallNow.AddMinutes(4.2) }, wallNow) is { Left: "5분 남음 · 301K", Soon: true },
                "cache window: each row says what the mod will do or did, and only a session running the mod can be pinged");
            Check(pingForm.Line(Row(older, true), wallNow) is { State: CachePings.UpdateText, CanPing: true }
                // A build past the last one worth reloading for is not told to reload (build 9 only names its process).
                && pingForm.Line(Row(alive with { Code = CacheWatch.ReloadHintCode }, true), wallNow).State != CachePings.UpdateText
                && pingForm.Line(Row(alive with { Code = CacheWatch.ReloadHintCode - 1 }, true), wallNow).State == CachePings.UpdateText
                && pingForm.Line(Row(older with { PingAt = pingAt, PingOk = true }, true), wallNow).State == pingAt.ToLocalTime().ToString("HH:mm") + " 핑함",
                "cache window: a session on an older mod build is told to type /reload-plugins, unless a ping is to be told of");
            // 유지: the overlay writes until when (8 hours on), the mod of build 3 and up pings 10 minutes before each lapse.
            var keepFile = Path.Combine(bridge, pinged + ".keep");
            var current = alive;
            var notYet = pingForm.Line(Row(current, true), wallNow);
            pingForm.Keep(pinged, true, wallNow);
            var until = CacheWatch.KeepUntil(pinged, wallNow, bridge);
            var untilText = until?.ToLocalTime().ToString("HH:mm");
            var nextPing = wallNow.AddMinutes(20).ToLocalTime().ToString("HH:mm");
            var keptLine = pingForm.Line(Row(current, true), wallNow);
            var oldMod = pingForm.Line(Row(older with { Code = 0 }, true), wallNow);
            var autoPinged = pingForm.Line(Row(current with { PingAt = pingAt, PingOk = true, PingAuto = true }, true), wallNow);
            var keepAfter = CacheWatch.KeepUntil(pinged, wallNow.AddHours(8.1), bridge);
            pingForm.Keep(pinged, false, wallNow);
            Check(notYet is { Keep: null, CanKeep: true } && pingForm.Line(Row(older with { Code = 0 }, true), wallNow) is { CanKeep: false }
                && until is { } u && Math.Abs((u - wallNow).TotalHours - 8) < 0.01 && keepAfter is null
                && keptLine.State == $"{nextPing} 자동 핑 예정 · 유지 ~{untilText}" && keptLine is { CanKeep: true, Keep: not null }
                && oldMod.State == CachePings.UpdateText && oldMod.CanKeep
                && autoPinged.State == $"{pingAt.ToLocalTime():HH:mm} 자동 핑함 · 다음 {nextPing} · 유지 ~{untilText}"
                && !File.Exists(keepFile) && CacheWatch.KeepUntil(pinged, wallNow, bridge) is null,
                "cache window: 유지 lasts 8 hours, says when the next automatic ping goes, and a session on an old mod is told to reload it");
            File.WriteAllText(Path.Combine(bridge, "code3.json"), JsonSerializer.Serialize(new
            {
                v = 1, code = 3, aliveAt = Ms(0.2), handled = "", ping = new { request = "keep-1", at = Ms(10), ok = true, auto = true }
            }));
            Check(CacheWatch.ReadBridge(Path.Combine(bridge, "code3.json")) is { Code: 3, PingAuto: true, PingOk: true }
                && CacheWatch.ReadBridge(Path.Combine(bridge, pinged + ".json")) is { Code: 0, PingAuto: false },
                "cache watch: the mod's build and whether its last ping was its own are read from its report");
            // 압축: a first press arms the row for 5 seconds, a second within them writes the request; the row then follows
            // what the mod says, until the compaction ends the row or 10 minutes pass.
            var compacts = new CachePings(bridge);
            var cOther = Guid.NewGuid().ToString();
            var cFirst = compacts.Compact(pinged, wallNow);
            var cArmedLine = compacts.Line(Row(alive, true), wallNow.AddSeconds(2));
            var cLapsedArm = compacts.Line(Row(alive, true), wallNow.AddSeconds(6));
            var cRearmed = compacts.Compact(pinged, wallNow.AddSeconds(10));
            compacts.Compact(cOther, wallNow.AddSeconds(11));
            var cOtherArmed = compacts.Line(Row(alive, true), wallNow.AddSeconds(11.5));
            var cCrossed = compacts.Compact(pinged, wallNow.AddSeconds(12));
            var cAskedAt = wallNow.AddSeconds(13);
            var cAsked = compacts.Compact(pinged, cAskedAt);
            var cCompactFile = Path.Combine(bridge, pinged + ".compact");
            var cRequest = File.Exists(cCompactFile) ? File.ReadAllText(cCompactFile) : "";
            var cWaiting = compacts.Line(Row(alive, true), cAskedAt.AddSeconds(10));
            var cWhileBusy = compacts.Line(Row(alive with { Busy = true }, true), cAskedAt.AddMinutes(3));
            var cSilent = compacts.Line(Row(alive, true), cAskedAt.AddMinutes(3));
            var cRunning = compacts.Line(Row(alive with { CompactHandled = cRequest, CompactAt = cAskedAt.AddSeconds(20), CompactOk = true }, true), cAskedAt.AddSeconds(30));
            var cFailed = compacts.Line(Row(alive with { CompactHandled = cRequest, CompactAt = cAskedAt.AddSeconds(20) }, true), cAskedAt.AddSeconds(30));
            var cForgotten = compacts.Line(Row(alive, true), cAskedAt.AddMinutes(11));
            Check(!cFirst && cArmedLine is { State: CachePings.ArmText, Arming: true, CanCompact: true }
                && cLapsedArm is { Arming: false, State: not CachePings.ArmText } && !cRearmed && cOtherArmed is { Arming: false } && !cCrossed
                && cAsked && cRequest.Length == 32
                && cWaiting is { State: "압축 요청함…", CanCompact: false, Arming: false } && cWhileBusy is { State: "답이 끝나면 압축", CanCompact: false }
                && cSilent is { State: "확장이 답하지 않음", CanCompact: true }
                && cRunning is { State: "압축 중…", CanCompact: false } && cFailed is { State: "압축 못 함", CanCompact: true }
                && cForgotten is { CanCompact: true } && cForgotten.State == compactAt + " 자동 압축 예정"
                && compacts.Line(Row(alive with { Code = 3 }, true), wallNow) is { CanCompact: false, State: CachePings.UpdateText }
                && compacts.Line(Row(alive, false), wallNow) is { CanCompact: false },
                "cache window: 압축 asks on the second press within 5 seconds, then follows the mod: waiting, after the reply, running, failed");
            File.WriteAllText(Path.Combine(bridge, "code4.json"), JsonSerializer.Serialize(new
            {
                v = 1, code = 4, aliveAt = Ms(0.2), busy = true, handled = "", compactHandled = "c1", compact = new { request = "c1", at = Ms(1), ok = true }
            }));
            Check(CacheWatch.ReadBridge(Path.Combine(bridge, "code4.json")) is { Code: 4, Busy: true, CompactHandled: "c1", CompactOk: true, CompactAt: not null }
                && CacheWatch.ReadBridge(Path.Combine(bridge, "code3.json")) is { Busy: false, CompactHandled: "", CompactAt: null, CompactOk: false },
                "cache watch: whether the session was answering and the 압축 it took up are read from the mod's report");

            // A compaction the mod tried is the log's to confirm: checked for 3 minutes, then retried once the other way,
            // then told as failed, past the lapse too, until the session's next request.
            File.WriteAllText(Path.Combine(bridge, "code5.json"), JsonSerializer.Serialize(new
            {
                v = 1, code = 5, aliveAt = Ms(0.2), handled = "",
                attempt = new { at = Ms(19), by = "auto", done = Ms(18.9), via = "api", ok = true }
            }));
            var readAttempt = CacheWatch.ReadBridge(Path.Combine(bridge, "code5.json"))?.Attempt;
            Check(readAttempt is { By: "auto", Via: "api", Ok: true, Reason: null, Done: not null }
                && Math.Abs((readAttempt.At - wallNow).TotalMinutes + 19) < 0.01
                && CacheWatch.ReadBridge(Path.Combine(bridge, "code4.json")) is { Attempt: null },
                "cache watch: the mod's last compaction attempt is read from its report");
            var hourAgo = new CacheState(1, 1, wallNow.AddMinutes(-60), 60, 301_010);
            CacheWatch.Bridge Tried(double minutesAgo, string by, double? doneAgo, bool ok, string? reason = null) =>
                alive with { Attempt = new(wallNow.AddMinutes(-minutesAgo), by, doneAgo is { } d ? wallNow.AddMinutes(-d) : null, "api", ok, reason) };
            Check(CacheWatch.Outcome(hourAgo, alive, wallNow) == CacheWatch.Compaction.None
                && CacheWatch.Outcome(hourAgo, Tried(2, "auto", null, false), wallNow) == CacheWatch.Compaction.Running
                && CacheWatch.Outcome(hourAgo, Tried(11, "auto", null, false), wallNow) == CacheWatch.Compaction.Failed
                && CacheWatch.Outcome(hourAgo, Tried(1, "auto", 0.5, true), wallNow) == CacheWatch.Compaction.Checking
                && CacheWatch.Outcome(hourAgo, Tried(4, "auto", 3.9, true), wallNow) == CacheWatch.Compaction.Failed
                && CacheWatch.Outcome(hourAgo, Tried(0.5, "asked", 0.4, false, "failed"), wallNow) == CacheWatch.Compaction.Failed
                && CacheWatch.Outcome(hourAgo, Tried(1, "retry", null, false), wallNow) == CacheWatch.Compaction.Retrying
                // build 8's turn-end compaction is told like the automatic one
                && CacheWatch.Outcome(hourAgo, Tried(1, "turnend", 0.5, true), wallNow) == CacheWatch.Compaction.Checking
                && CacheWatch.Outcome(hourAgo, Tried(4, "turnend", 3.9, true), wallNow) == CacheWatch.Compaction.Failed
                && CacheWatch.Outcome(hourAgo with { LastRequest = wallNow.AddMinutes(-0.2) }, Tried(1, "auto", 0.5, true), wallNow) == CacheWatch.Compaction.None
                && CacheWatch.Outcome(hourAgo with { Compacted = true }, Tried(4, "auto", 3.9, true), wallNow) == CacheWatch.Compaction.None
                && CacheWatch.Outcome(hourAgo, Tried(13 * 60, "auto", 13 * 60, true), wallNow) == CacheWatch.Compaction.None
                && CacheWatch.Effective(hourAgo, alive with { CompactedAt = wallNow }) is { Compacted: false },
                "cache watch: an attempt runs up to 10 minutes, is looked for in the log 3 minutes once answered, then failed; a later request or the log's compaction ends it");

            var lapsedId = Guid.NewGuid().ToString();
            var lapsedLog = Path.Combine(other.FullName, lapsedId + ".jsonl");
            File.WriteAllLines(lapsedLog, [Prompt("f1", 75), Line("g1", 74, 300_000)]);
            File.SetLastWriteTimeUtc(lapsedLog, wallNow.UtcDateTime.AddMinutes(-74));
            var lapsedBridge = Path.Combine(bridge, lapsedId + ".json");
            void Report(object attempt, bool busy = false) => File.WriteAllText(lapsedBridge, JsonSerializer.Serialize(new
            {
                v = 1, code = 5, sessionId = lapsedId, aliveAt = Ms(0.2), busy, lastAt = Ms(74), context = 301_010, compacted = true, handled = "", attempt
            }));
            var retryFile = Path.Combine(bridge, lapsedId + ".compact");
            Report(new { at = Ms(19), by = "auto", done = Ms(18.9), via = "api", ok = true }, busy: true);
            var retryWatch = new CacheWatch(start: false);
            retryWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var whileBusy = retryWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            var notWhileBusy = !File.Exists(retryFile);
            Report(new { at = Ms(19), by = "auto", done = Ms(18.9), via = "api", ok = true });
            retryWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var retrying = retryWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            var retryAsked = File.Exists(retryFile) ? File.ReadAllText(retryFile) : "";
            retryWatch.Check(wallNow.AddSeconds(30), watchRoot.FullName, _ => null, bridge);
            var stillAsked = retryWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            var askedOnce = File.ReadAllText(retryFile) == retryAsked;
            Report(new { at = Ms(0.5), by = "retry", via = "command" });
            retryWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var retryRunning = retryWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            Report(new { at = Ms(5), by = "retry", done = Ms(4.9), via = "command", ok = true });
            retryWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var failedForGood = retryWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            var noSecondRetry = File.ReadAllText(retryFile) == retryAsked;
            File.Delete(retryFile);
            Report(new { at = Ms(19), by = "auto", done = Ms(18.9), via = "api", ok = false, reason = "skipped" });
            var vetoWatch = new CacheWatch(start: false);
            vetoWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var vetoed = vetoWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            var vetoNotRetried = !File.Exists(retryFile);
            Report(new { at = Ms(19), by = "auto", done = Ms(18.9), via = "command", ok = false, reason = "stopped" });
            var stoppedWatch = new CacheWatch(start: false);
            stoppedWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var stoppedRow = stoppedWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            vetoNotRetried &= stoppedRow is { Compaction: CacheWatch.Compaction.Failed } && !File.Exists(retryFile);
            File.AppendAllLines(lapsedLog, [Prompt("f2", 0.5), Line("g2", 0.4, 301_000)]);
            vetoWatch.Check(wallNow, watchRoot.FullName, _ => null, bridge);
            var afterRequest = vetoWatch.Sessions.FirstOrDefault(x => x.Id == lapsedId);
            Check(whileBusy is { Compaction: CacheWatch.Compaction.Failed } && notWhileBusy
                && retrying is { Compaction: CacheWatch.Compaction.Retrying, Partner: true } && retrying.LapsesAt < wallNow && retrying.Shown(wallNow)
                && retryAsked.StartsWith("retry-") && retryAsked.Length == 38
                && stillAsked is { Compaction: CacheWatch.Compaction.Retrying } && askedOnce
                && retryRunning is { Compaction: CacheWatch.Compaction.Retrying }
                && failedForGood is { Compaction: CacheWatch.Compaction.Failed } && noSecondRetry
                && vetoed is { Compaction: CacheWatch.Compaction.Failed } && vetoNotRetried
                && afterRequest is { Compaction: CacheWatch.Compaction.None } && afterRequest.LapsesAt > wallNow,
                "cache watch: an unconfirmed compaction is retried once (not while answering, not after a veto or a stopped session's refusal), then stays listed as failed past the lapse until the next request");

            var failedAt = wallNow.AddMinutes(-19);
            var failedRow = new CacheWatch.Session(pinged, "x", wallNow.AddMinutes(-14), 301_010, Tried(19, "auto", 18.9, true), true, CacheWatch.Compaction.Failed);
            var failedText = $"{failedAt.ToLocalTime():HH:mm} 압축 실패";
            Check(pingForm.Line(failedRow, wallNow) is { Left: "캐시 만료 · 301K", Soon: true, CanPing: false, CanCompact: true } failedLine
                && failedLine.State == failedText
                && pingForm.Line(failedRow with { Compaction = CacheWatch.Compaction.Retrying }, wallNow) is { State: "압축 다시 시도 중…", CanCompact: false }
                && pingForm.Line(Row(Tried(1, "auto", 0.5, true), true) with { Compaction = CacheWatch.Compaction.Checking }, wallNow) is { State: "압축 확인 중…", CanPing: true }
                && pingForm.Line(Row(Tried(1, "auto", null, false), true) with { Compaction = CacheWatch.Compaction.Running }, wallNow) is { State: "압축 중…", CanCompact: false }
                && pingForm.Line(failedRow with { Partner = false }, wallNow) is { State: var stopped, CanPing: false } && stopped == failedText
                && !(failedRow with { Compaction = CacheWatch.Compaction.None }).Shown(wallNow),
                "cache window: a tried compaction reads running, checking, retrying or failed; a failed one past its lapse can be compacted, not pinged");
            var handover = new CachePings(bridge);
            var handAt = wallNow.AddMinutes(1);
            handover.Compact(pinged, handAt);
            handover.Compact(pinged, handAt.AddSeconds(1));
            var handRequest = File.ReadAllText(Path.Combine(bridge, pinged + ".compact"));
            var handTaken = alive with { CompactHandled = handRequest, CompactOk = true,
                Attempt = new(handAt.AddSeconds(20), "asked", null, null, false, null) };
            var handRunning = handover.Line(Row(handTaken, true) with { Compaction = CacheWatch.Compaction.Running }, handAt.AddSeconds(30));
            var handAfter = handover.Line(Row(alive with { CompactHandled = handRequest, CompactOk = true }, true), handAt.AddSeconds(40));
            Check(handRunning is { State: "압축 중…", CanCompact: false } && handAfter.State == compactAt + " 자동 압축 예정",
                "cache window: an asked 압축 is followed by the mod's attempt once taken up");

            // Turn-end compaction asked by the overlay (2026-11-09): read off the end of the session's log.
            var endRoot = Directory.CreateDirectory(Path.Combine(watchRoot.FullName, "turnend-root"));
            var endProject = Directory.CreateDirectory(Path.Combine(endRoot.FullName, "C--work-end"));
            string Said(string type, string? stop = null, string content = "text", bool side = false, bool meta = false) =>
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["type"] = type, ["isSidechain"] = side, ["isMeta"] = meta,
                    ["message"] = new Dictionary<string, object?> { ["role"] = type, ["stop_reason"] = stop, ["content"] = new[] { new { type = content } } }
                });
            string Sys(string subtype) => JsonSerializer.Serialize(new { type = "system", subtype });
            bool Ended(string name, string[] lines, double quietSeconds = 0)
            {
                var path = Path.Combine(endProject.FullName, name + ".jsonl");
                File.WriteAllLines(path, lines);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-quietSeconds));
                return CacheWatch.TurnEnded(path, DateTimeOffset.Now);
            }
            var prompt = Said("user");
            var answered = Said("assistant", "end_turn");
            var closing = Sys("stop_hook_summary");
            Check(Ended("a", [prompt, Said("assistant", "tool_use", "tool_use"), Said("user", content: "tool_result"), answered, closing])
                && !Ended("b", [prompt, Said("assistant", "tool_use", "tool_use"), Said("user", content: "tool_result")])
                && !Ended("c", [prompt, answered, closing, prompt])
                && Ended("d", [prompt, answered, closing, Said("user", meta: true), prompt, Sys("local_command"), JsonSerializer.Serialize(new { type = "custom-title" })])
                && !Ended("e", [prompt, answered]) && Ended("f", [prompt, answered], quietSeconds: 20)
                && Ended("g", [prompt, answered, closing, Said("assistant", "tool_use", "tool_use", side: true)])
                && !Ended("h", [prompt, Said("assistant", "max_tokens"), closing])
                && !Ended("i", [prompt, answered, closing, "{ half a line"]) && !CacheWatch.TurnEnded(Path.Combine(endProject.FullName, "none.jsonl"), DateTimeOffset.Now),
                "turn end: a main turn counts as ended on an answer that ended it (closed by the stop hooks, or a quiet log) or a lone slash command; never on a prompt, a tool's result or another stop");

            var endBridge = Directory.CreateDirectory(Path.Combine(watchRoot.FullName, "turnend-bridge")).FullName;
            var endId = Guid.NewGuid().ToString();
            var endLog = Path.Combine(endProject.FullName, endId + ".jsonl");
            // The session's log, last written `quiet` minutes ago (the overlay waits for 2 minutes of quiet).
            void Log(string[] lines, double quiet = 3)
            {
                File.WriteAllLines(endLog, lines);
                File.SetLastWriteTimeUtc(endLog, DateTime.UtcNow.AddMinutes(-quiet));
            }
            Log([prompt, answered, closing]);
            var endFile = Path.Combine(endBridge, endId + ".compact");
            int Asked(CacheWatch watch, int code = 7, long context = 450_000, bool busy = false, bool compacted = false, object? attempt = null,
                string handled = "", double lastAgo = 3.3, long threshold = 0)
            {
                File.WriteAllText(Path.Combine(endBridge, endId + ".json"), JsonSerializer.Serialize(new
                {
                    v = 1, code, sessionId = endId, aliveAt = Ms(0.1), busy, lastAt = Ms(lastAgo), context, compacted, handled = "", compactHandled = handled, attempt,
                    threshold
                }));
                return watch.TurnEnds(DateTimeOffset.Now, endRoot.FullName, endBridge);
            }
            int Fresh(Func<CacheWatch, int> ask) { if (File.Exists(endFile)) File.Delete(endFile); return ask(new CacheWatch(start: false)); }
            var endWatch = new CacheWatch(start: false);
            var endFirst = Asked(endWatch);
            var endRequest = File.Exists(endFile) ? File.ReadAllText(endFile) : "";
            var endAgain = Asked(endWatch, handled: endRequest);
            var nextTurn = Asked(endWatch, handled: endRequest, lastAgo: 3.1);
            var ownBuild = Fresh(w => Asked(w, code: CacheWatch.TurnEndOwnCode));
            var ownBuild9 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode - 1));
            var build10 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode));
            // 300k since 2026-11-10 (the user's "35만", then "30만"); builds 8 and 9 compact by themselves only from 400k, so
            // below it the overlay asks them too.
            var at310 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode, context: 310_000));
            var at290 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode, context: 290_000));
            var own380 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode - 1, context: 380_000));
            var own420 = Fresh(w => Asked(w, code: CacheWatch.TurnEndAskedAgainCode - 1, context: 420_000));
            var noCompactBuild = Fresh(w => Asked(w, code: CacheWatch.CompactCodeNeeded - 1));
            var small = Fresh(w => Asked(w, context: CacheWatch.TurnEndAt - 1));
            var answering = Fresh(w => Asked(w, busy: true));
            var done = Fresh(w => Asked(w, compacted: true));
            var triedSince = Fresh(w => Asked(w, attempt: new { at = Ms(0.2), by = "auto", via = "api" }));
            // 2026-11-10: 2 minutes of quiet after the answer first, and no background work still under way; within 30k of
            // the engine's threshold, at once and whatever runs.
            Log([prompt, answered, closing], quiet: 1);
            var tooSoon = Fresh(w => Asked(w, lastAgo: 1.3));
            var nearSoon = Fresh(w => Asked(w, lastAgo: 1.3, context: 440_000, threshold: 467_000));
            var notNear = Fresh(w => Asked(w, lastAgo: 1.3, context: 436_000, threshold: 467_000));
            var started = JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = new[] { new {
                type = "tool_result", content = "Command running in background with ID: b9xdqrwee. Output is being written to: x" } } } });
            // Written by hand: the engine keeps "<" as it is, where JsonSerializer would write it escaped.
            var notified = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"<task-notification>\\n<task-id>b9xdqrwee</task-id>\\n<status>completed</status>\\n</task-notification>\"}}";
            Log([prompt, Said("assistant", "tool_use", "tool_use"), started, answered, closing]);
            var heldWatch = new CacheWatch(start: false);
            var held = Fresh(w => Asked(heldWatch));
            var heldNear = Fresh(w => Asked(w, context: 440_000, threshold: 467_000));
            Log([prompt, Said("assistant", "tool_use", "tool_use"), started, answered, closing, notified, answered, closing]);
            var heldDone = Fresh(w => Asked(heldWatch));
            Log([prompt, answered, closing]);
            File.WriteAllText(endFile, "someone-else");
            var pending = Asked(new CacheWatch(start: false), handled: "older");
            var pendingKept = File.ReadAllText(endFile) == "someone-else";
            Log([prompt]);
            var underWay = Fresh(w => Asked(w));
            Check(endFirst == 1 && endRequest.StartsWith(CacheWatch.TurnEndPrefix) && endRequest.Length == CacheWatch.TurnEndPrefix.Length + 32
                && endAgain == 0 && nextTurn == 1
                && ownBuild == 0 && ownBuild9 == 0 && build10 == 1 && CacheWatch.TurnEndAt == 300_000 && at310 == 1 && at290 == 0 && own380 == 1 && own420 == 0 && noCompactBuild == 0 && small == 0 && answering == 0 && done == 0 && triedSince == 0
                && pending == 0 && pendingKept && underWay == 0,
                "turn end: the overlay asks a large session on builds 4 to 7 and 10 on once per turn, when its log shows the turn ended and nothing else is under way");
            Check(tooSoon == 0 && nearSoon == 1 && notNear == 0 && held == 0 && heldNear == 1 && heldDone == 1,
                $"turn end: asked after 2 minutes of quiet and not while background work waits for its end, but at once near the engine's threshold (soon {tooSoon}/{nearSoon}/{notNear}, work {held}/{heldNear}/{heldDone})");
            // The three ways background work starts in a real log, and its end notification, as they are written there.
            string[] work =
            [
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"Command running in background with ID: boo03go6h. Output is being written to: C:\\\\x\"}]}}",
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":[{\"type\":\"text\",\"text\":\"Async agent launched successfully. (This tool result is internal metadata \u2014 never quote it.)\\nagentId: a161be9ab67bb32b9 (internal ID)\"}]}]}}",
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"Monitor started (task byxo93vxe, expires in 20m unless the source ends first)\"}]}}",
            ];
            string Done(string id, string status) =>
                "{\"type\":\"user\",\"message\":{\"content\":\"<task-notification>\\n<task-id>" + id + "</task-id>\\n<output-file>x</output-file>\\n<status>" + status + "</status>\\n</task-notification>\"}}";
            var side = "{\"isSidechain\":true,\"type\":\"user\",\"message\":{\"content\":\"Command running in background with ID: bside0001.\"}}";
            Check(CacheWatch.WorkUnderWay([work[0]]) && CacheWatch.WorkUnderWay([work[1]]) && CacheWatch.WorkUnderWay([work[2]])
                && !CacheWatch.WorkUnderWay([.. work, Done("boo03go6h", "completed"), Done("a161be9ab67bb32b9", "failed"), Done("byxo93vxe", "killed")])
                && CacheWatch.WorkUnderWay([.. work, Done("boo03go6h", "completed"), Done("a161be9ab67bb32b9", "stopped")])
                && CacheWatch.WorkUnderWay([work[0], Done("byxo93vxe", "completed")]) && !CacheWatch.WorkUnderWay([side]) && !CacheWatch.WorkUnderWay([prompt, answered]),
                "turn end: background work is under way from its start (a command, an agent, a Monitor) until its end notification");

            // 2026-11-10, the user: an answer that ends a line with "지금 /compact 하기 좋음" is compacted at once (150k and
            // up), unless background work is under way. As the engine writes it: Korean as it is, quotes in text escaped.
            string Answer(string text) =>
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";
            var pauseAnswer = Answer("돌고 있는 백그라운드 작업은 없음. 지금 /compact 하기 좋음.");
            var pauseMidAnswer = Answer("지금 /compact 하기 좋음.\\n\\n남은 일은 없음.");
            var quotedAnswer = Answer("\\\"지금 /compact 하기 좋음\\\"이 뜬 경우를 말함.");
            var stopFeedback = "{\"type\":\"user\",\"isMeta\":true,\"message\":{\"role\":\"user\",\"content\":\"Stop hook feedback: x\"}}";
            int Pause(string[] lines, int code = 10, long context = 200_000)
            {
                Log(lines, quiet: 0);
                return Fresh(w => Asked(w, code: code, context: context, threshold: 467_000));
            }
            var paused = Pause([prompt, pauseAnswer, closing]);
            var pausedMid = Pause([prompt, pauseMidAnswer, closing]);
            var quoted = Pause([prompt, quotedAnswer, closing]);
            var pausedSmall = Pause([prompt, pauseAnswer, closing], context: CacheWatch.LargeContext - 1);
            var pausedWork = Pause([prompt, Said("assistant", "tool_use", "tool_use"), started, pauseAnswer, closing]);
            var pausedOwn = Pause([prompt, pauseAnswer, closing], code: 9);
            var pausedOwnLarge = Pause([prompt, pauseAnswer, closing], code: 9, context: 450_000);
            var pausedLarge = Pause([prompt, pauseAnswer, closing], context: 450_000);
            var pausedThenHook = Pause([prompt, pauseAnswer, closing, stopFeedback, answered, closing]);
            var pausedLastTurn = Pause([prompt, pauseAnswer, closing, prompt, answered, closing]);
            Log([prompt, answered, closing]);
            Check(paused == 1 && pausedMid == 1 && quoted == 0 && pausedSmall == 0 && pausedWork == 0 && pausedOwn == 1 && pausedOwnLarge == 0
                && pausedLarge == 1 && pausedThenHook == 1 && pausedLastTurn == 0,
                $"turn end: an answer that says \"지금 /compact 하기 좋음\" is compacted at once from 150k, not while background work runs, not when only quoted or said a turn before ({paused}{pausedMid}{quoted}{pausedSmall}{pausedWork}{pausedOwn}{pausedOwnLarge}{pausedLarge}{pausedThenHook}{pausedLastTurn})");

            // 2026-11-13, the user's "압축이 중복으로": a turn-end request asked 1 minute ago and not taken up is withdrawn
            // once the session made another request (more than 2 seconds) after it, or was compacted after it; kept while
            // it only waits, once the mod took it up, and for the person's own 압축. 1: withdrawn and gone, 0: kept, -1: neither.
            int Stale(double lastAgo, double? compactedAgo = null, string handled = "", string request = "turnend-0123456789abcdef")
            {
                File.WriteAllText(endFile, request);
                File.SetLastWriteTimeUtc(endFile, wallNow.AddMinutes(-1).UtcDateTime);
                var b = new CacheWatch.Bridge(wallNow, DateTimeOffset.FromUnixTimeMilliseconds(Ms(lastAgo)), "", null, false, null, 0, 0,
                    compactedAgo is { } c ? DateTimeOffset.FromUnixTimeMilliseconds(Ms(c)) : null, Code: CacheWatch.ModCode, CompactHandled: handled);
                var gone = CacheWatch.WithdrawStale(endId, b, endBridge);
                return gone && !File.Exists(endFile) ? 1 : !gone && File.Exists(endFile) ? 0 : -1;
            }
            var staleTurn = Stale(0.5);
            var staleWaits = Stale(1.05);
            var staleMargin = Stale(1 - 1.0 / 60);
            var staleCompacted = Stale(1.05, compactedAgo: 0.5);
            var staleCompactedBefore = Stale(1.05, compactedAgo: 3);
            var staleTaken = Stale(0.5, handled: "turnend-0123456789abcdef");
            var staleButton = Stale(0.5, request: "asked-0123");
            // Through the scan, as on 11-13: the session answering a new turn (busy) when the overlay looks.
            File.WriteAllText(endFile, "turnend-0123456789abcdef");
            File.SetLastWriteTimeUtc(endFile, wallNow.AddMinutes(-1).UtcDateTime);
            var scanAsked = Asked(new CacheWatch(start: false), busy: true, lastAgo: 0.5);
            var scanGone = !File.Exists(endFile);
            Check(staleTurn == 1 && staleWaits == 0 && staleMargin == 0 && staleCompacted == 1 && staleCompactedBefore == 0 && staleTaken == 0 && staleButton == 0
                && scanAsked == 0 && scanGone,
                $"turn end: a request not taken up before the session's next request or a compaction is withdrawn, not run a turn late ({staleTurn}{staleWaits}{staleMargin}{staleCompacted}{staleCompactedBefore}{staleTaken}{staleButton} scan {scanAsked}/{scanGone})");
            Check(File.ReadAllText(Path.Combine(ClaudeModSetup.BundledFolder, "tokenoverlay-cache", "hooks", "register.ts")) is var mod
                && mod.Contains("async function staleTurnEnd(") && mod.Contains("turnEnd && (await staleTurnEnd($, compactAsk))") && mod.Contains("const STALE_AFTER_MS = 2_000")
                && (long)CacheWatch.TurnEndStaleAfter.TotalMilliseconds == 2_000,
                "turn end: the mod drops a stale turn-end request itself too, with the overlay's 2-second margin");
            // 2026-11-13, the user's "자동압축뒤에 또 압축": a /compact the mod queued that another compaction overtook is
            // vetoed in session.compact. Its behaviour is tested by docs/mod-tests/overtaken.test.ts (claude plugin test).
            Check(mod.Contains("function overtaken(") && mod.Contains("if (vetoed) {") && mod.Contains("return { skip:")
                && mod.Contains("const OWN_INSTRUCTIONS = [COMPACT_INSTRUCTIONS, ASKED_INSTRUCTIONS, TURN_END_INSTRUCTIONS]")
                && mod.Contains("kept.compactedAt > kept.lastAt || (kept.attempt !== undefined && kept.compactedAt > kept.attempt.at)"),
                "turn end: the mod vetoes a compaction it asked for once another came first with nothing new since");
        }
        finally { watchRoot.Delete(true); }
        var compacted = new ClaudeTurnReader();
        compacted.ProcessLine(User("k1", 0));
        compacted.ProcessLine(Cached("p1", 2, 10, 1_000, 300_000, 1_000, 0));
        compacted.ProcessLine(JsonSerializer.Serialize(new { type = "system", subtype = "compact_boundary", timestamp = At(30) }));
        var afterCompact = compacted.Cache!;
        Check(afterCompact is { Compacted: true, LastContext: 0 } && CacheStatus.CapsuleNote(afterCompact, lastAt.AddMinutes(61)) == "캐시 만료 · 압축됨"
            && CacheStatus.CapsuleAlert(afterCompact, lastAt.AddMinutes(61)) && CacheStatus.CapsuleBar(afterCompact, lastAt.AddMinutes(61)) is null
            && CacheStatus.Line(afterCompact, lastAt.AddMinutes(61)) == "캐시 만료(추정) · 압축됨 · 다시 보내면 요약부터 새로 저장 · 세션 캐시 읽기 100%",
            "cache: after a compaction and a lapse the lapse comes first, in the alert colour, without the old context size");
        Check(CacheStatus.CapsuleNote(afterCompact, lastAt.AddMinutes(13)) == "캐시 46분 남음" && CacheStatus.CapsuleBar(afterCompact, lastAt.AddMinutes(13)) is { Level: CacheLevel.Fresh }
            && !CacheStatus.CapsuleAlert(afterCompact, lastAt.AddMinutes(13))
            && CacheStatus.Line(afterCompact, lastAt.AddMinutes(13)) == "캐시 유지 · 46분 남음 · 압축됨 · 다음 요청 때 새로 저장 · 세션 캐시 읽기 100%",
            "cache: a compacted chat whose cache is alive still shows the minutes left first");
        compacted.ProcessLine(User("k2", 40));
        compacted.ProcessLine(Cached("p2", 41, 10, 30_000, 0, 30_000, 0));
        Check(compacted.Cache is { Compacted: false, LastContext: 30_010 }, "cache: the first request after a compaction brings the usual countdown back");
        // ----- Context ring: last request's context against the auto-compact window set for its model.
        var ringSettings = Path.Combine(scratch, "ring-settings-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(ringSettings, """{"model":"x","modelSettings":{"claude-opus-5-5":{"autoCompactWindow":400000},"claude-sonnet-5-5[1m]":{"autoCompactWindow":800000},"bad":{"autoCompactWindow":"x"}}}""");
        Check(ContextUsage.ClaudeLimit("claude-opus-5-5", ringSettings) == 400_000 && ContextUsage.ClaudeLimit("claude-sonnet-5-5", ringSettings) == 800_000
            && ContextUsage.ClaudeLimit("bad", ringSettings) == ContextUsage.DefaultClaudeWindow
            && ContextUsage.ClaudeLimit("claude-opus-5-5", ringSettings + ".missing") == ContextUsage.DefaultClaudeWindow
            && ContextUsage.ClaudeFill(200_000, "claude-opus-5-5", settingsPath: ringSettings) == 0.5 && ContextUsage.ClaudeFill(0, "claude-opus-5-5", settingsPath: ringSettings) is null,
            "context ring: the model's auto-compact window from Claude Code's settings, else the whole window");
        Check(ContextUsage.ClaudeNote("claude-opus-5-5", settingsPath: ringSettings) == "Opus 5.5\n400K" && ContextUsage.ClaudeNote(null, settingsPath: ringSettings) == "1M"
            && ContextUsage.Note("gpt-6-astra", 272_000) == "6 Astra\n272K" && ContextUsage.Note("claude-opus-5-5", 0) is null
            && ContextUsage.Note(null, 999_400) == "999K" && ContextUsage.Note(null, 1_048_576) == "1M" && ContextUsage.Note(null, 1_500_000) == "1.5M",
            "context ring: beside it the model's short name and the limit the ring is measured against, in thousands below a million, else in millions");
        // A session started before /autocompact moved the setting keeps the window it started with; its mod says which.
        var ringBridge = Directory.CreateTempSubdirectory("ringbridge-").FullName;
        var ringSession = Guid.NewGuid().ToString();
        var ringNow = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(ringBridge, ringSession + ".json"),
            JsonSerializer.Serialize(new { v = 1, aliveAt = ringNow.AddSeconds(-20).ToUnixTimeMilliseconds(), window = 600_000, threshold = 567_000 }));
        var ringWindow = CacheWatch.WindowOf(ringSession, ringNow, ringBridge);
        var staleWindow = CacheWatch.WindowOf(ringSession, ringNow.AddMinutes(5), ringBridge);
        Check(ringWindow == 600_000 && staleWindow is null && CacheWatch.WindowOf(Guid.NewGuid().ToString(), ringNow, ringBridge) is null
            && ContextUsage.ClaudeFill(528_000, "claude-opus-5-5", ringWindow, ringSettings) == 0.88
            && ContextUsage.ClaudeNote("claude-opus-5-5", ringWindow, ringSettings) == "Opus 5.5\n600K",
            "context ring: the window a running session's mod reports wins over settings.json; a stale report does not");
        Directory.Delete(ringBridge, true);
        File.Delete(ringSettings);
        Check(ContextUsage.Level(0.49) == CacheLevel.Fresh && ContextUsage.Level(0.5) == CacheLevel.Middle && ContextUsage.Level(0.8) == CacheLevel.Soon,
            "context ring: green below half, amber below 80%, red from there");
        var ringReader = new ClaudeTurnReader();
        ringReader.ProcessLine(User("g1", 0));
        ringReader.ProcessLine(JsonSerializer.Serialize(new { type = "assistant", timestamp = At(1), message = new { id = "m1", model = "claude-opus-5-5",
            stop_reason = "end_turn", usage = new { input_tokens = 10, cache_creation_input_tokens = 1_000, cache_read_input_tokens = 150_000, output_tokens = 5 } } }));
        var ringBefore = (ringReader.Context, ringReader.LastModel);
        ringReader.ProcessLine(JsonSerializer.Serialize(new { type = "system", subtype = "compact_boundary", timestamp = At(2) }));
        Check(ringBefore == (151_010, "claude-opus-5-5") && ringReader.Context == 0,
            "context ring: the last request's context and model; nothing after a compaction that logs no summary size");
        string Boundary(long post, int at) => JsonSerializer.Serialize(new { type = "system", subtype = "compact_boundary", timestamp = At(at),
            compactMetadata = new { trigger = "manual", preTokens = 300_000, postTokens = post } });
        string Reply(string id, long context, int at, string model = "claude-opus-5-5") => JsonSerializer.Serialize(new { type = "assistant", timestamp = At(at),
            message = new { id, model, stop_reason = "end_turn", usage = new { input_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = context, output_tokens = 5 } } });
        var learn = new ClaudeTurnReader();
        learn.ProcessLine(User("h1", 0));
        learn.ProcessLine(Reply("r1", 60_000, 1));
        learn.ProcessLine(User("h2", 2));
        learn.ProcessLine(Reply("r2", 250_000, 3));
        learn.ProcessLine(Boundary(10_000, 4));
        var fromFirst = learn.Context;
        learn.ProcessLine(User("h3", 5));
        learn.ProcessLine(Reply("r3", 74_000, 6));
        var afterFirst = learn.Context;
        learn.ProcessLine(User("h4", 7));
        learn.ProcessLine(Reply("r4", 200_000, 8));
        learn.ProcessLine(Boundary(12_000, 9));
        var fromLearned = learn.Context;
        learn.ProcessLine(User("h5", 10));
        learn.ProcessLine(Reply("r5", 0, 11, "<synthetic>"));
        Check(fromFirst == 70_000 && afterFirst == 74_000 && fromLearned == 76_000 && learn.Context == 76_000,
            "context ring: after a compaction, the summary plus what this chat's last post-compaction request added (else its first request); a synthetic reply changes nothing");
        Check(ContextUsage.CodexFill(new TokenSnapshot("x", "x", 0, 0, 0, 0, 0, 64_000, 256_000, default)) == 0.25
            && ContextUsage.CodexFill(new TokenSnapshot("x", "x", 0, 0, 0, 0, 0, 64_000, 0, default)) is null,
            "context ring: Codex uses its log's context window");
        // ----- Settings: placement for each app, the ring switch.
        var defaults = OverlaySettings.CreateDefault();
        Check(defaults is { ClaudeAnchorMode: AnchorMode.InsideBottomRight, CodexAnchorMode: AnchorMode.InsideBottomRight, ContextRingEnabled: true },
            "settings: both apps default to the bottom right, ring on");
        var old = OverlaySettings.ParseJson("""{"SettingsVersion":1,"AnchorMode":1}""").Settings;
        Check(old is { ClaudeAnchorMode: AnchorMode.InsideTopRight, CodexAnchorMode: AnchorMode.InsideTopRight, ContextRingEnabled: true },
            "settings: the one placement of an older file goes to both apps");
        old.SetAnchor(HostApp.Codex, AnchorMode.TitleBarTopRight);
        old.ContextRingEnabled = false;
        var again = OverlaySettings.ParseJson(old.Serialize()).Settings;
        Check(!defaults.CpuMeterEnabled && !old.CpuMeterEnabled && !again.CpuMeterEnabled, "settings: the CPU meter starts off, also for an older file");
        old.CpuMeterEnabled = true;
        Check(OverlaySettings.ParseJson(old.Serialize()).Settings.CpuMeterEnabled, "settings: the CPU meter switch survives a save");
        old.CpuMeterEnabled = false;
        Check(again is { ClaudeAnchorMode: AnchorMode.InsideTopRight, CodexAnchorMode: AnchorMode.TitleBarTopRight, ContextRingEnabled: false }
            && again.AnchorFor(HostApp.Codex) == AnchorMode.TitleBarTopRight && again.AnchorFor(HostApp.Claude) == AnchorMode.InsideTopRight
            && System.Text.Json.JsonDocument.Parse(old.Serialize()).RootElement.GetProperty("AnchorMode").GetInt32() == (int)AnchorMode.InsideTopRight,
            "settings: each app's placement and the ring switch survive a save; the old field keeps Claude's for older versions");
        // ----- Theme: automatic follows Windows' app mode; a fixed choice stays put when Windows changes, and is saved.
        Check(defaults.Theme == ThemePreference.Auto && OverlaySettings.ParseJson("""{"SettingsVersion":1,"Theme":9}""").Settings.Theme == ThemePreference.Auto,
            "settings: theme defaults to automatic (an unknown value too)");
        old.Theme = ThemePreference.Dark;
        Check(OverlaySettings.ParseJson(old.Serialize()).Settings.Theme == ThemePreference.Dark, "settings: the chosen theme survives a save");
        var windowsTheme = new FakeThemeSource { Current = OverlayThemeKind.Light };
        var preferred = new PreferredOverlayThemeSource(windowsTheme, ThemePreference.Auto);
        var themeChanges = 0;
        preferred.Changed += (_, _) => themeChanges++;
        windowsTheme.Set(OverlayThemeKind.Dark);
        var autoFollows = preferred.Current == OverlayThemeKind.Dark && themeChanges == 1;
        preferred.Preference = ThemePreference.Light;
        windowsTheme.Set(OverlayThemeKind.Light);
        windowsTheme.Set(OverlayThemeKind.Dark);
        Check(autoFollows && preferred.Current == OverlayThemeKind.Light && themeChanges == 2,
            "theme: automatic follows Windows; a fixed choice applies at once and ignores Windows' changes");
        preferred.Preference = ThemePreference.Auto;
        Check(preferred.Current == OverlayThemeKind.Dark && themeChanges == 3, "theme: back to automatic takes Windows' current mode");
        preferred.Dispose();
        Check(OverlaySettings.ParseJson("""{"SettingsVersion":1}""").Settings is { ClaudeAnchorMode: AnchorMode.InsideBottomRight, CodexAnchorMode: AnchorMode.InsideBottomRight },
            "settings: a file without any placement gets the bottom right");
        var sub = new ClaudeTurnReader();
        sub.ProcessLine(User("s1", 0));
        sub.ProcessLine(Cached("q1", 1, 10, 30_000, 0, 0, 30_000));
        Check(sub.Cache is { TtlMinutes: 5 } && CacheStatus.Line(new CacheState(0, 0), lastAt) is null
            && CacheStatus.Line(new CacheState(1000, 960), lastAt) == "세션 캐시 읽기 96%",
            "cache: a 5-minute write sets a 5-minute lifetime; without times only the read share is shown (Codex)");
        Check(CacheStatus.JoinLines("a", null, "b\nc") == "a\nb\nc" && CacheStatus.JoinLines(null, "") is null, "cache line goes above the weekly lines");
        var noted = TurnPresentation.Create(new TokenSnapshot("x", "x", 0, 0, 0, 0, 0, 0, 0, default)
            { Turn = cacheTurns[0], App = HostApp.Claude, CacheNote = "캐시 47분 남음", CacheBar = new CacheBar(0.78, CacheLevel.Fresh) });
        Check(noted is { SubLine: "모델 —", SubLineLeft: "캐시 47분 남음", SubLineLeftAlert: false, SubLineLeftBar.Level: CacheLevel.Fresh },
            "capsule lower line: cache time on the left in its bar, model rate on the right");
        // ----- Token renewal is left to Claude Code: the overlay only wakes it when the token is about to run out.
        Check(ClaudeTokenRenewal.ParseExpiry(Credentials(123)) == 123 && ClaudeTokenRenewal.ParseExpiry("{}") is null
            && ClaudeTokenRenewal.ParseExpiry("x") is null, "renewal: expiry is read from the login file");
        long expiry = now.AddHours(2).ToUnixTimeMilliseconds();
        var runs = new List<string>();
        bool Ran(string[] arguments, int timeout) { runs.Add(arguments[0]); return true; }
        Check(ClaudeTokenRenewal.Renew(now, () => expiry, Ran) is { Attempted: false } && runs.Count == 0
            && ClaudeTokenRenewal.Renew(now, () => null, Ran) is { Attempted: false } && runs.Count == 0,
            "renewal: a token with time left, or no login at all, starts nothing");
        expiry = now.AddMinutes(5).ToUnixTimeMilliseconds();
        bool StatusRenews(string[] arguments, int timeout) { runs.Add(arguments[0]); expiry = now.AddHours(8).ToUnixTimeMilliseconds(); return true; }
        Check(ClaudeTokenRenewal.Renew(now, () => expiry, StatusRenews) is { Attempted: true, Renewed: true, Note: { } viaStatus }
            && viaStatus.Contains("auth status") && runs.SequenceEqual(["auth"]), "renewal: the free command is tried first and is enough when it renews");
        runs.Clear(); expiry = now.AddMinutes(-20).ToUnixTimeMilliseconds();
        bool OnlyRequestRenews(string[] arguments, int timeout)
        {
            runs.Add(arguments[0]);
            if (arguments[0] == "-p") expiry = now.AddHours(8).ToUnixTimeMilliseconds();
            return true;
        }
        Check(ClaudeTokenRenewal.Renew(now, () => expiry, OnlyRequestRenews) is { Renewed: true, Note: { } viaRequest }
            && viaRequest.Contains("짧은 요청") && runs.SequenceEqual(["auth", "-p"]), "renewal: an expired token falls back to one tiny request");
        runs.Clear(); expiry = now.AddMinutes(1).ToUnixTimeMilliseconds();
        Check(ClaudeTokenRenewal.Renew(now, () => expiry, Ran) is { Attempted: true, Renewed: false, Note: { } failed }
            && failed.Contains("실패") && runs.Count == 2, "renewal: when neither renews, it says so");
        Check(ClaudeTokenRenewal.TinyRequest.Contains("haiku") && ClaudeTokenRenewal.TinyRequest.Contains("--no-session-persistence")
            && !ClaudeTokenRenewal.TinyRequest.Concat(ClaudeTokenRenewal.StatusCommand).Any(a => a.Contains("refresh", StringComparison.OrdinalIgnoreCase)),
            "renewal: the request is the smallest model with no tools and no saved session");
        var renewCalls = 0;
        var renewing = new WeeklyUsageMonitor(() => [apiHistory], Path.Combine(apiRoot, "projects"), Path.Combine(apiRoot, "renew-store.json"),
            _ => new(12, "정상"), _ => { renewCalls++; return new(true, true, "10-05 19:00 토큰 갱신됨 (짧은 요청)"); });
        renewing.Update(now); renewing.Update(now.AddMinutes(2));
        Check(renewCalls == 1 && renewing.ApiStatus == "정상 · 10-05 19:00 토큰 갱신됨 (짧은 요청)", "renewal: attempted at most once per half hour, outcome shown in the status");
        renewing.Dispose();
        var renewed = new WeeklyUsageMonitor(() => [apiHistory], Path.Combine(apiRoot, "projects"), Path.Combine(apiRoot, "renew-store.json"), _ => new(12, "정상"));
        renewed.Update(now.AddMinutes(3));
        Check(renewed.ApiStatus.Contains("토큰 갱신됨"), "renewal: the last outcome survives a restart");
        try { Directory.Delete(root, true); } catch (IOException) { }
        return new { Passed = results.Count, Checks = results };
    }
}

// A theme source the checks switch by hand.
internal sealed class FakeThemeSource : IOverlayThemeSource
{
    public OverlayThemeKind Current { get; set; }
    public event EventHandler? Changed;
    public void Set(OverlayThemeKind kind) { Current = kind; Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose() { }
}
