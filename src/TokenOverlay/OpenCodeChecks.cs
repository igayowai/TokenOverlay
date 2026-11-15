namespace TokenOverlay;

// OpenCode capsule: turns cut from OpenCode's message rows, their three rates and the context ring, read from a made-up
// database through Windows' SQLite the way the real one is.
internal static class OpenCodeChecks
{
    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        static bool Near(double? value, double expected) => value is double v && Math.Abs(v - expected) < 0.01;
        long seq = 0;
        OpenCodeRow Message(string type, long at, string? outcome = null) => new(++seq, type, at, null, null, null, 0, 0, 0, 0, 0, null, null, outcome);
        OpenCodeRow Request(long created, long? first, long? completed, long output, long reasoning, long input = 100,
            long read = 900, long write = 0, string model = "deepseek-v4.1-flash") =>
            new(++seq, "assistant", created, created, first, completed, input, output, reasoning, read, write, "opencode-go", model, null);

        // Turn 1, 0 → 10 s: two requests, 1 → 3 s and 6 → 8 s, each waiting 1 s for its first piece; tools between
        // them. Output 300 + 100 reasoning and 500 + 100: 1000 tokens. TPS 1000 ÷ 10 s = 100, 모델 ÷ 4 s = 250, 생성 ÷ 2 s = 500.
        var turn1 = new List<OpenCodeRow>
        {
            Message("user", 0), Request(1000, 2000, 3000, 300, 100), Request(6000, 7000, 8000, 500, 100, read: 1900),
            Message("idle", 10_000, "succeeded")
        };
        var one = OpenCodeTurns.Read(turn1);
        Check(one.Turn is { Output: 1000, Input: 3000, Cached: 2800, Running: false, State: "완료", ModelRequests: 2 }
            && Near(one.Turn.Seconds, 10) && Near(one.Turn.TokensPerSecond, 100) && Near(one.Turn.ModelTokensPerSecond, 250)
            && Near(one.Turn.PureTokensPerSecond, 500),
            "a turn: output with reasoning over the turn (TPS), over its requests (모델) and over their generating time (생성)");
        Check(one.Context == 2000 && one.Provider == "opencode-go" && one.Model == "deepseek-v4.1-flash" && one.Turn!.Model == "deepseek-v4.1-flash",
            "context is the last request's input with its cache reads and writes, and its model");

        // Turn 2 starts and runs: a request still streaming. The finished turn stays, marked running (직전).
        var running = turn1.Concat([Message("user", 20_000), Request(21_000, 22_000, null, 50, 0, read: 5000)]).ToList();
        var two = OpenCodeTurns.Read(running);
        Check(two.Turn is { Output: 1000, Running: true } && Near(two.Turn.Seconds, 10) && two.Recent.Count == 1,
            "while a turn runs the last finished one stays on screen, marked running");
        Check(two.Context == 2000, "a request still streaming does not move the context yet");

        // A first turn still running has nothing finished to show: the waiting state.
        var first = OpenCodeTurns.Read([Message("user", 0), Request(1000, 2000, null, 10, 0)]);
        Check(first.Turn is { Running: true, Seconds: null }, "a first turn still running waits for its end");

        // A sub-agent's result (synthetic) and a message sent mid-turn stay inside the same turn.
        var steered = turn1.Take(2).Concat([Message("synthetic", 4000), Message("user", 4500), Request(5000, 5500, 7000, 200, 0),
            Message("idle", 9000, "interrupted")]).ToList();
        var three = OpenCodeTurns.Read(steered);
        Check(three.Turn is { Output: 600, State: "중단", ModelRequests: 2 } && Near(three.Turn.Seconds, 9) && three.Recent.Count == 1,
            "messages inside a turn do not start another; an interrupted turn reads 중단");
        Check(OpenCodeTurns.Read(turn1.Take(3).Append(Message("idle", 10_000, "failed")).ToList()).Turn?.State == "오류",
            "a failed turn reads 오류");

        // History: finished turns, newest first.
        var later = running.Concat([Request(23_000, 23_500, 25_000, 300, 0, read: 9000, write: 30_000), Message("idle", 30_000, "succeeded")]).ToList();
        var four = OpenCodeTurns.Read(later);
        Check(four.Recent.Count == 2 && four.Recent[0].Output == 300 && four.Recent[1].Output == 1000 && four.Turn is { Running: false, Output: 300 },
            "recent turns newest first; a request that never completed adds nothing");
        Check(four.Recent[0].Restored && !four.Recent[1].Restored, "a request writing more than half its context to the cache marks the turn rebuilt");
        Check(OpenCodeTurns.Read([Request(0, 500, 1000, 10, 0)]).Turn is { Running: true }, "a request with no message before it opens a turn of its own");

        Check(OpenCodeSource.SessionTitle("긴 문서 요약 · OpenCode") == "긴 문서 요약" && OpenCodeSource.SessionTitle("OpenCode") is null
            && OpenCodeSource.SessionTitle(" · OpenCode") is null && OpenCodeSource.SessionTitle(null) is null,
            "the window title less \" · OpenCode\" is the chat's title");

        // The same rows in a made-up database, read through Windows' SQLite with json_extract, numbers only.
        var path = Path.Combine(directory, "opencode-fixture.db");
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) File.Delete(file);
        var db = OpenCodeSource.Sqlite.Open(path, write: true);
        Check(db != IntPtr.Zero, "a made-up database opens");
        try
        {
            Check(OpenCodeSource.Sqlite.Execute(db,
                "create table session_v2 (id text primary key, title text, parent_id text, time_archived integer);"
                + "create table session_message (id text primary key, session_id text, type text, seq integer, time_created integer, time_updated integer, data text);"
                + "create table kv (key text primary key, value text);"
                + "insert into session_v2 values ('ses_main', '긴 문서 요약', null, null), ('ses_child', '긴 문서 요약', 'ses_main', null),"
                + " ('ses_a', '두 개', null, null), ('ses_b', '두 개', null, null), ('ses_old', '보관됨', null, 1);"
                + "insert into kv values ('models-dev:v2:catalog', json_object('updatedAt', 1, 'body',"
                + " json_quote(json_object('opencode-go', json_object('models', json_object('deepseek-v4.1-flash', json_object('limit', json_object('context', 1000000, 'output', 384000)))))))) ;"),
                "fixture tables");
            var rowsSql = string.Join(";", turn1.Select(r => Insert(r, r.At)));
            Check(OpenCodeSource.Sqlite.Execute(db, rowsSql), "fixture rows");
        }
        finally { OpenCodeSource.Sqlite.Close(db); }

        var source = new OpenCodeSource(path);
        var read = source.Read("긴 문서 요약");
        Check(read is { Id: "ses_main", Error: null, Snapshot: { App: HostApp.OpenCode, ThreadTitle: "긴 문서 요약" } snap }
            && snap.Turn is { Output: 1000 } t && Near(t.TokensPerSecond, 100) && Near(t.ModelTokensPerSecond, 250) && Near(t.PureTokensPerSecond, 500),
            "the database read gives the same turn as the rows (Windows' SQLite has json_extract)");
        Check(read.Snapshot!.ContextUsedTokens == 2000 && read.Snapshot.ContextFill is double fill && Math.Abs(fill - 0.002) < 1e-9
            && read.Snapshot.RingNote == "Deepseek V4.1 Flash\n1M" && read.Snapshot.ContextWindowTokens == 1_000_000,
            "the ring: last context against the model's window from OpenCode's own catalogue (stored as JSON text)");
        Check(source.Read("두 개") is { Id: null, Error: "같은 제목의 OpenCode 세션이 여럿임" }
            && source.Read("없는 제목") is { Id: null, Error: "현재 화면 제목과 일치하는 OpenCode 세션 없음" }
            && source.Read("보관됨").Id is null,
            "a title shared by two chats, a missing one and an archived one are not guessed");

        // A later poll reads only what changed: turn 2's rows, then a row updated in place.
        var next = later.Skip(turn1.Count).ToArray();
        db = OpenCodeSource.Sqlite.Open(path, write: true);
        try { Check(OpenCodeSource.Sqlite.Execute(db, string.Join(";", next.Select(r => Insert(r, r.At)))), "more fixture rows"); }
        finally { OpenCodeSource.Sqlite.Close(db); }
        var again = source.Read("긴 문서 요약");
        Check(again.Snapshot?.Turn is { Output: 300, Running: false } && again.Snapshot.History?.Turns.Count == 2,
            "rows added since the last read are taken in");
        Check(TurnPresentation.Create(read.Snapshot) is { SubLine: "모델 250.00 TPS", SubLine2: "생성 500.00 TPS", RingNote: "Deepseek V4.1 Flash\n1M" } shown
            && shown.Secondary.Value == "100.00",
            "the capsule shows TPS, 모델 and 생성 for OpenCode, and the ring's note");
        File.Delete(path);

        return new { passed = true, checks = checks.Select(name => new { name, passed = true }).ToArray() };
    }

    // One fixture row: the numbers where OpenCode keeps them in a message's data, plus a text field never read.
    private static string Insert(OpenCodeRow r, long updated)
    {
        var data = r.Type switch
        {
            "assistant" => "json_object('time', json_object('created', " + r.Created
                + (r.FirstToken is null ? "" : ", 'firstToken', " + r.FirstToken) + (r.Completed is null ? "" : ", 'completed', " + r.Completed)
                + "), 'model', json_object('id', '" + r.Model + "', 'providerID', '" + r.Provider + "'), 'content', json_array(json_object('type', 'text', 'text', '본문')),"
                + " 'tokens', json_object('input', " + r.Input + ", 'output', " + r.Output + ", 'reasoning', " + r.Reasoning
                + ", 'cache', json_object('read', " + r.CacheRead + ", 'write', " + r.CacheWrite + ")))",
            "idle" => "json_object('time', json_object('created', " + r.At + "), 'outcome', '" + r.Outcome + "')",
            _ => "json_object('time', json_object('created', " + r.At + "), 'text', '본문')"
        };
        return $"insert into session_message values ('msg_{r.Seq}', 'ses_main', '{r.Type}', {r.Seq}, {r.At}, {updated}, {data})";
    }
}
