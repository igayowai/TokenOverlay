using System.Text;
using System.Text.Json;

namespace TokenOverlay;

internal static class TurnChecks
{
    public static void Render(string directory, string log)
    {
        Directory.CreateDirectory(directory);
        var turn = new TurnStatisticsReader().Read(log);
        var snapshot = new TokenSnapshot("preview", log, 0, 0, 0, 0, 0, 0, 0, DateTime.UtcNow)
            { Turn = turn, ThreadTitle = "작업 턴별 평균 토큰 속도 확인" };
        var presentation = TurnPresentation.Create(snapshot);
        var host = new CodexWindowInfo(new IntPtr(1), new IntRect(0,0,3840,2052),
            new IntRect(0,0,3840,2052), null, new IntRect(0,0,3840,2052), 216,
            new WindowChromeMetrics(82,50,5,5,9));
        foreach (var expanded in new[] { false, true })
        {
            using var form = new TokenStripForm();
            form.SetPresentation(presentation);
            form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
            var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
                host, AnchorMode.InsideTopRight, expanded, presentation.ExpandedRows.Count, false, Presentation: presentation));
            form.ApplyLayout(layout);
            using var bitmap = new Bitmap(form.Width, form.Height);
            bitmap.SetResolution(216,216);
            form.DrawToBitmap(bitmap, new Rectangle(0,0,form.Width,form.Height));
            bitmap.Save(Path.Combine(directory, expanded ? "expanded.png" : "collapsed.png"));
        }
    }

    public static object Run(string scratch)
    {
        var results = new List<string>();
        void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("FAIL: " + name);
            results.Add(name);
        }
        string E(string type, string? id = null, long input = 0, long output = 0, long? ms = null) =>
            JsonSerializer.Serialize(new { timestamp = "2026-10-05T00:00:00Z", type = "event_msg",
                payload = new { type, turn_id = id, duration_ms = ms,
                    info = new { total_token_usage = new { input_tokens = input, output_tokens = output,
                        cached_input_tokens = input / 2, reasoning_output_tokens = output / 4 } } } });
        var r = new TurnStatisticsReader();
        r.ProcessLine(E("token_count", input:100, output:10));
        r.ProcessLine(E("task_started", "a"));
        r.ProcessLine(E("token_count", input:1100, output:210));
        r.ProcessLine(E("token_count", input:1100, output:210));
        r.ProcessLine(E("task_complete", "a", ms:10000));
        Check(r.Snapshot() is { Input:1000, Output:200, Total:1200, Seconds:10, TokensPerSecond:20 }, "turn delta, duration, reasoning not double-counted, duplicate usage");
        r.ProcessLine(E("task_started", "a"));
        r.ProcessLine(E("task_complete", "a", ms:500));
        Check(r.Snapshot()?.Seconds == 10, "duplicate turn boundary ignored");
        r.ProcessLine(E("task_started", "b"));
        Check(r.Snapshot() is { TurnId:"a", Running:true, TokensPerSecond:20 }, "previous completed turn visible while running");
        r.ProcessLine(E("task_complete", "b", ms:1000));
        Check(r.Snapshot() is { TurnId:"b", State:"집계 대기", Input:null }, "missing usage is pending, not zero");
        r.ProcessLine(E("token_count", input:1400, output:310));
        Check(r.Snapshot() is { Input:300, Output:100, TokensPerSecond:100 }, "late usage after completion updates same turn without new prompt");
        r.ProcessLine(E("task_started", "c"));
        r.ProcessLine(E("token_count", input:10, output:5));
        r.ProcessLine(E("task_complete", "c", ms:1000));
        Check(r.Snapshot() is { Input:null, State:"기록 불완전" }, "counter reset does not invent negative usage");
        r.ProcessLine(E("task_started", "d"));
        r.ProcessLine(E("token_count", input:20, output:10));
        r.ProcessLine(E("turn_aborted", "d", ms:2000));
        Check(r.Snapshot() is { State:"중단", Output:5, Seconds:2 }, "interrupted turn distinguished");
        r.ProcessLine(E("task_started", "e"));
        r.ProcessLine(E("token_count", input:50, output:30));
        r.ProcessLine(E("task_complete", "e", ms:0));
        Check(r.Snapshot()?.TokensPerSecond is null, "zero duration cannot produce infinite rate");
        var crossed = new TurnStatisticsReader();
        crossed.ProcessLine(E("task_started", "old"));
        crossed.ProcessLine(E("token_count", input:100,output:10));
        crossed.ProcessLine(E("task_complete", "old",ms:1000));
        crossed.ProcessLine(E("task_started", "new"));
        crossed.ProcessLine(E("token_count", "old",input:120,output:15));
        crossed.ProcessLine(E("task_complete", "new",ms:1000));
        Check(crossed.Snapshot()?.Problem == "턴 경계를 넘긴 지연 기록", "cross-turn late counters not silently assigned");
        var isolated = new TurnStatisticsReader();
        Check(isolated.Snapshot() is null, "independent session state");
        isolated.ProcessLine(E("task_started", "z"));
        isolated.ProcessLine("{ broken event");
        isolated.ProcessLine(E("token_count", input:100, output:10));
        isolated.ProcessLine(E("task_complete", "z", ms:1000));
        Check(isolated.Snapshot()?.Input is null, "malformed record is flagged");
        Check(isolated.Cache is { SessionInput: 100, SessionCached: 50 }, "Codex cache: session totals come from the running total");
        isolated.ProcessLine(E("task_started", "z2"));
        isolated.ProcessLine(E("token_count", input:150, output:30));
        isolated.ProcessLine(E("task_complete", "z2", ms:1000));
        Check(isolated.Snapshot() is { Problem: null, Input: 50, Output: 20 }, "turn after a damaged line counts again once the running total was seen");
        string Continued(long input, long output, long lastInput, long lastOutput) =>
            E("token_count", input: input, output: output).Replace("\"total_token_usage\"",
                $"\"last_token_usage\":{{\"input_tokens\":{lastInput},\"cached_input_tokens\":{lastInput / 2},\"output_tokens\":{lastOutput}}},\"total_token_usage\"");
        var continuedFile = new TurnStatisticsReader();
        continuedFile.ProcessLine(E("task_started", "next"));
        continuedFile.ProcessLine(Continued(66_177_282, 360_249, 78_964, 244));
        continuedFile.ProcessLine(Continued(66_820_917, 364_612, 124_135, 2_554));
        continuedFile.ProcessLine(E("task_complete", "next", ms: 1000));
        Check(continuedFile.Snapshot() is { Input: 722_599, Output: 4_607, Problem: null },
            "a chat continued in a new file: its first turn counts from where the earlier file's total stood, not from zero");
        var freshChat = new TurnStatisticsReader();
        freshChat.ProcessLine(E("task_started", "first"));
        freshChat.ProcessLine(Continued(5_000, 100, 5_000, 100));
        freshChat.ProcessLine(Continued(11_000, 300, 6_000, 200));
        freshChat.ProcessLine(E("task_complete", "first", ms: 1000));
        Check(freshChat.Snapshot() is { Input: 11_000, Output: 300 }, "a new chat's first turn still counts from zero");
        string Item(string time, string kind, string? role = null) =>
            $$$"""{"timestamp":"{{{time}}}","ordinal":7,"type":"response_item","payload":{"type":"{{{kind}}}","id":"x"{{{(role is null ? "" : $",\"role\":\"{role}\"")}}},"content":[]}}""";
        string Tok(string time, long output) => E("token_count", input: 1000 + output, output: output).Replace("2026-10-05T00:00:00Z", time);
        var outputLog = Path.Combine(scratch, "output-items.jsonl");
        File.WriteAllLines(outputLog, [
            E("task_started", "items"), Tok("2026-10-05T00:00:01Z", 10),
            Item("2026-10-05T00:00:10Z", "reasoning"), Item("2026-10-05T00:00:30Z", "custom_tool_call"),
            Item("2026-10-05T00:00:31Z", "custom_tool_call_output"), Item("2026-10-05T00:00:32Z", "agent_message"),
            Item("2026-10-05T00:00:33Z", "message", "user"), Tok("2026-10-05T00:00:34Z", 50),
            Tok("2026-10-05T00:00:40Z", 80),
            Item("2026-10-05T00:00:50Z", "message", "assistant"), Tok("2026-10-05T00:00:51Z", 90),
            E("task_complete", "items", ms: 60000)]);
        var itemsReader = new TurnStatisticsReader();
        itemsReader.Read(outputLog);
        var items = itemsReader.DisplayedResponses;
        Check(items.Count == 3 && items[0].LastItem == DateTimeOffset.Parse("2026-10-05T00:00:30Z") && items[1].LastItem is null
            && items[2].LastItem == DateTimeOffset.Parse("2026-10-05T00:00:50Z"),
            "each counted response keeps when the model last wrote for it; tool results, other agents and user messages do not count");
        var parsedItems = new TurnStatisticsReader();
        foreach (var line in File.ReadAllLines(outputLog)) parsedItems.ProcessLine(line);
        Check(parsedItems.Snapshot() is not null && parsedItems.DisplayedResponses.Select(r => r.LastItem).SequenceEqual(items.Select(r => r.LastItem)),
            "lines handed over one by one give the same output times");
        string At(string time, long input, long output) => E("token_count", input: input, output: output).Replace("2026-10-05T00:00:00Z", time);
        var timed = new TurnStatisticsReader();
        timed.ProcessLine(At("2026-10-05T01:00:00Z", 100, 10));
        timed.ProcessLine(At("2026-10-05T01:02:00Z", 100, 10));
        var requestAt = DateTimeOffset.Parse("2026-10-05T01:00:00Z");
        Check(timed.Cache is { LastRequest: { } at, TtlMinutes: null, HitOnly: true } && at == requestAt,
            "Codex cache: the last request is the last record that counted new input, and no lifetime is counted");
        string WithLast(string line, long input, long cached) =>
            line.Replace("\"info\":{", $"\"info\":{{\"last_token_usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":{cached}}},");
        timed.ProcessLine(WithLast(At("2026-10-05T01:05:00Z", 300, 20), 41_082, 40_715));
        var codex = timed.Cache;
        var sent = requestAt.AddMinutes(5);
        Check(new[] { 1.0, 31, 600 }.All(minutes => CacheStatus.CapsuleNote(codex, sent.AddMinutes(minutes)) == "캐시히트 99.1%"
                && CacheStatus.CapsuleBar(codex, sent.AddMinutes(minutes)) is null && !CacheStatus.CapsuleAlert(codex, sent.AddMinutes(minutes)))
            && CacheStatus.Line(codex, sent.AddMinutes(600)) == "마지막 요청 캐시히트 99.1% · 세션 캐시 읽기 50%",
            "Codex cache: the capsule shows the last request's hit share, with no countdown, bar or lapse at any age");
        Check(CacheStatus.HitPercent(41_082, 41_082) == "100.0%" && CacheStatus.HitPercent(41_081, 41_082) == "99.9%"
            && CacheStatus.HitPercent(0, 41_082) == "0.0%" && CacheStatus.HitPercent(0, 0) is null,
            "Codex cache hit: one decimal, never 100% while a token missed");
        var requests = timed.TakeRequests();
        // The first record carried no per-request usage, so only the second request is recorded.
        Check(requests.Count == 1 && requests[0] is { Input: 41_082, Cached: 40_715, PreviousInput: 0, GapMinutes: 5 }
            && timed.TakeRequests().Count == 0,
            "Codex cache record: each request with its gap to the one before, taken once");
        var logFolder = Path.Combine(scratch, "codex-cache");
        if (Directory.Exists(logFolder)) Directory.Delete(logFolder, true);
        var previousFolder = CodexCacheLog.UseFolder(logFolder);
        try
        {
            var first = new CodexRequest(requestAt, "s", "gpt-6-astra", null, 0, 1000, 0);
            var resumed = new CodexRequest(requestAt.AddHours(9), "s", "gpt-6-astra", 540, 1000, 1200, 237);
            CodexCacheLog.Append([first]);
            CodexCacheLog.Append([first, resumed]);
            CodexCacheLog.UseFolder(logFolder); // as after a restart: what was written is read back from the file
            CodexCacheLog.Append([first, resumed, resumed with { Session = "other" }]);
            var lines = File.ReadAllLines(CodexCacheLog.PathNow);
            Check(lines.Length == 3 && lines[1].Contains("\"gapMin\":540") && lines[1].Contains("\"hit\":19.8") && lines[1].Contains("\"cached\":237")
                && lines[0].Contains("\"gapMin\":null") && lines[2].Contains("\"session\":\"other\""),
                "Codex cache record file: one line per request, a request already written is skipped, also after a restart");
        }
        finally { CodexCacheLog.UseFolder(previousFolder); }
        var stale = new TurnStatisticsReader();
        stale.ProcessLine(E("task_started", "s1"));
        stale.ProcessLine(E("token_count", input:100, output:10));
        stale.ProcessLine("{ broken event");
        stale.ProcessLine(E("task_complete", "s1", ms:1000));
        stale.ProcessLine(E("task_started", "s2"));
        Check(stale.Snapshot() is { TurnId: "s1", Problem: "손상된 기록 포함" }, "turn holding the damaged line is flagged");
        stale.ProcessLine(E("token_count", input:200, output:40));
        stale.ProcessLine(E("task_complete", "s2", ms:1000));
        Check(stale.Snapshot() is { TurnId: "s2", Problem: "손상된 기록 포함" }, "turn whose starting total may be stale stays flagged");
        var path = Path.Combine(scratch, "turn-fixture.jsonl");
        File.WriteAllText(path, E("task_started", "utf8") + "\n");
        var disk = new TurnStatisticsReader();
        disk.Read(path);
        var usage = E("token_count", input:100, output:20);
        File.AppendAllText(path, usage[..(usage.Length/2)]);
        Check(disk.Read(path)?.Input is null, "partial appended JSON retained until newline");
        File.AppendAllText(path, usage[(usage.Length/2)..] + "\n" + E("task_complete", "utf8", ms:2000) + "\n");
        Check(disk.Read(path) is { Total:120, TokensPerSecond:10 }, "incremental completion parsed");
        Check(new TurnStatisticsReader().Read(path) == disk.Read(path), "restart reconstruction equals incremental result");
        File.WriteAllText(path, E("task_started", "replaced") + "\n");
        Check(disk.Read(path)?.TurnId == "replaced", "file truncation resets cursor");
        var main = new WindowCandidateFacts(new IntPtr(1), 1, true, true, false, IntPtr.Zero,
            0, new IntRect(0,0,1200,900), "Chrome_WidgetWin_1");
        var pet = new WindowCandidateFacts(new IntPtr(2), 1, true, true, false, IntPtr.Zero,
            0x80000, new IntRect(0,0,200,200), "Chrome_WidgetWin_1");
        Check(CodexWindowClassifier.Select([main,pet],new IntPtr(1)) is not null, "main foreground shows overlay");
        Check(CodexWindowClassifier.Select([main,pet],new IntPtr(2)) is null, "pet foreground does not count as main window");
        Check(CodexWindowClassifier.Select([main,pet],new IntPtr(999)) is null, "other app foreground hides overlay");
        Check(CodexWindowClassifier.Select([main with {IsMinimized=true}],new IntPtr(1)) is null, "minimized main hides overlay");
        string Meta() => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "fast-session" } });
        string Settings(string? tier, string thread = "fast-session") => JsonSerializer.Serialize(new {
            type = "event_msg", payload = new { type = "thread_settings_applied", thread_id = thread,
                thread_settings = new { service_tier = tier } } });
        string Context(string id) => JsonSerializer.Serialize(new { type = "turn_context", payload = new { turn_id = id } });
        var fast = new TurnStatisticsReader();
        fast.ProcessLine(Meta());
        fast.ProcessLine(Settings("priority"));
        fast.ProcessLine(E("task_started", "fast"));
        fast.ProcessLine(Context("fast"));
        fast.ProcessLine(E("token_count", input:100, output:20));
        fast.ProcessLine(E("task_complete", "fast", ms:1000));
        Check(fast.Snapshot()?.FastMode == "on", "priority tier marks completed turn Fast ON");
        TokenSnapshot WithTurn(TurnStatistics? turn) => new TokenSnapshot("preview", "", 0,0,0,0,0,0,0,DateTime.UtcNow) { Turn = turn };
        Check(TurnPresentation.Create(WithTurn(fast.Snapshot())).Secondary.CompactLabel == "⚡ TPS", "Fast ON compact rate carries lightning");
        fast.ProcessLine(Settings("default"));
        fast.ProcessLine(E("task_started", "standard"));
        Check(fast.Snapshot() is { TurnId:"fast", FastMode:"on", Running:true }, "new OFF turn does not relabel previous ON result");
        fast.ProcessLine(Context("standard"));
        fast.ProcessLine(E("task_complete", "standard", ms:1000));
        Check(fast.Snapshot()?.FastMode == "off", "default tier marks Fast OFF");
        Check(TurnPresentation.Create(WithTurn(fast.Snapshot())).Secondary.CompactLabel == "TPS", "Fast OFF has no lightning");
        fast.ProcessLine(Settings("fast"));
        fast.ProcessLine(E("task_started", "mixed"));
        fast.ProcessLine(Settings("default"));
        Check(fast.Snapshot()?.FastMode == "off", "settings change preserves completed turn");
        fast.ProcessLine(Context("mixed"));
        fast.ProcessLine(E("task_complete", "mixed", ms:1000));
        Check(fast.Snapshot()?.FastMode == "mixed", "applied context change within turn marks mixed");
        Check(TurnPresentation.Create(WithTurn(fast.Snapshot())).Secondary.CompactLabel == "⚡* TPS", "mixed turn is distinct from entirely Fast ON");
        foreach (var tier in new string?[] { null, "auto", "new-tier" })
        {
            fast.ProcessLine(Settings(tier));
            var id = "unknown-" + tier;
            fast.ProcessLine(E("task_started", id));
            fast.ProcessLine(E("task_complete", id, ms:1000));
            Check(fast.Snapshot()?.FastMode == "unknown", "missing or unrecognized tier remains unknown: " + (tier ?? "null"));
        }
        fast.ProcessLine(Settings("default"));
        fast.ProcessLine(Settings("priority", "different-session"));
        fast.ProcessLine(E("task_started", "isolated-fast"));
        fast.ProcessLine(Settings("priority"));
        fast.ProcessLine(E("task_complete", "isolated-fast", ms:1000));
        Check(fast.Snapshot()?.FastMode == "off", "foreign session and future toggle cannot relabel active result");
        var stopped = new TurnStatisticsReader();
        stopped.ProcessLine(E("task_started", "done"));
        stopped.ProcessLine(E("token_count", input:1000, output:200));
        stopped.ProcessLine(E("task_complete", "done", ms:10000));
        stopped.ProcessLine(E("task_started", "stopped-empty"));
        stopped.ProcessLine(E("token_count", input:1000, output:200));
        stopped.ProcessLine(E("turn_aborted", "stopped-empty", ms:10425));
        Check(stopped.Snapshot() is { TurnId:"done", Total:1200, TokensPerSecond:20, Running:false }, "turn stopped before any response keeps the previous result on display");
        Check(stopped.Recent(5).Select(r => r.Turn.TurnId).SequenceEqual(["done"]), "empty stopped turn is left out of the recent-turn list");
        stopped.ProcessLine(E("task_started", "stopped-partial"));
        stopped.ProcessLine(E("token_count", input:1500, output:260));
        stopped.ProcessLine(E("turn_aborted", "stopped-partial", ms:3000));
        Check(stopped.Snapshot() is { TurnId:"stopped-partial", State:"중단", Output:60, Seconds:3 }, "stopped turn that did use tokens is still shown as interrupted");
        Check(stopped.Recent(5).Select(r => r.Turn.TurnId).SequenceEqual(["stopped-partial", "done"]), "it also appears in the list");
        var onlyStopped = new TurnStatisticsReader();
        onlyStopped.ProcessLine(E("task_started", "lonely"));
        onlyStopped.ProcessLine(E("turn_aborted", "lonely", ms:2000));
        Check(onlyStopped.Snapshot() is { TurnId:"lonely", State:"집계 대기" or "중단" }, "with nothing earlier to show, the stopped turn itself is reported");
        var perResponse = new TurnStatisticsReader();
        perResponse.ProcessLine(E("token_count", input:100, output:10));
        perResponse.ProcessLine(E("task_started", "multi"));
        perResponse.ProcessLine(E("token_count", input:600, output:40));
        perResponse.ProcessLine(E("token_count", input:900, output:40));
        perResponse.ProcessLine(E("token_count", input:1500, output:95));
        perResponse.ProcessLine(E("task_complete", "multi", ms:9000));
        perResponse.Snapshot();
        Check(perResponse.DisplayedResponses.Select(r => r.Output).SequenceEqual([30L, 55L]) && perResponse.Snapshot()?.Output == 85,
            "per-response token increases are kept; a record with no new output (cut-off request, compaction) is not a response");
        Check(perResponse.Recent(5)[0].Responses.Count == 2, "recent-turn list carries the same responses");
        var fresh = new TurnStatisticsReader();
        fresh.ProcessLine(Meta());
        fresh.ProcessLine(E("task_started", "first"));
        fresh.ProcessLine(Context("first"));
        fresh.ProcessLine(E("token_count", input:100, output:20));
        fresh.ProcessLine(E("task_complete", "first", ms:1000));
        Check(fresh.Snapshot()?.FastMode == "unknown", "new chat: first turn has no settings record yet");
        fresh.ProcessLine(Settings("priority", "different-session"));
        Check(fresh.Snapshot()?.FastMode == "unknown", "another chat's settings do not fill it in");
        fresh.ProcessLine(Settings("priority"));
        Check(fresh.Snapshot() is { TurnId:"first", FastMode:"on" }, "chat's first settings record is taken as the first turn's tier");
        Check(fresh.Recent(5)[0].Turn.FastMode == "on", "recent-turn list shows the same inferred tier");
        fresh.ProcessLine(E("task_started", "second"));
        fresh.ProcessLine(Context("second"));
        fresh.ProcessLine(E("task_complete", "second", ms:1000));
        fresh.ProcessLine(Settings("default"));
        fresh.ProcessLine(E("task_started", "third"));
        fresh.ProcessLine(E("task_complete", "third", ms:1000));
        var tiers = fresh.Recent(5).ToDictionary(r => r.Turn.TurnId, r => r.Turn.FastMode);
        Check(tiers["first"] == "on" && tiers["second"] == "on" && tiers["third"] == "off", "later settings never relabel earlier turns");
        var freshOff = new TurnStatisticsReader();
        freshOff.ProcessLine(Meta());
        freshOff.ProcessLine(E("task_started", "a1")); freshOff.ProcessLine(E("task_complete", "a1", ms:1000));
        freshOff.ProcessLine(E("task_started", "a2")); freshOff.ProcessLine(E("task_complete", "a2", ms:1000));
        freshOff.ProcessLine(Settings("default"));
        Check(freshOff.Recent(5).All(r => r.Turn.FastMode == "off"), "every turn before the first record gets that record's tier, here OFF");
        var freshUnknown = new TurnStatisticsReader();
        freshUnknown.ProcessLine(Meta());
        freshUnknown.ProcessLine(E("task_started", "u1")); freshUnknown.ProcessLine(E("task_complete", "u1", ms:1000));
        freshUnknown.ProcessLine(Settings("new-tier"));
        freshUnknown.ProcessLine(Settings("priority"));
        Check(freshUnknown.Snapshot()?.FastMode == "unknown", "unrecognized first record leaves the first turn unknown; a later one does not fill it");
        var fastFixture = string.Join("\n", new[] { Meta(), Settings("priority"), E("task_started", "preview-fast"),
            Context("preview-fast"), E("token_count", input:232355,output:1216), E("task_complete", "preview-fast",ms:44034), "" });
        File.WriteAllText(Path.Combine(scratch,"fast-fixture.jsonl"),fastFixture);
        var diskFast = new TurnStatisticsReader().Read(Path.Combine(scratch,"fast-fixture.jsonl"));
        Check(diskFast?.FastMode == "on", "Fast history reconstructs after overlay restart");
        bool Kept(string line) => TurnStatisticsReader.Relevant(Encoding.UTF8.GetBytes(line));
        var bulky = "{\"timestamp\":\"2026-10-05T00:00:00Z\",\"ordinal\":7,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"text\":\"" + new string('x', 3000) + "\"}}";
        Check(!Kept(bulky) && !Kept("{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{\"type\":\"item_completed\",\"item\":{\"type\":\"token_count\"}}}"),
            "bulk records the reader never uses are skipped without parsing");
        Check(Kept(E("token_count", input:1, output:1)) && Kept(E("task_complete", "k", ms:1)) && Kept(Meta()) && Kept(Context("k")) && Kept(Settings("priority")),
            "every record the reader uses is still parsed");
        Check(Kept("{ broken event") && Kept("{\"payload\":{\"type\":\"token_count\"},\"type\":\"event_msg\"}")
            && Kept("{\"type\":\"event_msg\",\"timestamp\":\"t\",\"payload\":{\"type\":\"token_count\"}}"),
            "a record shaped differently from the usual head is parsed rather than skipped");
        var longPath = Path.Combine(scratch, "long-fixture.jsonl");
        var longLog = new StringBuilder();
        longLog.Append(E("task_started", "old")).Append('\n').Append(E("token_count", input:100, output:10)).Append('\n')
            .Append(E("task_complete", "old", ms:1000)).Append('\n');
        for (var n = 0; n < 1200; n++) longLog.Append(bulky).Append('\n');
        longLog.Append(E("task_started", "new")).Append('\n').Append(E("token_count", input:300, output:50)).Append('\n')
            .Append(E("task_complete", "new", ms:2000)).Append('\n');
        File.WriteAllText(longPath, longLog.ToString());
        var slow = new TurnStatisticsReader { ReadSliceMs = 0 };
        var early = slow.Read(longPath);
        Check(early is null && !slow.CaughtUp && slow.Recent(5).Count == 0, "a log still being read through shows no turns, not the old ones met so far");
        TurnStatistics? late = null;
        for (var n = 0; n < 50 && late is null; n++) late = slow.Read(longPath);
        Check(late is { TurnId:"new", Output:40, Seconds:2 } && slow.CaughtUp && slow.Recent(5).Select(x => x.Turn.TurnId).SequenceEqual(["new", "old"]),
            "once the end is reached the list is the true most recent turns");
        Check(new TurnStatisticsReader().Read(longPath) == late, "reading in one go gives the same result as reading in slices");
        // A log of many gigabytes is read from a couple of turns before the shown ones, not from its start.
        var hugePath = Path.Combine(scratch, "huge-fixture.jsonl");
        var huge = new StringBuilder(JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "s" } })).Append('\n');
        var turnLines = new Dictionary<string, long>();
        for (var t = 0; t < 10; t++)
        {
            turnLines[$"t{t}"] = Encoding.UTF8.GetByteCount(huge.ToString());
            huge.Append(E("task_started", $"t{t}")).Append('\n');
            for (var n = 0; n < 40; n++) huge.Append(bulky).Append('\n');
            huge.Append(E("token_count", input: 1000 * (t + 1), output: 100 * (t + 1))).Append('\n').Append(E("task_complete", $"t{t}", ms: 1000 * (t + 1))).Append('\n');
        }
        File.WriteAllText(hugePath, huge.ToString());
        var whole = new TurnStatisticsReader();
        var wholeLast = whole.Read(hugePath);
        string Rows(TurnStatisticsReader reader) => string.Join(" ", reader.Recent(TurnHistory.Size).Select(x => $"{x.Turn.TurnId}:{x.Turn.Output}:{x.Turn.Seconds}"));
        var (largeBefore, searchBefore) = (TurnStatisticsReader.LargeLogBytes, TurnStatisticsReader.LargeLogSearchBytes);
        try
        {
            TurnStatisticsReader.LargeLogBytes = 1024;
            using (var stream = File.OpenRead(hugePath))
                Check(TurnStatisticsReader.RecentTurnsStart(stream, TurnHistory.Size + 2) == turnLines["t3"],
                    "large log: reading starts at the line of the seventh latest turn start");
            var tailReader = new TurnStatisticsReader();
            var tailLast = tailReader.Read(hugePath);
            Check(tailLast == wholeLast && tailLast is { TurnId: "t9", Output: 100, Seconds: 10 } && Rows(tailReader) == Rows(whole),
                "large log: the latest turn and the recent list match reading the whole log");
            TurnStatisticsReader.LargeLogSearchBytes = 3 * Encoding.UTF8.GetByteCount(bulky) * 40;
            var shortReader = new TurnStatisticsReader();
            Check(shortReader.Read(hugePath) == wholeLast, "large log with few turns in the searched stretch: the latest turn is still right");
        }
        finally { (TurnStatisticsReader.LargeLogBytes, TurnStatisticsReader.LargeLogSearchBytes) = (largeBefore, searchBefore); }
        var sessions = Path.Combine(scratch, "continued-sessions");
        if (Directory.Exists(sessions)) Directory.Delete(sessions, true);
        Directory.CreateDirectory(sessions);
        const string threadA = "01a0b8a7-0000-7000-8000-00000000000a", otherId = "01a0b9c4-0000-7000-8000-00000000000b";
        string RootMeta(string id) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, originator = "Codex Desktop", source = "vscode" } });
        string Counted(long total) => JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "token_count",
            info = new { total_token_usage = new { total_tokens = total, input_tokens = total, cached_input_tokens = 0, output_tokens = 0, reasoning_output_tokens = 0 },
                last_token_usage = new { total_tokens = total }, model_context_window = 1000 } } });
        var firstFile = Path.Combine(sessions, $"rollout-2026-09-19T16-53-03-{threadA}.jsonl");
        var secondFile = Path.Combine(sessions, $"rollout-2026-09-19T22-04-09-{threadA}_{otherId}.jsonl");
        File.WriteAllText(firstFile, RootMeta(threadA) + "\n" + Counted(111) + "\n");
        File.SetLastWriteTimeUtc(firstFile, DateTime.UtcNow.AddDays(-20));
        using (var continued = new TokenLogMonitor(sessions) { PreferredThreadId = threadA })
        {
            Check(continued.Poll() is { TotalTokens: 111, ThreadId: threadA }, "a chat with one log file reads that file");
            File.WriteAllText(secondFile, RootMeta(threadA) + "\n" + Counted(222) + "\n");
            TokenSnapshot? after = null;
            for (var n = 0; n < 40 && after?.TotalTokens != 222; n++) { Thread.Sleep(50); after = continued.Poll(); }
            Check(after is { TotalTokens: 222, ThreadId: threadA } && after.LogPath == secondFile,
                "when the chat is continued in a second file, that newer file is read and still counts as the same chat");
        }
        using (var restarted = new TokenLogMonitor(sessions) { PreferredThreadId = threadA })
            Check(restarted.Poll() is { TotalTokens: 222 }, "after a restart the newest file of the chat is chosen, not the first one");
        const string threadF = "01a0b8a7-0000-7000-8000-00000000000f";
        var frozenFile = Path.Combine(sessions, $"rollout-2026-09-19T22-30-00-{threadF}.jsonl");
        File.WriteAllText(frozenFile, RootMeta(threadF) + "\n" + Counted(500) + "\n");
        var frozenAt = File.GetLastWriteTimeUtc(frozenFile);
        using (var frozen = new TokenLogMonitor(sessions) { PreferredThreadId = threadF })
        {
            Check(frozen.Poll() is { ContextUsedTokens: 500 }, "an open chat's log is read");
            File.AppendAllText(frozenFile, Counted(940) + "\n");
            File.SetLastWriteTimeUtc(frozenFile, frozenAt);
            Check(frozen.Poll() is { ContextUsedTokens: 940 }, "a log that grew is read again although its write time stayed the same");
        }
        const string threadC = "01a0b8a7-0000-7000-8000-00000000000c";
        File.WriteAllText(Path.Combine(sessions, $"rollout-2026-09-19T23-00-00-{threadC}.jsonl"),
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = threadC, originator = "codex_cli_rs", source = "cli", cli_version = "0.1.0",
                instructions = "secret words" } }) + "\n" + Counted(333) + "\n");
        using (var described = new TokenLogMonitor(sessions))
        {
            var report = described.Describe(threadC);
            Check(report.Contains("log files 1") && report.Contains("app codex_cli_rs") && report.Contains("source cli")
                && report.Contains("used no") && report.Contains("token count found") && !report.Contains("secret"),
                "the bug report says why a chat's log is not used, without its content");
            Check(described.Describe(threadA).Contains("log files 2") && described.Describe(null).Contains("chat: none"),
                "the bug report counts every file of the chat");
        }
        const string threadW = "01a0b8a7-0000-7000-8000-00000000000d";
        File.WriteAllText(Path.Combine(sessions, $"rollout-2026-09-19T23-10-00-{threadW}.jsonl"),
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = threadW, originator = "codex_work_desktop", source = "vscode" } })
            + "\n" + Counted(444) + "\n");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeName = Path.GetFileName(home);
        var scrubbed = DiagnosticLog.Scrub($"Location: \"{home.Replace("\\", "\\\\")}\\\\AppData\" · {homeName.ToUpperInvariant()}");
        Check(!scrubbed.Contains(homeName, StringComparison.OrdinalIgnoreCase) && scrubbed.Contains("%USERPROFILE%"),
            "the report hides the PC user name also where a log wrote the path with doubled backslashes");
        using (var work = new TokenLogMonitor(sessions) { PreferredThreadId = threadW })
            Check(work.Poll() is { TotalTokens: 444, ThreadId: threadW }, "a desktop build that names itself codex_work_desktop is read too");
        var titles = new Dictionary<string,string> { ["visible"] = "Visible chat", ["background"] = "Other subscribed chat" };
        Check(ThreadTitleReader.MatchUniqueTitle(titles, "Visible chat") == "visible", "visible title wins over background subscription order");
        Check(ThreadTitleReader.MatchUniqueTitle(titles, "Unknown chat") is null, "unknown visible title never substitutes another chat");
        titles["duplicate"] = "Visible chat";
        Check(ThreadTitleReader.MatchUniqueTitle(titles, "Visible chat") is null, "duplicate titles fail closed rather than guessing");
        titles["duplicate"] = "Renamed chat";
        Check(ThreadTitleReader.MatchUniqueTitle(titles, "Renamed chat") == "duplicate", "updated title resolves independently of previous selection");
        titles["long"] = "A long chat title that the window cuts short";
        titles["long2"] = "A long chat title with a different ending";
        Check(ThreadTitleReader.MatchUniqueTitle(titles, "A long chat title that the win…") == "long"
            && ThreadTitleReader.MatchUniqueTitle(titles, "A long chat title…") is null
            && ThreadTitleReader.MatchUniqueTitle(titles, "A long chat title that the win") is null,
            "a title cut with … matches the one chat it starts; two candidates or no … do not match");
        const long M = 60_000;
        var weekNow = DateTimeOffset.FromUnixTimeMilliseconds(1_000 * M);
        long resetA = 500 * M, resetB = weekNow.ToUnixTimeMilliseconds() + 3 * 24 * 60 * M;
        var week = CodexWeeklyMath.Week([
            new(400 * M, 70, resetA),            // last week: left out
            new(600 * M, 1, resetB),             // first reading of the week, after a long pause: elsewhere
            new(605 * M, 2, resetB),             // busy: this PC +1
            new(610 * M, 3, resetB + 1_000),     // same reset reported a second off: still this week, this PC +1
            new(700 * M, 13, resetB),            // after 90 quiet minutes: elsewhere +10
            new(702 * M, 12, resetB),            // a dip is noise, not a reset
            new(704 * M, 14, resetB)], weekNow); // busy: this PC +1 over the highest so far
        Check(week is { Account: 14, ThisPc: 3 } && week.StartMs == resetB - 7L * 24 * 60 * M,
            "Codex weekly: rises right after this PC's requests are this PC's, rises across a quiet stretch are elsewhere");
        Check(CodexWeeklyMath.Week([new(600 * M, 5, weekNow.ToUnixTimeMilliseconds() - M)], weekNow) is null,
            "Codex weekly: a week whose reset has passed shows nothing until a new reading");
        Check(CodexWeeklyMath.Line(week!).Contains("· 나 3% · 다른 곳 11% · 계정 14%"), "Codex weekly line text");
        Check(CodexWeeklyMath.ParseLine("""{"timestamp":"2026-10-16T09:55:15.000Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":33.0,"window_minutes":10080,"resets_at":1792649651},"secondary":null}}}""")
                is { Percent: 33, ResetMs: 1792649651000 }
            && CodexWeeklyMath.ParseLine("""{"timestamp":"2026-10-16T09:55:15.000Z","payload":{"rate_limits":{"limit_id":"base_model_inference","primary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1}}}}""") is null,
            "Codex weekly reading parsed from token_count; other limits ignored");
        // This PC logged two readings an hour ago; the user's laptop two half an hour ago, after a quiet stretch.
        var weeklyRoot = Path.Combine(scratch, "codex-weekly-share");
        if (Directory.Exists(weeklyRoot)) Directory.Delete(weeklyRoot, true);
        var weeklyLogs = Path.Combine(weeklyRoot, "sessions");
        var weeklyShare = Path.Combine(weeklyRoot, "share");
        Directory.CreateDirectory(weeklyLogs); Directory.CreateDirectory(weeklyShare);
        var realNow = DateTimeOffset.UtcNow;
        var resetSeconds = realNow.AddDays(3).ToUnixTimeSeconds();
        string Reading(DateTimeOffset at, int percent) => JsonSerializer.Serialize(new { timestamp = at.ToString("O"), type = "event_msg",
            payload = new { type = "token_count", rate_limits = new { limit_id = "codex",
                primary = new { used_percent = (double)percent, window_minutes = 10080, resets_at = resetSeconds } } } });
        File.WriteAllText(Path.Combine(weeklyLogs, "rollout-weekly.jsonl"),
            Reading(realNow.AddMinutes(-60), 10) + "\n" + Reading(realNow.AddMinutes(-58), 11) + "\n");
        var laptopAt = realNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(weeklyShare, "LAPTOP.codex.json"), JsonSerializer.Serialize(new { device = "LAPTOP",
            samples = new[] { new[] { laptopAt, 15, resetSeconds * 1000 }, new[] { laptopAt + 2 * M, 16, resetSeconds * 1000 } } }));
        var alone = new CodexWeeklyMonitor([weeklyLogs]);
        alone.Update(realNow);
        var weeklyDrive = new DriveShare(weeklyShare, "PC1");
        var together = new CodexWeeklyMonitor([weeklyLogs], () => weeklyDrive);
        together.Update(realNow);
        Check(alone.Footer?.StartsWith("이번주 ") == true && alone.Footer.Contains("· 나 1% · 다른 곳 10% · 계정 11%")
            && together.Footer?.Contains("· PC1 1% · LAPTOP 1% · 다른 곳 14% · 계정 16%") == true
            && File.ReadAllText(Path.Combine(weeklyShare, "PC1.codex.json")).Contains("\"samples\":[["),
            "Codex weekly share: the laptop's readings join the time line and its rise is \"my other device\"; this PC's readings are written");
        File.WriteAllText(Path.Combine(weeklyShare, "BROKEN.codex.json"), JsonSerializer.Serialize(new { device = "BROKEN",
            name = "A\nVERY-LONG-NAME-" + new string('x', 80),
            samples = new[] { new[] { laptopAt + M, 900, resetSeconds * 1000 }, new[] { laptopAt + 3 * M, 50, laptopAt - M } } }));
        var guarded = new CodexWeeklyMonitor([weeklyLogs], () => weeklyDrive);
        guarded.Update(realNow);
        Check(guarded.Footer?.Contains("· PC1 1% · LAPTOP 1% · 다른 곳 14% · 계정 16%") == true && !guarded.Footer.Contains("VERY-LONG"),
            "Codex weekly share: another device's readings with an impossible percent or reset are skipped (and it is left off the line, nothing of it this week)");
        // A second device of mine (a tablet, busy 20 min ago: 16 → 18) gets its own part next to the laptop's, the last busy first.
        var twoShare = Path.Combine(scratch, "codex-weekly-two", "devices");
        Directory.CreateDirectory(twoShare);
        File.Copy(Path.Combine(weeklyShare, "LAPTOP.codex.json"), Path.Combine(twoShare, "LAPTOP.codex.json"), true);
        var tabletAt = realNow.AddMinutes(-20).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(twoShare, "TABLET.codex.json"), JsonSerializer.Serialize(new { device = "TABLET",
            samples = new[] { new[] { tabletAt, 16, resetSeconds * 1000 }, new[] { tabletAt + 2 * M, 18, resetSeconds * 1000 } } }));
        var twoDrive = new DriveShare(twoShare, "PC1");
        var two = new CodexWeeklyMonitor([weeklyLogs], () => twoDrive);
        two.Update(realNow);
        Check(two.Footer?.Contains("· PC1 1% · TABLET 2% · LAPTOP 1% · 다른 곳 14% · 계정 18%") == true,
            "Codex weekly share: each of my other devices gets its own part by name");
        Check(DriveShare.ShownName("A\nVERY-LONG-NAME-" + new string('x', 80)) is { Length: 40 } shown && !shown.Contains('\n')
            && DriveShare.IsPlainId("pc-7a01b2c3") && !DriveShare.IsPlainId(@"..\..\x") && !DriveShare.IsPlainId("a/b") && !DriveShare.IsPlainId(".."),
            "share: other devices' names are one short line; only plain ids reach a file delete");
        File.Delete(Path.Combine(weeklyShare, "BROKEN.codex.json"));
        together.Update(realNow);
        var detects = 0;
        DriveShare? found = null;
        var link = new DriveShareLink(() => { detects++; return found; }, enabled: true);
        Check(link.Current() is null && link.Current() is null && detects == 1 && link.Status.StartsWith("구글 드라이브 찾는 중"),
            "drive link: a missing Google Drive is looked for again once a minute, not on every pass");
        link.Enabled = false;
        Check(link.Current() is null && link.Status.StartsWith("꺼짐"), "drive link: switched off, nothing is shared");
        found = weeklyDrive;
        link.Enabled = true;
        Check(link.Current() == weeklyDrive && detects == 2 && link.Status == "연결됨 · 내 기기 2대 (PC1, LAPTOP)",
            "drive link: switching on looks at once; status names this device and the others seen");
        // Someone else sharing the folder: their requests' rises are theirs.
        var withPeople = CodexWeeklyMath.Week([
            new(600 * M, 10, resetB),             // this PC's request
            new(601 * M, 12, resetB),             // this PC +2
            new(610 * M, 12, resetB, Person: 1),  // 친구1's request
            new(611 * M, 15, resetB, Person: 1)], // 친구1 +3
            weekNow, people: 1);
        Check(withPeople is { Account: 15, ThisPc: 2, People: [3] }
            && CodexWeeklyMath.Line(withPeople, null, ["친구1"]).Contains("· 나 2% · @친구1 3% · 다른 곳 10% · 계정 15%"),
            "Codex weekly people: a rise right after another person's request is theirs");
        // 친구1's first reading comes after a quiet stretch (elsewhere +1), the next right after their request (친구1 +2).
        var friendAt = realNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(weeklyShare, "FRIEND.codex.json"), JsonSerializer.Serialize(new { device = "FRIEND",
            samples = new[] { new[] { friendAt, 17, resetSeconds * 1000 }, new[] { friendAt + 2 * M, 19, resetSeconds * 1000 } } }));
        weeklyDrive.SetOwner("FRIEND", "친구1");
        together.Update(realNow);
        Check(together.Footer?.Contains("· PC1 1% · LAPTOP 1% · @친구1 2% · 다른 곳 15% · 계정 19%") == true
            && weeklyDrive.Status == "연결됨 · 내 기기 2대 (PC1, LAPTOP) · 다른 사람 친구1",
            "Codex weekly share: another person's device is read apart from mine and named in the status");
        Check(together.ShareNote.Contains("| @친구1 ") && together.ShareNote.Contains(": samples "),
            "Codex weekly share: the bug report names what each shared device gave");
        File.Delete(Path.Combine(weeklyShare, "FRIEND.codex.json"));
        together.Update(realNow);
        // Asked readings (not requests) in between: a rise while no request was logged is elsewhere even when close.
        var asked = CodexWeeklyMath.Week([
            new(600 * M, 10, resetB),                 // this PC's request
            new(610 * M, 10, resetB, Local: false),   // asked: unchanged
            new(620 * M, 13, resetB, Local: false),   // asked 10 min later, no request since: elsewhere +3
            new(625 * M, 13, resetB),                 // this PC's request
            new(626 * M, 15, resetB, Local: false),   // asked a minute later: this PC +2
            new(630 * M, 15, resetB, Local: false),   // asked: unchanged
            new(640 * M, 16, resetB, Local: false)],  // asked 10 min later, quiet: elsewhere +1
            weekNow);
        Check(asked is { Account: 16, ThisPc: 2 }, "Codex weekly: readings asked in between split a quiet stretch from a busy one");
        Check(CodexUsageApi.Parse("""{"id":1,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":36,"windowDurationMins":10080,"resetsAt":1792649651},"secondary":null},"rateLimitsByLimitId":{"codex":{"limitId":"codex","primary":{"usedPercent":36,"windowDurationMins":10080,"resetsAt":1792649651}}}}}""")
                == (36, 1792649651000L, null)
            && CodexUsageApi.Parse("""{"id":1,"error":{"code":-32600,"message":"not logged in"}}""") is { Percent: null, Problem: "Codex 거절: not logged in" }
            && CodexUsageApi.IsAnswer("""{"id":1,"result":{}}""") && !CodexUsageApi.IsAnswer("""{"method":"account/updated","params":{"id":1}}""")
            && !CodexUsageApi.IsAnswer("""{"id":0,"result":{}}"""),
            "Codex usage answer: weekly percent and reset read; an error is reported; notifications are not the answer");
        var dropped = CodexWeeklyMath.Week([
            new(600 * M, 40, resetB),
            new(700 * M, 2, resetB, Local: false),   // reset for everyone, same week end reported: a new week from here
            new(701 * M, 2, resetB),
            new(702 * M, 4, resetB, Local: false)], weekNow);
        Check(dropped is { Account: 4, ThisPc: 2 } && dropped.StartMs == 700 * M,
            "Codex weekly: a clear drop without a new week end still starts a new week at that reading");
        Check(CodexUsageApi.ParseCredits("""{"id":1,"result":{"rateLimits":{},"rateLimitResetCredits":{"availableCount":2,"credits":null}}}""") == 2
            && CodexUsageApi.ParseCredits("""{"id":1,"result":{"rateLimits":{}}}""") is null,
            "Codex usage answer: banked reset credits read when Codex reports them");
        var apiStore = Path.Combine(weeklyRoot, "codex-weekly-store.json");
        var apiCalls = 0;
        var withApi = new CodexWeeklyMonitor([weeklyLogs], null, _ => { apiCalls++; return new UsageApiResult(17, "정상", ResetMs: resetSeconds * 1000); }, apiStore);
        withApi.Update(realNow);
        withApi.Update(realNow.AddSeconds(30));
        withApi.Update(realNow.AddMinutes(11));
        withApi.Dispose();
        var reloaded = new CodexWeeklyMonitor([weeklyLogs], null, _ => new UsageApiResult(null, "Codex 앱이 꺼져 있음"), apiStore);
        reloaded.Update(realNow.AddMinutes(12));
        Check(apiCalls == 2 && reloaded.ApiSamples.Count == 2 && reloaded.ApiStatus == "Codex 앱이 꺼져 있음"
            && reloaded.Footer?.Contains("계정 17%") == true,
            "Codex weekly: asked every ten minutes while this PC is quiet, kept across a restart, and counted in the week");
        var idA = DeviceIdentity.FromMachineGuid("1d4e6a2c-0000-4000-8000-000000000001");
        Check(idA == DeviceIdentity.FromMachineGuid(" 1D4E6A2C-0000-4000-8000-000000000001 ")
            && idA != DeviceIdentity.FromMachineGuid("1d4e6a2c-0000-4000-8000-000000000002")
            && System.Text.RegularExpressions.Regex.IsMatch(idA, "^pc-[0-9a-f]{8}$") && DeviceIdentity.Id.StartsWith("pc-"),
            "device ID: the same computer always gets the same short ID, another computer another, no account involved");
        var migrateDir = Path.Combine(weeklyRoot, "migrate");
        Directory.CreateDirectory(migrateDir);
        var legacyFile = Path.Combine(migrateDir, DriveShare.DeviceName() + ".codex.json");
        File.WriteAllText(legacyFile, "{}");
        File.WriteAllText(Path.Combine(migrateDir, "pc-0000beef.codex.json"), JsonSerializer.Serialize(new { device = "pc-0000beef", name = "노트북" }));
        var migrating = new DriveShare(migrateDir, "pc-1234abcd", Environment.MachineName);
        migrating.Write("codex", new { device = migrating.Device, name = migrating.Name });
        foreach (var document in migrating.ReadOthers("codex", DateTimeOffset.UtcNow)) document.Dispose();
        Check(!File.Exists(legacyFile) && File.Exists(Path.Combine(migrateDir, "pc-1234abcd.codex.json"))
            && migrating.Status == $"연결됨 · 내 기기 2대 ({Environment.MachineName}, 노트북)",
            "drive share: files are named by device ID, this computer's old name-based file goes, others show by their names");
        // Device group: devices join by themselves, can be left out, renamed, merged and taken off the list.
        var groupRoot = Path.Combine(weeklyRoot, "group");
        var groupDevices = Path.Combine(groupRoot, "base", "devices");
        Directory.CreateDirectory(groupDevices);
        File.WriteAllText(Path.Combine(groupDevices, "pc-b.codex.json"), JsonSerializer.Serialize(new { device = "pc-b", name = "노트북" }));
        File.WriteAllText(Path.Combine(groupDevices, "pc-c.codex.json"), JsonSerializer.Serialize(new { device = "pc-c", name = "옛 데스크탑" }));
        var joined = new List<string>();
        void OnJoined(string shown) => joined.Add(shown);
        DriveShare.DeviceAdded += OnJoined;
        try
        {
            var group = new DriveShare(groupDevices, "pc-a", "DESKTOP-1");
            int Others() { var docs = group.ReadOthers("codex", DateTimeOffset.UtcNow); docs.ForEach(d => d.Dispose()); return docs.Count; }
            var firstRead = Others();
            var secondRead = Others();
            Check(firstRead == 2 && secondRead == 2 && joined.Order().SequenceEqual(["노트북", "옛 데스크탑"])
                && File.Exists(Path.Combine(groupRoot, "base", "group.json")) && group.Status == "연결됨 · 내 기기 3대 (DESKTOP-1, 노트북, 옛 데스크탑)",
                "device group: devices in the share join as mine and are announced once");
            group.SetExcluded("pc-c", true);
            group.SetExcluded("pc-a", true);
            Check(Others() == 1 && group.Status == "연결됨 · 내 기기 2대 (DESKTOP-1, 노트북)",
                "device group: a device left out is not counted; this device cannot be left out");
            group.SetExcluded("pc-c", false);
            group.Rename("pc-b", "집 노트북");
            group.Merge("pc-c", "pc-b");
            var reopenedGroup = new DriveShare(groupDevices, "pc-a", "DESKTOP-1");
            foreach (var d in reopenedGroup.ReadOthers("codex", DateTimeOffset.UtcNow)) d.Dispose();
            var rows = reopenedGroup.ListDevices();
            Check(reopenedGroup.Status == "연결됨 · 내 기기 2대 (DESKTOP-1, 집 노트북)" && rows.Count == 3 && rows[0] is { Id: "pc-a", IsThis: true }
                && rows.Single(r => r.Id == "pc-c").MergedInto == "pc-b",
                "device group: names and merges are kept in the share for every device; merged devices count once");
            reopenedGroup.Remove("pc-c");
            reopenedGroup.Remove("pc-a");
            Check(!File.Exists(Path.Combine(groupDevices, "pc-c.codex.json")) && reopenedGroup.ListDevices().Select(r => r.Id).Order().SequenceEqual(["pc-a", "pc-b"]),
                "device group: taking a device off the list removes its files; this device stays");
        }
        finally { DriveShare.DeviceAdded -= OnJoined; }
        // Owners: devices of the same owner are "나" on each side; a merged device goes with the one it was merged into;
        // devices without an owner go by the default owner's name on someone else's screen.
        var peopleRoot = Path.Combine(weeklyRoot, "people");
        var peopleDevices = Path.Combine(peopleRoot, DriveShare.FolderName, "devices");
        Directory.CreateDirectory(peopleDevices);
        foreach (var (id, shownName) in new[] { ("pc-me", "PC"), ("pc-me2", "노트북"), ("pc-f", "F-PC"), ("pc-f2", "F-옛PC") })
            File.WriteAllText(Path.Combine(peopleDevices, $"{id}.codex.json"), JsonSerializer.Serialize(new { device = id, name = shownName }));
        var mineSide = new DriveShare(peopleDevices, "pc-me", "PC");
        mineSide.SetOwner("pc-f", "친구1");
        mineSide.Merge("pc-f2", "pc-f");
        string Owners(DriveShare side)
        {
            var files = side.ReadShared("codex", DateTimeOffset.UtcNow);
            files.ForEach(f => f.Document.Dispose());
            return string.Join(",", files.Select(f => f.Person ?? "나").OrderBy(p => p, StringComparer.Ordinal));
        }
        var mineOwners = Owners(mineSide);
        var friendSide = new DriveShare(peopleDevices, "pc-f", "F-PC");
        var friendOwners = Owners(friendSide);
        mineSide.SetDefaultOwner("AB");
        mineSide.SetOwner("pc-me2", "ab");
        var renamedOwners = Owners(new DriveShare(peopleDevices, "pc-f", "F-PC"));
        Check(mineOwners == "나,친구1,친구1" && mineSide.Status == "연결됨 · 내 기기 2대 (PC, 노트북) · 다른 사람 친구1"
            && friendOwners == "공유 폴더 주인,공유 폴더 주인,나" && renamedOwners == "AB,AB,나"
            && mineSide.ListDevices().Single(r => r.Id == "pc-me2") is { Owner: "AB", Mine: true }
            && mineSide.ListDevices().Single(r => r.Id == "pc-f") is { Owner: "친구1", Mine: false },
            "device owners: same owner is 나 on each side, a merged device follows its owner, the default owner is named on the other side");
        var shortcutParent = Path.Combine(weeklyRoot, "drive-letter");
        var shortcutTarget = Path.Combine(shortcutParent, ".shortcut-targets-by-id", "1AbC");
        Directory.CreateDirectory(Path.Combine(shortcutTarget, DriveShare.FolderName));
        Check(DriveShare.SharedTarget(shortcutParent) == shortcutTarget && DriveShare.SharedTarget(peopleRoot) is null
            && DriveShare.PickedRoot(Path.Combine(peopleRoot, DriveShare.FolderName)) == peopleRoot
            && DriveShare.PickedRoot(peopleRoot) == peopleRoot && DriveShare.PickedRoot(Path.Combine(weeklyRoot, "TokenOverlay-x")) == Path.Combine(weeklyRoot, "TokenOverlay-x"),
            "drive share: a TokenOverlay folder shared by someone else is found under the shortcut targets; picking the TokenOverlay folder itself works");
        var picked = new DriveShareLink(() => null, true, groupRoot);
        var pickedShare = picked.Current();
        picked.FolderOverride = Path.Combine(groupRoot, "missing");
        Check(pickedShare?.Folder == Path.Combine(groupRoot, DriveShare.FolderName, "devices") && picked.Current() is null
            && picked.Status == "지정한 공유 폴더를 찾는 중…",
            "drive link: a folder picked by hand is used instead of Google Drive; a missing one is waited for");
        var withFolder = OverlaySettings.CreateDefault();
        withFolder.DriveShareFolder = @"D:\공유\폴더";
        Check(OverlaySettings.ParseJson(withFolder.Serialize()).Settings is { DriveShareFolder: @"D:\공유\폴더", DriveShareEnabled: true },
            "settings: the picked share folder is saved");
        Check(DeviceManagerForm.Ago(TimeSpan.FromSeconds(30)) == "방금" && DeviceManagerForm.Ago(TimeSpan.FromMinutes(5)) == "5분 전"
            && DeviceManagerForm.Ago(TimeSpan.FromHours(3)) == "3시간 전" && DeviceManagerForm.Ago(TimeSpan.FromDays(2)) == "2일 전",
            "device manager: last seen in plain words");
        var groupFile = Path.Combine(mineSide.BaseFolder, "group.json");
        var groupBefore = File.GetLastWriteTimeUtc(groupFile);
        using (var manager = new DeviceManagerForm(new DriveShareLink(() => mineSide, true), _ => { }))
        {
            manager.StartPosition = FormStartPosition.Manual; manager.Location = new Point(-20000, -20000); manager.ShowInTaskbar = false;
            manager.Show();
            Application.DoEvents();
            Check(manager.Visible && File.GetLastWriteTimeUtc(groupFile) == groupBefore && mineSide.ListDevices().All(row => !row.Excluded),
                "device manager: opening it leaves the devices as they were (the list's own check boxes settling are not the user's)");
            manager.Hide();
            OverlayContext.Reveal(manager);
            Check(manager.Visible, "device manager: a window left hidden is shown again when opened from the menu");
            manager.Close();
        }
        var logDir = Path.Combine(weeklyRoot, "logs");
        var previousLogs = DiagnosticLog.UseFolder(logDir);
        try
        {
            DiagnosticLog.Change("check", "same");
            DiagnosticLog.Change("check", "same");
            DiagnosticLog.Change("check", "other");
            DiagnosticLog.Info("check", "path " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
            var logText = File.ReadAllText(Directory.GetFiles(logDir, "overlay-*.log").Single());
            Check(System.Text.RegularExpressions.Regex.Matches(logText, "check: same").Count == 1 && logText.Contains("check: other")
                && logText.Contains("%USERPROFILE%") && !logText.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                "log: a state is written once per change, the user's folder never appears");
            var report = DiagnosticLog.CreateReport("summary " + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), weeklyRoot);
            using var zip = System.IO.Compression.ZipFile.OpenRead(report);
            using var summaryReader = new StreamReader(zip.GetEntry("summary.txt")!.Open());
            Check(zip.Entries.Any(e => e.Name.StartsWith("overlay-")) && summaryReader.ReadToEnd().Contains("summary %USERPROFILE%")
                && Path.GetFileName(report).StartsWith("TokenOverlay-log-pc-"),
                "bug report: one zip with the logs and a summary, named by device ID, user folder hidden");
        }
        finally { DiagnosticLog.UseFolder(previousLogs); }
        var stateTitles = CodexThreadTitles.Read(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        Check(stateTitles is { Count: > 0 }, "Codex state database titles read through Windows SQLite, read-only");
        return new { Passed = results.Count, Checks = results };
    }
}
