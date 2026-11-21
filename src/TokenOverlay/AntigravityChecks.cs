namespace TokenOverlay;

// Antigravity: reading its steps' protobuf metadata, a conversation's turns counted like the other capsules', the
// window title to conversation lookup, and the capsule drawn for review.
internal static class AntigravityChecks
{
    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }

        // Protobuf pieces as Antigravity writes them.
        static byte[] Varint(ulong v) { var b = new List<byte>(); while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); return b.ToArray(); }
        static byte[] V(int field, long v) => [.. Varint((ulong)(field << 3)), .. Varint((ulong)v)];
        static byte[] L(int field, params byte[][] parts) { var body = parts.SelectMany(p => p).ToArray(); return [.. Varint((ulong)(field << 3 | 2)), .. Varint((ulong)body.Length), .. body]; }
        static byte[] S(int field, string text) => L(field, System.Text.Encoding.UTF8.GetBytes(text));
        static byte[] T(int field, long ms) => L(field, V(1, ms / 1000), V(2, ms % 1000 * 1_000_000));
        const long B = 1_795_000_000_000;
        static byte[] Input(long at) => T(1, B + at);
        static byte[] Request(long start, long first, long done, long output, long input, long read, long model = 1669) =>
            [.. T(1, B + start), .. V(3, 2), .. T(6, B + first), .. T(7, B + done), .. T(8, B + done),
             .. L(9, V(1, model), V(2, input), V(3, output), V(5, read), V(6, 24), S(7, "bot-x"), V(9, output / 2), V(10, output - output / 2))];
        static byte[] Tool(long start, long done) => [.. T(1, B + start), .. V(3, 2), .. L(4, S(1, "call_1"), S(2, "run_command")), .. T(8, B + done)];

        // Turn 1, 0 → 8 s: two requests, 1 → 3 s (400 tokens) and 6 → 8 s (600), a tool between them ending at 5 s;
        // each waits 1 s for its first token. TPS 1000 ÷ 8 s = 125; 모델 1000 ÷ 4 s = 250; 생성 1000 ÷ 2 s = 500.
        // Turn 2, 20 → 23 s: one request (300 tokens) 21 → 23 s, first token at 22 s. TPS 100, 모델 150, 생성 300.
        var steps = new List<AntigravityStep>
        {
            new(0, 14, Input(0)), new(1, 15, Request(1000, 2000, 3000, 400, 100, 900)), new(2, 132, Tool(3000, 5000)),
            new(3, 15, Request(6000, 7000, 8000, 600, 100, 1900)), new(4, 101, [.. T(1, B + 8000), .. V(3, 5)]),
            new(5, 14, Input(20000)), new(6, 15, Request(21000, 22000, 23000, 300, 50, 2950))
        };
        Check(AntigravityProto.Time(steps[1].Metadata, 6) == B + 2000 && AntigravityProto.Number(AntigravityProto.Field(steps[1].Metadata, 9), 3) == 400
            && AntigravityProto.Number([0xFF], 1) is null && AntigravityProto.Field([0x0A, 0x05, 0x01], 1).IsEmpty,
            "protobuf: a timestamp in milliseconds, a nested varint; cut-off bytes give nothing");
        string? Named(long e) => e == 1669 ? "gemini-flash-worker-agy" : null;
        var idle = OpenCodeTurns.Read(AntigravitySteps.Rows(steps, running: false, Named));
        Check(idle.Turn is { Output: 300, Running: false, State: "완료", Seconds: 3 } shown
            && Math.Abs(shown.TokensPerSecond!.Value - 100) < 0.01 && Math.Abs(shown.ModelTokensPerSecond!.Value - 150) < 0.01
            && Math.Abs(shown.PureTokensPerSecond!.Value - 300) < 0.01 && shown.Model == "gemini-flash-worker-agy",
            "an idle conversation: its last turn, TPS over the turn, 모델 over its requests, 생성 from their first token");
        Check(idle.Recent is [{ Output: 300 }, { Output: 1000, Seconds: 8 } earlier] && Math.Abs(earlier.TokensPerSecond!.Value - 125) < 0.01
            && Math.Abs(earlier.ModelTokensPerSecond!.Value - 250) < 0.01 && Math.Abs(earlier.PureTokensPerSecond!.Value - 500) < 0.01
            && idle.Context == 3000,
            "a turn ends at its last step (the tool's end counts), not at the next input; the context is the last request's input with its cache reads");
        var running = OpenCodeTurns.Read(AntigravitySteps.Rows(steps, running: true, Named));
        Check(running.Turn is { Output: 1000, Running: true } && running.Recent.Count == 1,
            "a conversation still working keeps its last finished turn on screen, marked running");
        var generating = steps.Take(6).Append(new AntigravityStep(6, 15, [.. T(1, B + 21000), .. V(3, 1)])).ToList();
        Check(OpenCodeTurns.Read(AntigravitySteps.Rows(generating, running: true, Named)).Turn is { Output: 1000, Running: true },
            "a request still generating (no end yet) adds nothing");
        Check(AntigravitySteps.LastModel(steps) == 1669 && AntigravitySteps.LastModel(steps.Take(1)) is null, "the last request's model enum");
        Check(AntigravityModels.Display("gemini-pro-orchestrator-g1-pro") == "Orchestrator (Argon)" && AntigravityModels.Display("gemini-pro-orchestrator-neon") == "Orchestrator (Neon)"
            && AntigravityModels.Display("gemini-pro-orchestrator-g2") == "Orchestrator" && AntigravityModels.Display("gemini-orchestrator") == "Orchestrator"
            && AntigravityModels.Display("gemini-4-argon-trial") == "Gemini 4 Argon" && AntigravityModels.Display("gemini-4-argon") == "Gemini 4 Argon"
            && AntigravityModels.Display("gemini-4-hydrogen-tiered-high") == "Gemini 4 Hydrogen" && AntigravityModels.Display("gemini-3.8-flash-low") == "Gemini 3.8 Flash"
            && AntigravityModels.Display("gemini-3.5-flash-lite") == "Gemini 3.5 Flash Lite" && AntigravityModels.Display("gemini-3-flash-a") == "Gemini 3 Flash"
            && AntigravityModels.Display("claude-opus-5-5") == "Opus 5.5" && AntigravityModels.Display("gemini-flash-worker-agy") == "Flash Worker"
            && AntigravityModels.Display(null) is null,
            "model ids as Antigravity's model menu names them");
        // An orchestrator request (1668) then worker requests (1669): all read as the picked Orchestrator (Argon), one name.
        var mixed = steps.Select(s => s.Index == 1 ? s with { Metadata = Request(1000, 2000, 3000, 400, 100, 900, model: 1668) } : s).ToList();
        var namer = AntigravityModels.Namer(mixed, e => e switch { 1668 => "gemini-pro-orchestrator-g1-pro", 1669 => "gemini-flash-worker-agy", _ => null });
        Check(namer(1669) == "Orchestrator (Argon)" && namer(1668) == "Orchestrator (Argon)" && namer(5) is null
            && OpenCodeTurns.Read(AntigravitySteps.Rows(mixed, false, namer)).Recent is [_, { Model: "Orchestrator (Argon)" }],
            "a worker's requests go under the orchestrator that sent them, so the turn has one model, no \"+\"");

        Check(AntigravitySource.TitleMatches("긴 문서 요약 작업과 그림 정리하고 마무리...", "긴 문서 요약 작업과 그림 정리하고 마무리하기")
            && AntigravitySource.TitleMatches("짧은 제목", "짧은 제목") && !AntigravitySource.TitleMatches("짧은 제목", "짧은 제목 둘")
            && !AntigravitySource.TitleMatches("짧은...", "짧은") && !AntigravitySource.TitleMatches("...", "아무거나") && !AntigravitySource.TitleMatches("x", "")
            && AntigravitySource.ConversationTitle("Antigravity") is null && AntigravitySource.ConversationTitle(" ") is null
            && AntigravitySource.ConversationTitle("짧은 제목") == "짧은 제목",
            "window title: the whole title, or its start with \"...\" when cut; the app's name alone is no conversation");
        Check(AntigravitySource.ViewedAtIn("title:\"x\" last_user_view_time:{seconds:1795093541 nanos:440000000}") == 1_795_093_541_440
            && AntigravitySource.ViewedAtIn("last_user_view_time: { seconds: 12 }") == 12_000 && AntigravitySource.ViewedAtIn("title:\"x\"") is null,
            "annotations: the time the user last viewed a conversation");
        var gen = L(1, V(3, 1669), L(4, V(1, 1669), V(3, 10)), L(9, L(10, V(1, 3000), V(4, 512_000))), S(19, "gemini-flash-worker-agy"));
        Check(AntigravitySource.ModelIn(gen) == (1669, "gemini-flash-worker-agy", 512_000)
            && AntigravitySource.ModelIn(L(1, V(3, 1417))) == (1417, null, 0) && AntigravitySource.ModelIn([]) is null,
            "a gen row's model enum, id and context budget");

        // The same conversations in made-up files, read through Windows' SQLite.
        var home = Path.Combine(Path.GetTempPath(), "TokenOverlay-antigravity-check");
        if (Directory.Exists(home)) Directory.Delete(home, true);
        Directory.CreateDirectory(Path.Combine(home, "conversations"));
        Directory.CreateDirectory(Path.Combine(home, "annotations"));
        static string Hex(byte[] b) => "X'" + Convert.ToHexString(b) + "'";
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        void Database(string path, string sql)
        {
            var db = OpenCodeSource.Sqlite.Open(path, write: true);
            try { Check(db != IntPtr.Zero && OpenCodeSource.Sqlite.Execute(db, sql), "fixture " + Path.GetFileName(path)); }
            finally { OpenCodeSource.Sqlite.Close(db); }
        }
        const string longTitle = "긴 문서 요약 작업과 그림 정리하고 마무리하기까지 한 번에";
        var window = longTitle[..25] + "...";
        string Summary(string id, string title, string status = "CASCADE_RUN_STATUS_IDLE", string source = "", string agent = "", string parent = "") =>
            $"insert into conversation_summaries values ({Q(id)}, {Q(title)}, {Q(status)}, {Q(source)}, {Q(agent)}, {Q(parent)})";
        Database(Path.Combine(home, "conversation_summaries.db"), string.Join(";",
            "create table conversation_summaries (conversation_id text primary key, title text, status text, source text, agent_name text, parent_conversation_id text)",
            Summary("conv-a", longTitle), Summary("conv-old", longTitle[..25] + " 예전 것"), Summary("conv-cli", longTitle, source: AntigravitySource.CliSource),
            Summary("conv-sub", longTitle, agent: "self"), Summary("conv-c", "짧은 제목"), Summary("conv-d", "두 개"), Summary("conv-e", "두 개")));
        File.WriteAllText(Path.Combine(home, "annotations", "conv-a.pbtxt"), "title:\"x\" last_user_view_time:{seconds:1795000100 nanos:0}");
        File.WriteAllText(Path.Combine(home, "annotations", "conv-old.pbtxt"), "title:\"y\" last_user_view_time:{seconds:1795000000 nanos:0}");
        string StepsSql(IEnumerable<AntigravityStep> rows) => string.Join(";", rows.Select(s => $"insert into steps values ({s.Index}, {s.Type}, 3, {Hex(s.Metadata)})"));
        const string tables = "create table steps (idx integer primary key, step_type integer, status integer, metadata blob);"
            + "create table gen_metadata (idx integer primary key, data blob, size integer not null default 0)";
        Database(Path.Combine(home, "conversations", "conv-a.db"), tables + ";" + StepsSql(steps)
            + $";insert into gen_metadata (idx, data) values (1, {Hex(gen)})"
            // The newest gen row carries the whole prompt; another model's id in it must not be read.
            + $";insert into gen_metadata (idx, data) values (2, {Hex([.. L(1, V(3, 1669), S(19, "wrong")), .. L(8, new byte[300_000])])})");
        // conv-c's only gen row for its model is a big one: no budget, so no ring.
        Database(Path.Combine(home, "conversations", "conv-c.db"), tables + ";"
            + StepsSql([new(0, 14, Input(0)), new(1, 15, Request(1000, 2000, 3000, 10, 10, 10, model: 1417))])
            + $";insert into gen_metadata (idx, data) values (1, {Hex([.. L(1, V(3, 1417), L(9, L(10, V(4, 256_000)))), .. L(8, new byte[300_000])])})");

        var source = new AntigravitySource(home);
        var read = source.Read(window);
        Check(read is { Id: "conv-a", Error: null, Snapshot: { App: HostApp.Antigravity, ThreadTitle: longTitle, Turn: { Output: 300, Running: false } } },
            "the window's cut title finds its conversation; CLI and sub-agent ones are left out; the one viewed last wins a shared start");
        var snap = read.Snapshot!;
        Check(snap.ContextUsedTokens == 3000 && snap.ContextWindowTokens == 512_000 && Math.Abs(snap.ContextFill!.Value - 3000 / 512_000.0) < 1e-12
            && snap.RingNote == "Flash Worker\n512K" && snap.History?.Turns.Count == 2,
            "the ring: the last request's context against Antigravity's own budget for its model, named by the small gen row");
        Check(TurnPresentation.Create(snap) is { SubLine: "모델 150.00 TPS", SubLine2: "생성 300.00 TPS" } p && p.Secondary.Value == "100.00",
            "the capsule shows TPS, 모델 and 생성 for Antigravity");
        Check(ReferenceEquals(source.Read(window).Snapshot, snap), "unchanged files are not read again");
        Check(source.Read("짧은 제목") is { Id: "conv-c", Snapshot: { RingNote: null, ContextFill: null, Turn.Output: 10 } },
            "a model whose budget is only in a big gen row: numbers without a ring");
        Check(source.Read("두 개") is { Id: null, Error: "같은 제목의 Antigravity 대화가 여럿임" }
            && source.Read("없는 제목") is { Id: null, Error: "현재 화면 제목과 일치하는 Antigravity 대화 없음" },
            "a title two conversations share, never viewed, and a missing one are not guessed");
        Database(Path.Combine(home, "conversation_summaries.db"), "update conversation_summaries set status = 'CASCADE_RUN_STATUS_RUNNING' where conversation_id = 'conv-a'");
        Check(source.Read(window).Snapshot?.Turn is { Output: 1000, Running: true }, "the summary's status turns the conversation running");
        Directory.Delete(home, true);

        Check(CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Local\Programs\Antigravity\Antigravity.exe") == HostApp.Antigravity
            && CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Local\Programs\Antigravity\resources\bin\language_server.exe") == HostApp.None
            && CodexWindowLocator.ClassifyExecutablePath(@"C:\Users\u\AppData\Local\agy\bin\agy.exe") == HostApp.None
            && CodexWindowLocator.ClassifyExecutablePath(@"C:\Temp\Antigravity.exe") == HostApp.None,
            "only the desktop install's Antigravity.exe is the Antigravity app");

        // This PC's real conversations (numbers only), by the titles Antigravity keeps.
        var real = new List<object>();
        TokenSnapshot? live = null;
        var realHome = AntigravitySource.DefaultHome;
        var summaries = Path.Combine(realHome, "conversation_summaries.db");
        if (File.Exists(summaries))
        {
            var titles = new List<(string Id, string Title)>();
            var db = OpenCodeSource.Sqlite.Open(summaries);
            try
            {
                OpenCodeSource.Sqlite.Query(db, "select conversation_id, title from conversation_summaries where title <> '' and source <> ?1 order by last_modified_time desc limit 6",
                    s => OpenCodeSource.Sqlite.Bind(s, 1, AntigravitySource.CliSource),
                    s => titles.Add((OpenCodeSource.Sqlite.Text(s, 0) ?? "", OpenCodeSource.Sqlite.Text(s, 1) ?? "")));
            }
            finally { OpenCodeSource.Sqlite.Close(db); }
            var reader = new AntigravitySource();
            foreach (var (id, title) in titles)
            {
                // As the window shows it.
                var r = reader.Read(title.Length > 28 ? title[..25] + "..." : title);
                var t = r.Snapshot?.Turn;
                real.Add(new { Conversation = id, Found = r.Id, r.Error, t?.Total, t?.Cached, t?.Output, t?.Seconds, t?.ModelSeconds, t?.PureSeconds,
                    t?.ModelRequests, t?.TokensPerSecond, t?.ModelTokensPerSecond, t?.PureTokensPerSecond, t?.State, t?.Running, t?.Model,
                    r.Snapshot?.ContextUsedTokens, r.Snapshot?.ContextWindowTokens, r.Snapshot?.ContextFill, r.Snapshot?.RingNote,
                    Recent = r.Snapshot?.History?.Turns.Count });
                live ??= r.Snapshot?.Turn is not null ? r.Snapshot : null;
            }
        }

        // Draft (user, 2026-11-19): the capsule at the newest real conversation's numbers, CPU meter on, both themes.
        CodexWindowInfo Host(uint dpi) => new(new IntPtr(1), new IntRect(0, 0, 4000, 1200), new IntRect(0, 0, 4000, 1200), null,
            new IntRect(0, 0, 4000, 1200), dpi, new WindowChromeMetrics(36, 24, 4, 4, 4));
        var drafts = new List<string>();
        if (live is not null)
            foreach (var (name, kind) in new[] { ("antigravity-dark", OverlayThemeKind.Dark), ("antigravity-light", OverlayThemeKind.Light) })
            {
                var shownDraft = TurnPresentation.Create(live with { ThreadTitle = "긴 문서 요약" }) with { CpuMeter = true };
                using var form = new TokenStripForm();
                _ = form.Handle;
                form.SetPresentation(shownDraft);
                form.ApplyTheme(OverlayThemePalette.For(kind));
                form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144), AnchorMode.InsideTopRight, false, 0, false, Presentation: shownDraft)));
                form.SetMeters(0.16, 0.65, new GpuReading(38, 52));
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                var file = Path.Combine(directory, name + ".png");
                bitmap.Save(file);
                drafts.Add(file);
            }
        return new { Passed = checks.Count, Checks = checks, Real = real, Drafts = drafts };
    }
}
