using System.Drawing;

namespace TokenOverlay;

// OpenAI complimentary tokens in the Ctrl list: today's Usage API answer summed per group (only the complimentary
// tier), the models put in their group, the rows and title, and the open list drawn for review.
internal static class OpenAiFreeChecks
{
    public static object Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool passed, string name) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }

        // Shaped like this account's own answer of 2026-11-12 (gpt-5.4-mini, 7 in, 13 out), with a billed row beside it
        // and two large-group models.
        const string json = """
            {"object":"page","data":[{"object":"bucket","start_time":1794700800,"end_time":1794787200,"results":[
              {"object":"organization.usage.completions.result","input_tokens":7,"output_tokens":13,"num_model_requests":1,
               "service_tier":"incentivized-tier","model":"gpt-5.4-mini-2026-03-17","input_cached_tokens":0},
              {"input_tokens":32,"output_tokens":73,"num_model_requests":1,"service_tier":"incentivized-tier","model":"gpt-5.6-terra"},
              {"input_tokens":90000,"output_tokens":10000,"num_model_requests":3,"service_tier":"incentivized-tier","model":"gpt-6-astra"},
              {"input_tokens":20000,"output_tokens":5000,"num_model_requests":2,"service_tier":"incentivized-tier","model":"gpt-6.1-sol"},
              {"input_tokens":400000,"output_tokens":1000,"num_model_requests":1,"service_tier":"default","model":"gpt-6-astra"}]}],
             "has_more":false,"next_page":null}
            """;
        var now = DateTimeOffset.Parse("2026-11-11T15:30:00Z");
        var usage = OpenAiFreeTokenMonitor.Summarize(json, now);
        Check(usage.Small is { Tokens: 125, Requests: 2 } && usage.Large is { Tokens: 125_000, Requests: 5 },
            "today's complimentary tokens add up per group, input and output together; a billed (default tier) row is left out");
        Check(usage.Large.Models.SequenceEqual(["gpt-6-astra", "gpt-6.1-sol"]) && usage.Small.Models.SequenceEqual(["gpt-5.6-terra", "gpt-5.4-mini"]),
            "the models used today, most tokens first, without the snapshot date");
        Check(OpenAiFreeTokenMonitor.IsSmall("gpt-5.6-terra") && OpenAiFreeTokenMonitor.IsSmall("gpt-5-mini-2025-08-07")
            && OpenAiFreeTokenMonitor.IsSmall("o4-mini-2025-04-16") && !OpenAiFreeTokenMonitor.IsSmall("gpt-5-2025-08-07")
            && !OpenAiFreeTokenMonitor.IsSmall("gpt-6-luna") && !OpenAiFreeTokenMonitor.IsSmall("gpt-5.4-2026-03-05")
            && OpenAiFreeTokenMonitor.IsSmall("gpt-7-nano") && !OpenAiFreeTokenMonitor.IsSmall("gpt-7-sol"),
            "models go to the help article's group (gpt-6-luna large, gpt-5.6-luna small); an unlisted mini or nano counts small");

        Check(usage.Billed is { Tokens: 401_000, Requests: 1 } && usage.Billed.Models.SequenceEqual(["gpt-6-astra"]),
            "the billed (default tier) tokens are kept apart");
        Check(OpenAiFreeTokenMonitor.SumCost("""
            {"object":"page","data":[{"start_time":1794700800,"results":[
              {"amount":{"value":1.81,"currency":"usd"},"line_item":"gpt-6-astra, cache writes"},
              {"amount":{"value":3.03,"currency":"usd"}},{"amount":{"value":0.62,"currency":"usd"}}]}]}
            """) == 5.46m, "today's cost adds up the Costs API's line items");
        var table = OpenAiFreeTokenMonitor.Table(usage with { Cost = 5.46m }, now, failed: false);
        Check(table.Title == "OpenAI 무료 토큰 · 00:30 조회 · 09:00 초기화", "the title says when it was read (KST) and when the day turns");
        Check(table.Rows[0] == new HistoryRow("큰 묶음", "13만", "13만", "50.0%", "25만", "6 Astra · 6.1 Sol", "5")
            && table.Rows[1] == new HistoryRow("작은 묶음", "125", "250만", "99.9%", "250만", "5.6 Terra · 5.4 Mini", "2"),
            "rows: used, left, share left, the day's limit, requests and the models");
        Check(table.Rows.Count == 3 && table.Rows[2] == new HistoryRow("유료 청구", "40만", "—", "—", "$5.46", "6 Astra", "1"),
            "billed tokens get their own row with today's cost (" + table.Rows[^1] + ")");
        Check(OpenAiFreeTokenMonitor.Table(usage with { Cost = null }, now, false).Rows[2].Model == "금액 —"
            && OpenAiFreeTokenMonitor.Summarize(json.Replace("\"service_tier\":\"default\"", "\"service_tier\":\"incentivized-tier\""), now) is var free
            && OpenAiFreeTokenMonitor.Table(free, now, false).Rows.Count == 2,
            "a cost not read shows a dash; a day with nothing billed has no billed row");
        var full = OpenAiFreeTokenMonitor.Summarize(json.Replace("\"input_tokens\":90000", "\"input_tokens\":900000"), now);
        Check(OpenAiFreeTokenMonitor.Table(full, now, false).Rows[0] is { Seconds: "0", Rate: "0%" },
            "past the limit nothing is left (never below zero)");
        var failed = OpenAiFreeTokenMonitor.Table(null, now, failed: true);
        Check(failed.Title.Contains("조회 실패 00:30") && failed.Rows.All(row => row.Tokens == "—"),
            "nothing read today: the rows show dashes and the title says the read failed");
        Check(OpenAiFreeTokenMonitor.Summarize("""{"data":[{"results":[]}]}""", now) is { Large.Tokens: 0, Small.Tokens: 0 },
            "a day with no requests reads zero");

        // OpenCode Go: the answer recorded in pistonex/opencode-go-usage docs/RESEARCH.md (2026-09-13), reset times set.
        const string go = """
            { "usage": {
                "rolling": { "status": "ok", "percent": 0, "resetsAt": "2026-11-11T17:10:00Z" },
                "weekly":  { "status": "ok", "percent": 4, "resetsAt": "2026-11-12T22:00:00Z" },
                "monthly": { "status": "rate-limited", "percent": 100, "resetsAt": "2026-11-21T03:44:20.699Z" } } }
            """;
        var windows = OpenCodeGoMonitor.Parse(go);
        Check(windows is { Length: 3 } && windows[2] is { Percent: 100, Reached: true } && windows[0] is { Percent: 0, Reached: false },
            "OpenCode Go: the three windows with their share used and whether the limit is reached");
        var goTable = OpenCodeGoMonitor.Build(windows, now, null);
        Check(goTable.Title == "OpenCode Go · 00:30 조회"
            && goTable.Rows[0] == new HistoryRow("5시간", "0%", "2시간", "100%", "월의 20%", "11-12 (목) 02:10", "정상")
            && goTable.Rows[1] == new HistoryRow("주간", "4%", "31시간", "96%", "월의 50%", "11-13 (금) 07:00", "정상")
            && goTable.Rows[2] == new HistoryRow("월간", "100%", "9일", "0%", "월 한도", "11-21 (토) 12:44", "막힘"),
            "OpenCode Go as a table: used, time to the reset, left, the window's share of the month, state and the reset in KST ("
            + string.Join(" | ", goTable.Rows) + ")");
        var goFailed = OpenCodeGoMonitor.Build(null, now, "키가 맞지 않음");
        Check(goFailed.Title == "OpenCode Go · 키가 맞지 않음" && goFailed.Rows.Count == 3 && goFailed.Rows.All(row => row.Tokens == "—"),
            "OpenCode Go without numbers: the title says why and the rows show dashes");
        Check(OpenCodeGoMonitor.Parse("""{"type":"error","error":{"type":"AuthError"}}""") is null
            && OpenCodeGoMonitor.Parse("""{"usage":{}}""") is null,
            "an answer without the windows is not read as numbers");
        Check(OpenCodeGoMonitor.Until(TimeSpan.FromMinutes(39.2)) == "40분" && OpenCodeGoMonitor.Until(TimeSpan.Zero) == "곧",
            "under an hour in minutes, rounded up");

        // Anthropic Console credits: shaped like this account's prepaid/credits answer of 2026-11-12 (one Ultra grant).
        const string credits = """
            {"amount":19047,"currency":"USD","balance":{"money":null,"credits":{"amount_minor":19047,"exponent":2}},
             "amount_without_scoped_credits":0,"tranches":[],"promo_tranches":[{"remaining_amount_minor_units":19046,"currency":"USD",
             "expires_at":"2026-12-09T00:00:00Z","granted_amount_minor_units":20000,"granted_at":"2026-11-06T16:53:26.577000Z",
             "program_id":"subscription_api_credit","name":"API credit (Ultra)"},
             {"remaining_amount_minor_units":0,"expires_at":"2026-11-01T00:00:00Z","granted_amount_minor_units":20000,"name":"API credit (Ultra)"}]}
            """;
        var parsed = AnthropicCreditMonitor.Parse(credits);
        var creditLeft = AnthropicCreditMonitor.Text(parsed, now, null);
        Check(creditLeft == "$190.46", "Anthropic: the dollars left of the running grant; an expired grant is left out (" + creditLeft + ")");
        Check(AnthropicCreditMonitor.Text(AnthropicCreditMonitor.Parse(credits.Replace("\"amount_without_scoped_credits\":0", "\"amount_without_scoped_credits\":-20000")), now, null) == "-$9.54",
            "a paid balance below zero counts against the grant");
        Check(AnthropicCreditMonitor.Text(null, now, "로그인 만료") == "로그인 만료" && AnthropicCreditMonitor.Text(parsed, now, "조회 실패") == "$190.46",
            "no numbers yet: what went wrong; a failed refresh keeps the last value");
        Check(AnthropicCreditMonitor.Parse("""{"type":"error"}""") is null
            && AnthropicCreditMonitor.ApiOrganization("""[{"uuid":"11111111-1111-1111-1111-111111111111","capabilities":["claude_max","chat"]},{"uuid":"22222222-2222-2222-2222-222222222222","capabilities":["api","api_individual"]}]""") == "22222222-2222-2222-2222-222222222222",
            "an unknown answer is not numbers; the billed organization is the one with the api capability");

        // The open list, in place of the recent turns, over the weekly line (drawn for review).
        var basePresentation = TurnPresentation.Create(null);
        var turns = basePresentation with { History = [new HistoryRow("1턴 전", "1k", "1초", "1", "1")] };
        Check(OverlayContext.WithTables(turns, [], null) is { History.Count: 1, GoTables.Count: 0, HasTable: true }
            && OverlayContext.WithTables(turns, [goTable], creditLeft) is { History.Count: 0, GoTables.Count: 1 },
            "without the Go key the recent turns stay; with it Go's table takes their place");
        var goSecond = OpenCodeGoMonitor.Build(windows, now, null, "OpenCode Go 2");
        Check(goSecond.Title == "OpenCode Go 2 · 00:30 조회", "the second Go account's table is titled with its number");
        var goThird = OpenCodeGoMonitor.Build(windows, now, null, "OpenCode Go 3");
        CodexWindowInfo Host(uint dpi, int width) => new(new IntPtr(1), new IntRect(0, 0, width, 1200), new IntRect(0, 0, width, 1200), null,
            new IntRect(0, 0, width, 1200), dpi, new WindowChromeMetrics(36, 24, 4, 4, 4));
        foreach (var (name, kind, accounts) in new[] { ("openai-free-light", OverlayThemeKind.Light, 1), ("openai-free-dark", OverlayThemeKind.Dark, 1),
                     ("opencode-two-light", OverlayThemeKind.Light, 2), ("opencode-two-dark", OverlayThemeKind.Dark, 2),
                     ("opencode-three-light", OverlayThemeKind.Light, 3), ("opencode-three-dark", OverlayThemeKind.Dark, 3) })
        {
            var listed = basePresentation with { History = [],
                HistoryFooter = "이번주 17(토)~ · PC 35% · 다른 곳 0% · 계정 35%\n초기화 11-14 (토) 20:00 · 2일 18시간 남음",
                GoTables = accounts switch { 3 => [goThird, goSecond, goTable], 2 => [goSecond, goTable], _ => [goTable] },
                CreditLeft = creditLeft, App = HostApp.Claude };
            Check(accounts switch
                {
                    1 => !listed.GoTitle(0) && listed.GoTop(0) == 0 && listed is { TurnRows: 0, TableRows: 4, HistoryPanelRows: 8 },
                    2 => !listed.GoTitle(0) && listed.GoTitle(1) && listed.GoTop(1) == 5 && listed is { TableRows: 9, HistoryPanelRows: 13 },
                    _ => !listed.GoTitle(0) && listed.GoTitle(2) && listed.GoTop(1) == 5 && listed.GoTop(2) == 10
                         && listed is { TableRows: 14, HistoryPanelRows: 18 },
                },
                "Go's header and three rows under the panel's title (a second account on top, the first under its own title), then the week with Anthropic's dollars in front of the devices, its bar, time line and reset line");
            using var form = new TokenStripForm();
            _ = form.Handle;
            form.SetPresentation(listed);
            form.ApplyTheme(OverlayThemePalette.For(kind));
            form.SetShowHistory(true);
            form.ApplyLayout(OverlayLayoutCalculator.Calculate(new(Host(144, 4000), AnchorMode.InsideTopRight, true, listed.HistoryPanelRows, false,
                Presentation: listed, CompactPanel: true)));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(Path.Combine(directory, $"{name}.png"));
        }
        return new { passed = true, checks };
    }
}
