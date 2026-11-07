using System.Text.Json;

namespace TokenOverlay;

// Accounts on one PC (AccountBook): keys, periods, names, reading the apps' files, and the weeks of Claude and Codex
// kept apart by account, with what goes to the share.
internal static class AccountChecks
{
    private const string Claude = AccountBook.Claude;
    private const string Codex = AccountBook.Codex;

    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        var root = Path.Combine(directory, "accounts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string Key(string raw) => AccountBook.Key(raw);

        var key = Key("11111111-aaaa");
        Check(key == Key(" 11111111-AAAA ") && key != Key("22222222") && System.Text.RegularExpressions.Regex.IsMatch(key, "^acct-[0-9a-f]{10}$"),
            "accounts: an account is kept as a short derived key, the same for the same ID");

        // The user's account, then a sibling's from hour 2, then the user's again from hour 3.
        const long H = 3_600_000;
        const long t0 = 1_800_000_000_000;
        var path = Path.Combine(root, "accounts.json");
        var book = new AccountBook(path);
        var (me, sibling) = (Key("me-uuid"), Key("sibling-uuid"));
        var first = book.Observe(Claude, me, t0);
        var same = book.Observe(Claude, me, t0 + H);
        var switched = book.Observe(Claude, sibling, t0 + 2 * H);
        Check(first is null && same is null && switched is { From: { Mine: true, Name: "나" }, To: { Mine: false, Name: "계정 2" } }
            && book.Current(Claude)?.Key == sibling && !book.MineNow(Claude) && book.MineNow(Codex),
            "accounts: the first account is the user's own (나); a new one is another (계정 2) and its change is told once");
        var back = book.Observe(Claude, me, t0 + 3 * H);
        Check(back is { To: { Mine: true, Name: "나" } } && book.IsMine(Claude, t0 + H) && !book.IsMine(Claude, t0 + 2 * H + H / 2)
            && book.IsMine(Claude, t0 + 4 * H) && book.IsMine(Claude, t0 - H) && book.IsMine(Codex, t0 + 2 * H + H / 2),
            "accounts: a moment belongs to the account in use then; before the first reading, the first account's; the other app is apart");
        book.Rename(Claude, sibling, "  동생 ");
        var reloaded = new AccountBook(path);
        Check(reloaded.Accounts(Claude) is [{ Name: "나", Mine: true }, { Name: "동생", Mine: false }] && !reloaded.IsMine(Claude, t0 + 2 * H + H / 2)
            && reloaded.Current(Claude)?.Key == me,
            "accounts: names and when each account was in use survive a restart");
        var stored = File.ReadAllText(path);
        Check(!stored.Contains("uuid", StringComparison.OrdinalIgnoreCase) && stored.Contains(me) && stored.Contains(sibling),
            "accounts: only derived keys are stored, no account ID");
        reloaded.SetMine(Claude, sibling);
        Check(reloaded.MineOf(Claude)?.Key == sibling && !reloaded.IsMine(Claude, t0 + H) && reloaded.IsMine(Claude, t0 + 2 * H + H / 2),
            "accounts: the user can say which account is theirs; one per app");
        var old = new AccountBook(null);
        old.Observe(Codex, me, t0);
        old.Observe(Codex, sibling, t0 + H);
        old.Observe(Codex, me, t0 + 40L * 24 * H);
        Check(old.IsMine(Codex, t0 + 39L * 24 * H) == false && old.IsMine(Codex, t0 + 41L * 24 * H),
            "accounts: changes past 30 days are dropped but the one covering that time stays");

        // The apps' own files: the desktop app's account, Claude Code's (with its organization), Codex's.
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new Dictionary<string, object> { ["lastKnownAccountUuid"] = "app-uuid", ["oauth:tokenCache"] = "not read" }));
        var code = Path.Combine(root, ".claude.json");
        File.WriteAllText(code, JsonSerializer.Serialize(new { oauthAccount = new { accountUuid = "cli-uuid", organizationUuid = "org-1", emailAddress = "x@example.com" } }));
        var auth = Path.Combine(root, "auth.json");
        File.WriteAllText(auth, JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { account_id = "codex-1", access_token = "not read" } }));
        var probe = new AccountProbe(() => [config], code, auth);
        Check(probe.ClaudeInUse() == (Key("app-uuid"), true) && probe.ClaudeCode() == (Key("cli-uuid"), Key("org-1")) && probe.CodexInUse() == Key("codex-1"),
            "accounts: read from the desktop app's config, Claude Code's sign-in and Codex's auth file, as keys");
        Check(new AccountProbe(() => [Path.Combine(root, "missing.json")], code, auth).ClaudeInUse() == (Key("cli-uuid"), false)
            && new AccountProbe(() => [], Path.Combine(root, "none"), Path.Combine(root, "none")).CodexInUse() is null,
            "accounts: without the desktop app Claude Code's sign-in is the one in use; nothing signed in reads as nothing");
        File.WriteAllText(config, JsonSerializer.Serialize(new { lastKnownAccountUuid = "app-2" }));
        File.SetLastWriteTimeUtc(config, DateTime.UtcNow.AddSeconds(5));
        Check(probe.ClaudeInUse().Key == Key("app-2"), "accounts: a file that changed is read again");

        // The week's line under another account's name.
        const string plain = "이번주 5(목)~ · 나 3% · 다른 곳 1% · 계정 4%";
        Check(WeeklySplit.ParseLine(WeeklySplit.Named("동생", plain)) is { Who: "동생", Account: 4, Since: "5(목)~", Parts: [("이 PC", 3), ..] }
            && WeeklySplit.ParseLine(plain) is { Who: null } && WeeklySplit.ParseLine(WeeklySplit.Unknown("동생", "Codex")) is null,
            "accounts: another account's week leads with its name; while not known, a plain line says so");

        // Claude: the app's record has the user's organization (org-1) and the sibling's (org-2). The sibling is in
        // use from an hour ago; this PC answered at -2 h (user) and -45 min (sibling).
        var now = DateTimeOffset.Parse("2026-10-05T10:00:00Z");
        long T(double hoursAgo) => now.AddHours(-hoursAgo).ToUnixTimeMilliseconds();
        string Sample(double hoursAgo, string org, int sd) => JsonSerializer.Serialize(new { t = T(hoursAgo), org, u = new { fh = 0, sd } });
        string Answer(string id, double hoursAgo) => JsonSerializer.Serialize(new
        {
            type = "assistant", timestamp = now.AddHours(-hoursAgo).ToString("O"), requestId = "r-" + id,
            message = new { id, usage = new { input_tokens = 1, output_tokens = 100 } }
        });
        var history = Path.Combine(root, "plan-usage-history.json");
        File.WriteAllText(history, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(30, "org-1", 40), Sample(20, "org-1", 46),
            Sample(10, "org-1", 3), Sample(3, "org-2", 20), Sample(1, "org-1", 9), Sample(0.5, "org-2", 25)) + "]}");
        var projects = Path.Combine(root, "projects");
        Directory.CreateDirectory(Path.Combine(projects, "p"));
        File.WriteAllText(Path.Combine(projects, "p", "s.jsonl"), Answer("x1", 2) + "\n" + Answer("s1", 0.75) + "\n");
        var claudeBook = new AccountBook(null);
        claudeBook.Note(Claude, Key("cli-uuid"), Key("org-1"));
        claudeBook.Observe(Claude, Key("cli-uuid"), T(48));
        claudeBook.Observe(Claude, Key("sibling-uuid"), T(1));
        claudeBook.Rename(Claude, Key("sibling-uuid"), "동생");
        var shareDir = Path.Combine(root, "share");
        Directory.CreateDirectory(shareDir);
        var weekly = new WeeklyUsageMonitor(() => [history], projects, null, share: () => new DriveShare(shareDir, "PC1"), accounts: claudeBook);
        weekly.Update(now);
        var written = File.ReadAllText(Path.Combine(shareDir, "PC1.claude.json"));
        Check(weekly.LastWindows[^1] is { Account: 9, ThisPc: 6 } && weekly.Line is { } siblingLine
            && siblingLine.StartsWith("동생 · 이번주 ") && siblingLine.EndsWith("· 이 PC 5% · 다른 곳 20% · 계정 25%") && weekly.ResetLine is null,
            "accounts: Claude, while the sibling is in use the week is theirs (the app's record for their organization, this PC's answers then); the user's week keeps only theirs");
        Check(written.Contains((T(2) / 60_000).ToString()) && !written.Contains((T(0.75) / 60_000).ToString()) && !written.Contains(",25]"),
            "accounts: Claude, what goes to the share is the user's own account only");
        claudeBook.Observe(Claude, Key("cli-uuid"), T(0));
        weekly.Update(now.AddSeconds(1));
        Check(weekly.Line is { } mineLine && mineLine.StartsWith("이번주 ") && mineLine.EndsWith("· 나 6% · 다른 곳 3% · 계정 9%"),
            "accounts: Claude, back on the user's account the week is theirs again");
        var unknownBook = new AccountBook(null);
        unknownBook.Note(Claude, Key("cli-uuid"), Key("org-1"));
        unknownBook.Observe(Claude, Key("cli-uuid"), T(48));
        unknownBook.Observe(Claude, Key("new-uuid"), T(0.1));
        File.WriteAllText(history, "{\"version\":2,\"samples\":[" + string.Join(",", Sample(10, "org-1", 3), Sample(1, "org-1", 9)) + "]}");
        var fresh = new WeeklyUsageMonitor(() => [history], projects, null, accounts: unknownBook);
        fresh.Update(now);
        Check(fresh.Line == WeeklySplit.Unknown("계정 2", "Claude 앱") && fresh.Footer == fresh.Line,
            "accounts: Claude, another account with nothing in the app's record yet says so instead of the user's week");

        // Claude Code in a terminal signed in to someone else's account while the app stays the user's (2026-11-07):
        // the usage API answers for Claude Code's sign-in, so its 18% and reset time must not become the user's.
        var cliBook = new AccountBook(null);
        cliBook.NoteCode(Key("cli-uuid"), Key("org-1"));
        cliBook.Observe(Claude, Key("cli-uuid"), T(48));
        var apiCalls = 0;
        var cliWeekly = new WeeklyUsageMonitor(() => [history], projects, null,
            api: _ => { apiCalls++; return new UsageApiResult(18, "정상", ResetMs: T(-120)); }, accounts: cliBook);
        var codeSwitched = cliBook.NoteCode(Key("other-uuid"), Key("org-9"));
        cliWeekly.Update(now);
        Check(codeSwitched && cliBook.CodeOther() is { Name: "계정 2", Mine: false } && cliBook.MineNow(Claude) && apiCalls == 0
            && cliWeekly.ApiSamples.Count == 0 && cliWeekly.ResetLine is null && cliWeekly.Line is { } cliLine && cliLine.EndsWith("계정 9%")
            && cliWeekly.StaleLine == WeeklyUsageMath.CodeOtherLine("계정 2") && WeeklySplit.IsLogin(cliWeekly.StaleLine!)
            && !WeeklySplit.IsStale(cliWeekly.StaleLine!) && cliWeekly.ApiStatus == WeeklyUsageMonitor.CodeOtherStatus,
            "accounts: Claude Code signed in to another account, the usage API is not asked and the user's week is the app's record, with a line saying whose login it is");
        var codeBack = cliBook.NoteCode(Key("cli-uuid"), Key("org-1"));
        cliWeekly.Update(now.AddSeconds(1));
        Check(codeBack && cliBook.CodeOther() is null && apiCalls == 1 && cliWeekly.ApiSamples is [{ Percent: 18 }]
            && cliWeekly.ResetLine is not null && cliWeekly.StaleLine is null,
            "accounts: Claude Code back on the user's account, the usage API is asked at once and its reset time shown again");
        var orgBook = new AccountBook(null);
        orgBook.Note(Claude, Key("app-uuid"), Key("org-1"));
        orgBook.Observe(Claude, Key("app-uuid"), T(48));
        var sameOrg = orgBook.NoteCode(Key("cli-id-of-the-same-person"), Key("org-1"));
        Check(!sameOrg && orgBook.CodeIsMine() && orgBook.CodeOther() is null && !orgBook.NoteCode(null, null) && orgBook.CodeIsMine(),
            "accounts: Claude Code under another account ID but the user's organization still counts as the user's; no sign-in counts as the user's");

        // Codex: the user's readings an hour ago (10 → 11), the sibling's from 30 minutes ago (40 → 42), another reset.
        var codexLogs = Path.Combine(root, "sessions");
        Directory.CreateDirectory(codexLogs);
        var realNow = DateTimeOffset.UtcNow;
        string Reading(DateTimeOffset at, int percent, long reset) => JsonSerializer.Serialize(new { timestamp = at.ToString("O"), type = "event_msg",
            payload = new { type = "token_count", rate_limits = new { limit_id = "codex",
                primary = new { used_percent = (double)percent, window_minutes = 10080, resets_at = reset } } } });
        var (mineReset, theirReset) = (realNow.AddDays(3).ToUnixTimeSeconds(), realNow.AddDays(5).ToUnixTimeSeconds());
        File.WriteAllText(Path.Combine(codexLogs, "rollout-a.jsonl"), Reading(realNow.AddMinutes(-60), 10, mineReset) + "\n"
            + Reading(realNow.AddMinutes(-58), 11, mineReset) + "\n" + Reading(realNow.AddMinutes(-20), 40, theirReset) + "\n"
            + Reading(realNow.AddMinutes(-18), 42, theirReset) + "\n");
        var codexBook = new AccountBook(null);
        codexBook.Observe(Codex, Key("codex-me"), realNow.AddHours(-2).ToUnixTimeMilliseconds());
        codexBook.Observe(Codex, Key("codex-sibling"), realNow.AddMinutes(-30).ToUnixTimeMilliseconds());
        codexBook.Rename(Codex, Key("codex-sibling"), "동생");
        var codexShare = Path.Combine(root, "codex-share");
        Directory.CreateDirectory(codexShare);
        var codexWeekly = new CodexWeeklyMonitor([codexLogs], () => new DriveShare(codexShare, "PC1"), null, null, codexBook);
        codexWeekly.Update(realNow);
        var codexWritten = File.ReadAllText(Path.Combine(codexShare, "PC1.codex.json"));
        Check(codexWeekly.Footer is { } theirs && theirs.StartsWith("동생 · 이번주 ") && theirs.Split('\n')[0].EndsWith("계정 42%") && theirs.Contains("~ · 이 PC ")
            && WeeklySplit.ParseReset(theirs.Split('\n')[1]) is not null,
            "accounts: Codex, while the sibling is in use the week and its reset are theirs");
        Check(codexWritten.Contains(",11,") && !codexWritten.Contains(",42,") && !codexWritten.Contains(",40,"),
            "accounts: Codex, what goes to the share is the user's own account only");
        codexBook.Observe(Codex, Key("codex-me"), realNow.ToUnixTimeMilliseconds());
        codexWeekly.Update(realNow.AddSeconds(1));
        Check(codexWeekly.Footer is { } own && own.StartsWith("이번주 ") && own.Split('\n')[0].EndsWith("계정 11%"),
            "accounts: Codex, back on the user's account the week is theirs again");

        return new { Passed = checks.Count, Checks = checks };
    }
}
