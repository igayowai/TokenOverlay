using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// One reading from the Claude app's own usage record: when, and the weekly percent of the plan used (whole account).
internal readonly record struct WeeklySample(long AtMs, int Percent);
// One model response logged on this PC: when, and its tokens (input incl. cache + output, like the capsule).
// Weight: the same tokens priced as the plan roughly counts them (input 1, cache write 1.25, cache read 0.1,
// output 5), for splitting a rise between devices used at the same time.
internal readonly record struct WeeklyActivity(long AtMs, long Tokens, double Weight = 0);
// One stretch between two weekly resets. EndMs is null for the stretch still running.
// ThisPc: this PC together with the user's other devices sharing through Google Drive. ThisPcOnly: set when other
// devices share, the part of ThisPc seen on this PC alone. People: other people's parts, in the order they were given.
// Devices: the user's other devices by name with their parts of the rest of ThisPc (WeeklySplit.DeviceParts).
internal sealed record WeeklyWindow(long StartMs, long? EndMs, int Account, int ThisPc, long Tokens, int? ThisPcOnly = null,
    IReadOnlyList<int>? People = null, IReadOnlyList<(string Name, int Percent)>? Devices = null);
// Someone else on the account, from their overlay through the share: the moments they were busy and their
// weighted use by minute ([ms, weight]).
internal sealed record WeeklyParty(IReadOnlyList<long> Seen, IReadOnlyList<(long AtMs, double Weight)> Weights);

// The weekly percent split by where it was spent, for both apps' lines:
// "PC 1% · 랩탑 98% · 다른 곳 0% · 계정 99%" (first this PC, then the user's other devices sharing through Google
// Drive, each under the name the device manager shows; 나1 / 나2 without names). mine = both together;
// thisPcOnly = this PC alone, null without other devices sharing (then just "나").
// Other people sharing the folder come next, marked for the bar: "… · @친구1 20% · 다른 곳 …".
internal static class WeeklySplit
{
    internal sealed record Names(string Self, string Others);
    internal static readonly Names Default = new("나1", "나2");
    internal const char PersonMark = '@';

    // A device name as the line can carry it: no " · " (the line's separator), not too long for the panel.
    internal static string LineName(string name)
    {
        var text = name.Replace('·', '-').Trim().TrimStart(PersonMark).Trim();
        return text.Length == 0 ? "?" : text.Length <= 16 ? text : text[..15] + "…";
    }

    // More of the user's devices than this are summed up as one "내 다른 기기", so the line stays readable.
    internal const int MostDevices = 4;

    // The user's other devices with their whole-percent parts of what is not this PC's alone (mine - own): the ones
    // busy this week or with a part, by their unrounded credit, the rounding (and a rise only the sum of devices
    // was seen for) evened out on the biggest so the parts add up. Null when there are too many to name.
    internal static IReadOnlyList<(string Name, int Percent)>? DeviceParts(int mine, int own,
        IReadOnlyList<(string Name, double Credit, bool Busy)> devices)
    {
        var rest = Math.Max(0, mine - Math.Min(own, mine));
        var parts = devices.Select(device => (device.Name, Percent: Math.Max(0, (int)Math.Round(device.Credit)), device.Busy)).ToList();
        while (parts.Count > 0 && parts.Sum(part => part.Percent) is var sum && sum != rest)
        {
            var biggest = parts.IndexOf(parts.OrderByDescending(part => part.Percent).ThenByDescending(part => part.Busy).First());
            parts[biggest] = parts[biggest] with { Percent = parts[biggest].Percent + Math.Sign(rest - sum) };
        }
        var shown = parts.Where(part => part.Busy || part.Percent > 0).Select(part => (part.Name, part.Percent)).ToArray();
        return shown.Length > MostDevices ? null : shown;
    }

    // When the week began, as the line shows it: "9(금)".
    public static string Since(long startMs, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(startMs), zone ?? TimeZoneInfo.Local);
        return $"{local.Day}({"일월화수목금토"[(int)local.DayOfWeek]})";
    }

    // The weekly line read back for the list panel's bar: who spent what share of the limit, and the account's total.
    // People: how many of the parts just before "다른 곳" are other people's.
    // Who: the account's name when it is not the user's own ("동생 · 이번주 …", see AccountBook), else null.
    internal sealed record Bar(string Since, IReadOnlyList<(string Name, int Percent)> Parts, int Account, int People = 0, string? Who = null);

    private static readonly System.Text.RegularExpressions.Regex BarLine = new(
        @"^(?:(?<who>[^·]+?) · )?이번주 (?<since>\S+~) · (?:(?<name>[^·]+?) (?<share>\d+)% · ){1,8}다른 곳 (?<other>\d+)% · 계정 (?<acc>\d+)%$");

    // The week of an account other than the user's own, under its name; this PC's part reads "이 PC", not "나", there
    // (2026-11-07 user's choice: "동생 · … 나 5%" read as the user's own).
    internal static string Named(string who, string line) => $"{LineName(who)} · {line.Replace("~ · 나 ", "~ · 이 PC ")}";

    // The week's place while another account is in use and its week is not known yet.
    internal static string Unknown(string who, string app) => $"{LineName(who)} 계정 사용 중 · 주간 사용량은 {app} 기록이 쌓이면 보임";

    public static Bar? ParseLine(string line)
    {
        var m = BarLine.Match(line);
        if (!m.Success) return null;
        static int N(string text) => int.Parse(text, CultureInfo.InvariantCulture);
        var names = m.Groups["name"].Captures.Select(c => c.Value).ToArray();
        var parts = names.Zip(m.Groups["share"].Captures, (name, share) => (name.TrimStart(PersonMark), N(share.Value)))
            .Append(("다른 곳", N(m.Groups["other"].Value))).ToArray();
        return new Bar(m.Groups["since"].Value, parts, N(m.Groups["acc"].Value), names.Count(name => name.StartsWith(PersonMark)),
            m.Groups["who"].Success ? m.Groups["who"].Value : null);
    }

    // The reset line as a graph: when it starts over, how long is left, and the share of the 7-day week gone by.
    // Extra: what Codex's line carries after the time left (" · 리셋 크레딧 2개"), kept in the text.
    internal sealed record Reset(string When, string Left, double Elapsed, string Extra = "");

    private static readonly System.Text.RegularExpressions.Regex ResetText = new(
        @"^(?<when>초기화 .+?) · (?<left>(?:(?<d>\d+)일 (?<dh>\d+)시간|(?<h>\d+)시간 (?<hm>\d+)분|(?<m>\d+)분) 남음)(?<extra> · 리셋 크레딧 \d+개)?$");

    public static Reset? ParseReset(string line)
    {
        var m = ResetText.Match(line);
        if (!m.Success) return null;
        double N(string group) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = N("d") * 1440 + (N("dh") + N("h")) * 60 + N("hm") + N("m");
        return new Reset(m.Groups["when"].Value, m.Groups["left"].Value, Math.Clamp(1 - minutes / (7 * 1440.0), 0, 1), m.Groups["extra"].Value);
    }

    // The line WeeklyUsageMath.StaleLine writes; with it the week's bar is drawn grey, so an old percent is not taken for today's.
    public static bool IsStale(string line) => line.Contains(" 이후 갱신 안 됨 · ", StringComparison.Ordinal);

    // A line ending so opens Claude Code's sign-in when clicked (the stale line or WeeklyUsageMath.LoginLine).
    internal const string LoginAction = "눌러서 다시 로그인";
    public static bool IsLogin(string line) => line.EndsWith(" · " + LoginAction, StringComparison.Ordinal);

    // devices: the user's other devices each with their part (from DeviceParts), in place of names.Others for them all.
    public static string Text(int account, int mine, int? thisPcOnly, Names? names = null, IReadOnlyList<(string Name, int Percent)>? people = null,
        IReadOnlyList<(string Name, int Percent)>? devices = null)
    {
        var (self, others) = names is null ? (Default.Self, Default.Others) : (LineName(names.Self), LineName(names.Others));
        var me = thisPcOnly is not int own ? $"나 {mine}%"
            : devices is null ? $"{self} {Math.Min(own, mine)}% · {others} {mine - Math.Min(own, mine)}%"
            : $"{self} {Math.Min(own, mine)}%" + string.Concat(devices.Select(device => $" · {LineName(device.Name)} {device.Percent}%"));
        var shared = people ?? [];
        var them = string.Concat(shared.Select(person => $" · {PersonMark}{LineName(person.Name)} {person.Percent}%"));
        return string.Create(CultureInfo.InvariantCulture,
            $"{me}{them} · 다른 곳 {Math.Max(0, account - mine - shared.Sum(person => person.Percent))}% · 계정 {account}%");
    }
}

// How much of the account's weekly percent was spent from this PC.
// The percent itself is account-wide and weighted by model, so tokens cannot be converted into it.
// Instead every rise between two readings is credited to this PC when this PC was using Claude in that
// stretch (a logged Code response, or the Claude window in front for a chat), and left to "elsewhere"
// when it was not. Another device used at the same moment as this PC is therefore counted here too:
// the figure is an upper bound for this PC, and "elsewhere" a lower bound.
// Other people sharing the folder are sides of their own: a rise while only they were busy is theirs.
internal static class WeeklyUsageMath
{
    // The app's percent trails real use a little: a rise may show up shortly after the last response.
    internal const long LagMs = 2 * 60 * 1000;

    // Index of the first entry later than the given time.
    private static int After(long[] list, long at)
    {
        var index = Array.BinarySearch(list, at);
        if (index < 0) return ~index;
        while (index < list.Length && list[index] <= at) index++;
        return index;
    }

    // One side's busy moments and weighted use, to tell who spent a rise.
    private sealed class Side
    {
        private readonly long[] _seen;
        private readonly long[] _times;
        private readonly double[] _sums;

        public Side(IEnumerable<long> seen, IEnumerable<(long AtMs, double Weight)> weights)
        {
            _seen = seen.Order().ToArray();
            var sorted = weights.Where(w => w.Weight > 0).OrderBy(w => w.AtMs).ToArray();
            _times = sorted.Select(w => w.AtMs).ToArray();
            _sums = new double[sorted.Length + 1];
            for (var i = 0; i < sorted.Length; i++) _sums[i + 1] = _sums[i] + sorted[i].Weight;
        }

        public bool Active(long from, long to) => After(_seen, to) > After(_seen, from - LagMs);
        public double Weight(long from, long to) => _sums[After(_times, to)] - _sums[After(_times, from - LagMs)];
    }

    // How a rise between two readings is shared out among the sides busy in that stretch (this PC's side first):
    // by weight when every one of them made weighted responses, otherwise all of it to this PC's side when that was
    // busy (as before there were people), otherwise evenly. Null when nobody was busy.
    private static double[]? Shares(Side[] sides, long from, long to)
    {
        var active = Enumerable.Range(0, sides.Length).Where(k => sides[k].Active(from, to)).ToArray();
        if (active.Length == 0) return null;
        var shares = new double[sides.Length];
        var weights = active.Select(k => sides[k].Weight(from, to)).ToArray();
        if (weights.All(w => w > 0))
            for (var i = 0; i < active.Length; i++) shares[active[i]] = weights[i] / weights.Sum();
        else if (active[0] == 0) shares[0] = 1;
        else foreach (var k in active) shares[k] = 1.0 / active.Length;
        return shares;
    }

    // Oldest first; the last one is the running week. A falling percent is a reset.
    // presence: moments the Claude window was in front on this PC (any tab), in ms.
    // others: the user's other devices' weighted use by minute ([ms, weight]). With it, a rise in a stretch where this PC
    // and another device both made responses is shared by weight instead of going to this PC whole.
    // people: other people's sides; each window's People has their parts in the same order.
    public static IReadOnlyList<WeeklyWindow> Windows(IReadOnlyList<WeeklySample> samples, IEnumerable<WeeklyActivity> activity,
        IEnumerable<long>? presence = null, IEnumerable<(long AtMs, double Weight)>? others = null, IReadOnlyList<WeeklyParty>? people = null)
    {
        var result = new List<WeeklyWindow>();
        if (samples.Count == 0) return result;
        var sorted = activity.Where(a => a.Tokens > 0).OrderBy(a => a.AtMs).ToArray();
        var times = sorted.Select(a => a.AtMs).ToArray();
        var sums = new long[sorted.Length + 1];
        for (var i = 0; i < sorted.Length; i++) sums[i + 1] = sums[i] + sorted[i].Tokens;
        long Tokens(long from, long? to) => sums[to is long end ? After(times, end) : sorted.Length] - sums[After(times, from)];
        var sides = new List<Side> { new(times.Concat(presence ?? []), sorted.Select(a => (a.AtMs, a.Weight))) };
        if (others is not null)
        {
            var weighted = others.Where(o => o.Weight > 0).ToArray();
            sides.Add(new(weighted.Select(o => o.AtMs), weighted));
        }
        var firstPerson = sides.Count;
        foreach (var person in people ?? []) sides.Add(new(person.Seen, person.Weights));
        var all = sides.ToArray();
        var credit = new double[all.Length];
        IReadOnlyList<int>? People() => people is null or [] ? null : credit[firstPerson..].Select(c => (int)Math.Round(c)).ToArray();
        void Credit(double amount, long from, long to)
        {
            if (amount > 0 && Shares(all, from, to) is { } shares)
                for (var k = 0; k < all.Length; k++) credit[k] += amount * shares[k];
        }

        // The first reading's percent was spent before the record began: nobody can tell where.
        var start = samples[0].AtMs;
        for (var i = 1; i < samples.Count; i++)
        {
            var before = samples[i - 1];
            var now = samples[i];
            if (now.Percent < before.Percent)
            {
                result.Add(new WeeklyWindow(start, now.AtMs, before.Percent, (int)Math.Round(credit[0]), Tokens(start, now.AtMs), People: People()));
                start = now.AtMs;
                Array.Clear(credit);
                // Whatever the new week already shows was spent between the reset and this reading.
                Credit(now.Percent, before.AtMs, now.AtMs);
            }
            else Credit(now.Percent - before.Percent, before.AtMs, now.AtMs);
        }
        result.Add(new WeeklyWindow(start, null, samples[^1].Percent, (int)Math.Round(credit[0]), Tokens(start, null), People: People()));
        return result;
    }

    // The app's 15-minute record and the overlay's own one-minute readings, as one time line.
    // A small dip between two close readings is rounding noise between the sources, not a reset.
    public static IReadOnlyList<WeeklySample> Merge(IEnumerable<WeeklySample> first, IEnumerable<WeeklySample> second)
    {
        var merged = new List<WeeklySample>();
        foreach (var sample in first.Concat(second).OrderBy(s => s.AtMs))
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                if (sample.AtMs == last.AtMs) continue;
                if (sample.Percent < last.Percent && last.Percent - sample.Percent <= 2 && sample.AtMs - last.AtMs < 10 * 60_000) continue;
            }
            merged.Add(sample);
        }
        return merged;
    }

    // Keeps the first and the last reading of every unchanged run: all that attribution needs.
    public static void AddCompressed(List<WeeklySample> list, WeeklySample sample)
    {
        if (list.Count >= 2 && list[^1].Percent == sample.Percent && list[^2].Percent == sample.Percent) list[^1] = sample;
        else list.Add(sample);
    }

    // "elsewhere" = the account's percent this PC was not seen spending (other devices, or before the record began).
    // This PC / the user's other devices by name (or just "나" without them) / other people / "다른 곳", as WeeklySplit words it.
    public static string Line(WeeklyWindow week, WeeklySplit.Names? names = null, IReadOnlyList<string>? people = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"이번주 {WeeklySplit.Since(week.StartMs)}~ · {WeeklySplit.Text(week.Account, week.ThisPc, week.ThisPcOnly, names, Parts(people, week.People), week.Devices)}");

    // Names with their parts, for the line; none without both.
    internal static IReadOnlyList<(string Name, int Percent)>? Parts(IReadOnlyList<string>? names, IReadOnlyList<int>? parts) =>
        names is null || parts is null ? null : names.Zip(parts).ToArray();

    // {"samples":[{"t":<ms>,"org":"…","u":{"sd":<weekly %>,"fh":<5-hour %>}}]} → the latest account's weekly readings.
    // "초기화 10-17 (토) 20:00 · 4일 3시간 남음". The limit starts over at the same weekday and time every week, so a
    // reported time that has already passed is moved on by whole weeks and marked as an estimate.
    public static string ResetLine(long resetMs, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        const long week = 7L * 24 * 60 * 60 * 1000;
        var nowMs = now.ToUnixTimeMilliseconds();
        var estimated = resetMs <= nowMs;
        if (estimated) resetMs += ((nowMs - resetMs) / week + 1) * week;
        // The service reports 19:59:59.5 for what it shows as 20:00.
        var minute = (resetMs + 30_000) / 60_000 * 60_000;
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(minute), zone ?? TimeZoneInfo.Local);
        var left = TimeSpan.FromMilliseconds(minute - nowMs);
        var remaining = left.TotalDays >= 1 ? $"{(int)left.TotalDays}일 {left.Hours}시간"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}시간 {left.Minutes}분" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분";
        var day = "일월화수목금토"[(int)local.DayOfWeek];
        return $"초기화 {local:MM-dd} ({day}) {local:HH:mm}{(estimated ? " (추정)" : "")} · {remaining} 남음";
    }

    // Under the week while the usage API keeps failing: the percent above is the last one read, at lastMs.
    // "11-05 02:12 이후 갱신 안 됨 · 눌러서 다시 로그인" when the login is gone or ran out, else "· 조회 실패".
    public static string StaleLine(long lastMs, string status, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(lastMs), zone ?? TimeZoneInfo.Local);
        var login = status.StartsWith("로그인", StringComparison.Ordinal);
        return $"{local:MM-dd HH:mm} 이후 갱신 안 됨 · {(login ? WeeklySplit.LoginAction : "조회 실패")}";
    }

    // Under the week in the login's last days, so it can be renewed before the percent stops:
    // "로그인 2일 5시간 후 만료 · 눌러서 다시 로그인", or "로그인 만료됨 · …" once the time has passed.
    // Under the user's week while Claude Code in a terminal is signed in to another account (AccountBook.CodeOther): the
    // usage API is not asked then, as it would answer for that account; a click signs Claude Code in again.
    public static string CodeOtherLine(string name) => $"Claude Code 로그인: {WeeklySplit.LineName(name)} · {WeeklySplit.LoginAction}";

    public static string LoginLine(long endMs, DateTimeOffset now)
    {
        var left = TimeSpan.FromMilliseconds(endMs - now.ToUnixTimeMilliseconds());
        var when = left <= TimeSpan.Zero ? "만료됨"
            : left.TotalDays >= 1 ? $"{(int)left.TotalDays}일 {left.Hours}시간 후 만료"
            : left.TotalHours >= 1 ? $"{(int)left.TotalHours}시간 {left.Minutes}분 후 만료" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}분 후 만료";
        return $"로그인 {when} · {WeeklySplit.LoginAction}";
    }

    public static IReadOnlyList<WeeklySample> ParseHistory(string json)
    {
        var list = ParseHistoryByOrg(json);
        if (list.Count == 0) return [];
        var account = list[^1].Org;
        return list.Where(s => s.Org == account).Select(s => new WeeklySample(s.At, s.Percent)).ToArray();
    }

    // Every reading with the derived key of the organization it was read for (AccountBook), oldest first.
    public static IReadOnlyList<(long At, string? Org, int Percent)> ParseHistoryByOrg(string json)
    {
        var list = new List<(long At, string? Org, int Percent)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return [];
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object
                || !sample.TryGetProperty("t", out var t) || !t.TryGetInt64(out var at)
                || !sample.TryGetProperty("u", out var u) || u.ValueKind != JsonValueKind.Object
                || !u.TryGetProperty("sd", out var sd) || sd.ValueKind != JsonValueKind.Number) continue;
            var org = sample.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String && o.GetString() is { Length: > 0 } raw
                ? AccountProbe.OrgKey(raw) : null;
            list.Add((at, org, (int)Math.Round(sd.GetDouble())));
        }
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    // One session-log line → the response it records, or null. Key: one response is logged once per content block.
    public static (string Key, WeeklyActivity Activity)? ParseLogLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "assistant"
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                || !message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return null;
            long Number(string key) => usage.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt64(out var n) ? n : 0;
            var tokens = Number("input_tokens") + Number("cache_creation_input_tokens") + Number("cache_read_input_tokens")
                + Number("output_tokens");
            var weight = Number("input_tokens") + 1.25 * Number("cache_creation_input_tokens") + 0.1 * Number("cache_read_input_tokens")
                + 5.0 * Number("output_tokens");
            var request = root.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : "";
            return (id.GetString() + "|" + request, new WeeklyActivity(at.ToUnixTimeMilliseconds(), tokens, weight));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

internal readonly record struct UsageApiResult(int? WeeklyPercent, string Status, bool RetryLater = false, long? ResetMs = null, int? ResetCredits = null);

// The account's weekly percent straight from Anthropic, once a minute, with the access token Claude Code keeps
// after a terminal login. Read-only: the token is read into memory for the one request, sent only to
// api.anthropic.com (no redirects), never stored, logged or refreshed. Refreshing is left to Claude Code,
// because rotating the refresh token here could log Claude Code out.
internal static class ClaudeUsageApi
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    internal const string NoLogin = "로그인 정보 없음 · 트레이 메뉴 → 설정 → 'Claude 로그인…'으로 한 번 로그인";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10) };

    internal static string CredentialsPath =>
        Path.Combine(Path.GetDirectoryName(ClaudeSessionIndex.ProjectsRoot())!, ".credentials.json");

    public static UsageApiResult Fetch(DateTimeOffset now)
    {
        string json;
        try
        {
            if (!File.Exists(CredentialsPath)) return new(null, NoLogin);
            json = File.ReadAllText(CredentialsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(null, "로그인 정보 읽기 대기"); }
        var (token, problem) = ReadToken(json, now);
        if (token is null) return new(null, problem!, problem != NoLogin);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "TokenOverlay");
            using var response = Client.Send(request, HttpCompletionOption.ResponseContentRead);
            if (!response.IsSuccessStatusCode) return new(null, $"사용량 조회 거절 (HTTP {(int)response.StatusCode})", true);
            using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
            var body = reader.ReadToEnd();
            return ParseUsage(body) is int percent
                ? new(percent, "정상", false, ParseReset(body)) : new(null, "사용량 응답 형식이 달라짐", true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return new(null, "사용량 조회 실패 · 네트워크");
        }
    }

    // {"claudeAiOauth":{"accessToken":"…","expiresAt":<ms>}} → the token while it is still valid.
    internal static (string? Token, string? Problem) ReadToken(string json, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("accessToken", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
                return (null, NoLogin);
            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var at)
                && at <= now.ToUnixTimeMilliseconds() + 30_000)
                return (null, "로그인 토큰 만료 · 터미널 Claude Code를 한 번 실행하면 재개");
            return (token.GetString(), null);
        }
        catch (JsonException) { return (null, "로그인 정보 형식 오류"); }
    }

    // {"seven_day":{"resets_at":"2026-10-17T10:59:59.5+00:00",…}} → when the weekly limit starts over, in Unix ms.
    internal static long? ParseReset(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            static long? At(JsonElement holder) => holder.TryGetProperty("resets_at", out var at)
                && at.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                ? when.ToUnixTimeMilliseconds() : null;
            if (root.TryGetProperty("seven_day", out var week) && week.ValueKind == JsonValueKind.Object && At(week) is long weekly)
                return weekly;
            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                foreach (var limit in limits.EnumerateArray())
                    if (limit.ValueKind == JsonValueKind.Object && limit.TryGetProperty("kind", out var kind)
                        && kind.ValueKind == JsonValueKind.String && kind.GetString() == "weekly_all" && At(limit) is long listed)
                        return listed;
            return null;
        }
        catch (JsonException) { return null; }
    }

    // {"seven_day":{"utilization":22.0,…},"limits":[{"kind":"weekly_all","percent":22},…]} → 22
    internal static int? ParseUsage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("seven_day", out var week) && week.ValueKind == JsonValueKind.Object
                && week.TryGetProperty("utilization", out var used) && used.ValueKind == JsonValueKind.Number)
                return (int)Math.Round(used.GetDouble());
            if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
                foreach (var limit in limits.EnumerateArray())
                    if (limit.ValueKind == JsonValueKind.Object && limit.TryGetProperty("kind", out var kind)
                        && kind.ValueKind == JsonValueKind.String && kind.GetString() == "weekly_all"
                        && limit.TryGetProperty("percent", out var percent) && percent.ValueKind == JsonValueKind.Number)
                        return (int)Math.Round(percent.GetDouble());
            return null;
        }
        catch (JsonException) { return null; }
    }
}

internal readonly record struct TokenRenewal(bool Attempted, bool Renewed, string? Note);

// The access token lives about eight hours and only Claude Code may renew it (it owns the refresh token).
// When the token is about to run out, the overlay wakes the bundled Claude Code, hidden, and lets it do the
// renewing: first with a command that costs nothing, then, if the file did not change, with one tiny Haiku
// request (about 5k tokens). The overlay itself never reads or sends the refresh token.
internal static class ClaudeTokenRenewal
{
    internal static readonly TimeSpan Margin = TimeSpan.FromMinutes(10);
    internal static readonly string[] StatusCommand = ["auth", "status"];
    internal static readonly string[] TinyRequest = ["-p", "Reply with OK.", "--model", "haiku", "--tools", "",
        "--system-prompt", "Reply with OK.", "--strict-mcp-config", "--disable-slash-commands",
        "--no-session-persistence", "--output-format", "json"];

    public static TokenRenewal Renew(DateTimeOffset now) => Renew(now, ReadExpiry, Run);

    internal static TokenRenewal Renew(DateTimeOffset now, Func<long?> readExpiry, Func<string[], int, bool> run)
    {
        if (readExpiry() is not long before) return new(false, false, null);
        if (before - now.ToUnixTimeMilliseconds() > (long)Margin.TotalMilliseconds) return new(false, false, null);
        var at = now.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        run(StatusCommand, 20_000);
        if (readExpiry() is long afterStatus && afterStatus > before) return new(true, true, $"{at} 토큰 갱신됨 (auth status)");
        run(TinyRequest, 90_000);
        if (readExpiry() is long afterRequest && afterRequest > before) return new(true, true, $"{at} 토큰 갱신됨 (짧은 요청)");
        return new(true, false, $"{at} 토큰 갱신 실패 · 터미널 Claude Code를 한 번 실행하면 재개");
    }

    internal static long? ParseExpiry(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) && oauth.ValueKind == JsonValueKind.Object
                && oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var at) ? at : null;
        }
        catch (JsonException) { return null; }
    }

    internal static long? ReadExpiry()
    {
        try { return File.Exists(ClaudeUsageApi.CredentialsPath) ? ParseExpiry(File.ReadAllText(ClaudeUsageApi.CredentialsPath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // The newest Claude Code the desktop app ships: <data root>\claude-code\<version>\<build>\claude.exe
    internal static string? FindCli()
    {
        try
        {
            return ClaudeSessionIndex.DataRoots().Select(root => Path.Combine(root, "claude-code")).Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "claude.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // True while Claude Code holds a login on this PC: a refresh token is stored (11-05: it emptied both tokens when the
    // login ran out but kept the file, so the file alone says nothing).
    public static bool HasLogin => ReadLoginEnd() is not null;

    // {"claudeAiOauth":{"refreshToken":"…","refreshTokenExpiresAt":<ms>}} → when the login itself runs out (renewing the
    // token does not move it, about four weeks after signing in); 0 when a login is stored without that time; null without one.
    internal static long? ParseLoginEnd(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object
                || !oauth.TryGetProperty("refreshToken", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
                return null;
            return oauth.TryGetProperty("refreshTokenExpiresAt", out var ends) && ends.TryGetInt64(out var at) ? at : 0;
        }
        catch (JsonException) { return null; }
    }

    internal static long? ReadLoginEnd()
    {
        try { return File.Exists(ClaudeUsageApi.CredentialsPath) ? ParseLoginEnd(File.ReadAllText(ClaudeUsageApi.CredentialsPath)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool CanLogin => FindCli() is not null;

    internal static string LoginArguments(string cli) =>
        $"/c \"echo 브라우저가 열리면 로그인하고 [승인]을 누르면 끝납니다. 이 창은 저절로 닫힙니다. & echo. & \"{cli}\" auth login --claudeai & timeout /t 5\"";

    // Starts the bundled Claude Code's own sign-in: it opens the browser, the user approves there, and it stores the
    // login itself. The console stays visible because the sign-in may ask for a code to be pasted.
    public static bool OpenLoginWindow()
    {
        try
        {
            if (FindCli() is not { } cli) return false;
            var folder = Path.Combine(Path.GetDirectoryName(WeeklyUsageMonitor.DefaultStorePath)!, "renew");
            Directory.CreateDirectory(folder);
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", LoginArguments(cli))
                { UseShellExecute = true, WorkingDirectory = folder });
            return process is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool Run(string[] arguments, int timeoutMs)
    {
        try
        {
            if (FindCli() is not { } cli) return false;
            // An empty folder of the overlay's own: no project files or project settings get involved.
            var folder = Path.Combine(Path.GetDirectoryName(WeeklyUsageMonitor.DefaultStorePath)!, "renew");
            Directory.CreateDirectory(folder);
            var start = new System.Diagnostics.ProcessStartInfo(cli)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return false;
            process.StandardInput.Close();
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

// Reads the Claude app's usage record and this PC's session logs (both read-only) off the UI thread,
// at most once a minute, notes when the Claude window is in front, and keeps the finished weeks and
// the recent presence minutes in a small file of its own.
internal sealed class WeeklyUsageMonitor : IDisposable
{
    internal static string DefaultStorePath => Path.Combine(AppPaths.DataFolder, "weekly-usage.json");
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Stale = TimeSpan.FromDays(7);
    private static readonly TimeSpan Kept = TimeSpan.FromDays(9);
    private static readonly TimeSpan ResponsesKept = TimeSpan.FromDays(14);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    // The usage API is asked every two minutes while this PC is using Claude, rarely while it is not,
    // and left alone for a while after a refusal. Every signed-in device asks on its own (once a minute drew refusals
    // with two devices on one account).
    private static readonly TimeSpan ApiActiveInterval = TimeSpan.FromSeconds(110);
    private static readonly TimeSpan ApiIdleInterval = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ApiBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RenewRetry = TimeSpan.FromMinutes(30);
    // How long the usage API may fail before the week says its percent is an old one.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    // How long before the login runs out the week starts saying so.
    private static readonly TimeSpan LoginWarning = TimeSpan.FromDays(3);
    private const long Minute = 60_000;
    private static readonly byte[] UsageMark = Encoding.UTF8.GetBytes("\"usage\"");

    private readonly Func<IEnumerable<string>> _historyFiles;
    private readonly string _projects;
    private readonly string? _store;
    private readonly Func<DateTimeOffset, UsageApiResult>? _api;
    private readonly Func<DateTimeOffset, TokenRenewal>? _renew;
    private readonly Func<long?>? _loginEnd;
    private readonly Func<DriveShare?>? _shareSource;
    private DriveShare? _sharedTo;
    // What was written last (minutes, last minute, weight, readings, last reading), so an unchanged record is not written again.
    private (int, long, long, int, long) _sharedKey;
    // The other devices' readings from the last pass, so the logs are read back far enough for them.
    private IReadOnlyList<WeeklySample> _sharedReadings = [];
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeeklyActivity> _responses = new(StringComparer.Ordinal);
    private readonly object _presenceLock = new();
    private readonly SortedSet<long> _presence = new();
    private readonly List<WeeklySample> _apiSamples = new();
    private IReadOnlyList<WeeklyWindow> _storedWeeks = [];
    private bool _loaded;
    private bool _dirty;
    private DateTimeOffset _saved;
    private DateTimeOffset _apiNext;
    private DateTimeOffset _renewNext;
    private volatile string? _renewNote;
    private bool _wasActive;
    private long _latestResponseMs;
    private int _busy;
    private volatile bool _apiEnabled = true;
    private volatile bool _userSet;
    private volatile string _apiStatus = "대기";
    private volatile string? _line;
    // Last weekly reset time the usage API reported (Unix ms, 0 = never). Kept so the line survives an API outage.
    private long _resetMs;
    private volatile string? _resetLine;
    private volatile string? _staleLine;
    private System.Threading.Timer? _timer;

    public WeeklyUsageMonitor(Func<DriveShare?>? shareSource = null, AccountBook? accounts = null) : this(
        () => ClaudeSessionIndex.DataRoots().Select(root => Path.Combine(root, "plan-usage-history.json")),
        ClaudeSessionIndex.ProjectsRoot(), DefaultStorePath, ClaudeUsageApi.Fetch, ClaudeTokenRenewal.Renew, shareSource,
        ClaudeTokenRenewal.ReadLoginEnd, accounts) { }

    // accounts: which account the app was on when (AccountBook); the user's week leaves out what was spent on another,
    // and while another is in use the line is that account's. Null: one account, as before.
    internal WeeklyUsageMonitor(Func<IEnumerable<string>> historyFiles, string projects, string? store,
        Func<DateTimeOffset, UsageApiResult>? api = null, Func<DateTimeOffset, TokenRenewal>? renew = null, Func<DriveShare?>? share = null,
        Func<long?>? loginEnd = null, AccountBook? accounts = null)
    {
        _historyFiles = historyFiles; _projects = projects; _store = store; _api = api; _renew = renew; _shareSource = share; _loginEnd = loginEnd;
        _accounts = accounts;
    }

    private readonly AccountBook? _accounts;
    private bool Mine(long ms) => _accounts?.IsMine(AccountBook.Claude, ms) ?? true;
    // The usage record's readings with their organization, from the last pass.
    private IReadOnlyList<(long At, string? Org, int Percent)> _history = [];

    // The list's bottom line, or null while nothing reliable is known.
    public string? Line => _line;
    // Second line under the list: when the weekly limit starts over. Null until the usage API has reported it once.
    public string? ResetLine => _line is null ? null : _resetLine;
    // Line under those while the usage API keeps failing, saying since when the percent stands still, or in the login's
    // last days, when it runs out; both open the sign-in when clicked. Null otherwise.
    public string? StaleLine => _line is null ? null : _staleLine;
    // The lines as the list footer shows them.
    public string? Footer => _line is { } line ? string.Join("\n", new[] { line, _resetLine, _staleLine }.OfType<string>()) : null;
    internal IReadOnlyList<WeeklyWindow> LastWindows { get; private set; } = [];
    internal IReadOnlyList<WeeklySample> ApiSamples => _apiSamples;
    // For bug reports: what the last pass wrote to the share and read from each other device there.
    internal string ShareNote { get; private set; } = "not read yet";

    // One-minute readings through the login token. Off: only the app's own 15-minute record is used.
    public bool ApiEnabled => _apiEnabled;
    public string ApiStatus => !_apiEnabled ? "꺼짐 · 앱의 15분 기록만 사용" : _renewNote is { } note ? _apiStatus + " · " + note : _apiStatus;
    public void SetApiEnabled(bool value)
    {
        _apiEnabled = value; _userSet = true;
        lock (_presenceLock) _dirty = true;
        _apiNext = default;
    }

    // Runs on its own so readings continue while the Claude window is behind other apps.
    public void Start() => _timer ??= new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), Tick);

    public void Dispose()
    {
        _timer?.Dispose(); _timer = null;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { if (_loaded) Save(DateTimeOffset.UtcNow, force: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // The Claude window is in front on this PC right now (any tab): remembered to the minute.
    public void NotePresence(DateTimeOffset now)
    {
        lock (_presenceLock)
        {
            if (_presence.Add(now.ToUnixTimeMilliseconds() / Minute * Minute)) _dirty = true;
        }
    }

    public void Refresh()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { Update(DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { DiagnosticLog.Change("claude-weekly-read", e.GetType().Name + ": " + e.Message); }
        // A timer thread must not end the overlay: anything else is logged for a bug report.
        catch (Exception e) { DiagnosticLog.Error("claude-weekly", e); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    internal void Update(DateTimeOffset now)
    {
        UpdateMine(now);
        ShowOther(now);
    }

    // The user's own week: only the minutes, responses and readings of their own account.
    private void UpdateMine(DateTimeOffset now)
    {
        Load();
        long[] presence;
        lock (_presenceLock)
        {
            var oldest = (now - Kept).ToUnixTimeMilliseconds();
            _presence.RemoveWhere(t => t < oldest);
            _allPresence = _presence.ToArray();
        }
        presence = _allPresence.Where(Mine).ToArray();
        _apiSamples.RemoveAll(s => s.AtMs < (now - Kept).ToUnixTimeMilliseconds());
        // A week that ended over a week ago is already written down: its responses are no longer needed.
        var expired = (now - ResponsesKept).ToUnixTimeMilliseconds();
        foreach (var key in _responses.Where(pair => pair.Value.AtMs < expired).Select(pair => pair.Key).ToArray()) _responses.Remove(key);
        var fileSamples = ReadSamples();

        // Logs are only needed from the oldest week that is still running or not yet written down.
        var known = WeeklyUsageMath.Merge(WeeklyUsageMath.Merge(fileSamples, _apiSamples), _sharedReadings);
        if (known.Count > 0)
        {
            var needed = WeeklyUsageMath.Windows(known, [])
                .Where(w => w.EndMs is null || !_storedWeeks.Any(s => s.StartMs == w.StartMs)).Min(w => w.StartMs);
            Scan(DateTimeOffset.FromUnixTimeMilliseconds(needed - WeeklyUsageMath.LagMs).UtcDateTime);
        }
        else Scan((now - ActiveWithin).UtcDateTime);

        var codeOther = _accounts?.CodeOther();
        AskApi(now, presence, codeOther is not null);
        var ownSamples = WeeklyUsageMath.Merge(fileSamples, _apiSamples);
        var shared = Share(now, presence, ownSamples);
        _sharedReadings = shared.Readings;
        // The other devices' readings fill in what this one did not see: before it joined, or while it was off.
        var samples = WeeklyUsageMath.Merge(ownSamples, shared.Readings);
        if (samples.Count == 0 || now.ToUnixTimeMilliseconds() - samples[^1].AtMs > (long)Stale.TotalMilliseconds)
        {
            LastWindows = []; _line = null; _resetLine = null; _staleLine = null;
            DiagnosticLog.Change("claude-weekly-line", $"none · {samples.Count} readings, last {DriveShare.When(samples.LastOrDefault().AtMs)}");
            Save(now, force: false);
            return;
        }
        var (seen, othersShare) = (shared.Seen, shared.Others);
        var mineResponses = MineResponses();
        var computed = WeeklyUsageMath.Windows(samples, mineResponses, seen, people: shared.People);
        // A finished week is written once, while its logs still exist, and never recomputed afterwards.
        var finished = computed.Where(w => w.EndMs is not null && !_storedWeeks.Any(s => s.StartMs == w.StartMs)).ToArray();
        if (finished.Length > 0) _storedWeeks = _storedWeeks.Concat(finished).OrderBy(w => w.StartMs).ToArray();
        Save(now, force: finished.Length > 0);
        var windows = computed.Select(w => _storedWeeks.FirstOrDefault(s => s.StartMs == w.StartMs) ?? w).ToArray();
        // With other devices sharing, the running week is worked out once more with each of them a side of its own
        // (after other people), a rise while two were busy shared by weight: this PC's part alone and each device's.
        var devices = shared.Devices;
        if (othersShare && WeeklyUsageMath.Windows(samples, mineResponses, presence.Concat(shared.SelfSeen), null,
                [.. shared.People ?? [], .. devices.Select(device => device.Side)]) is [.., var alone])
        {
            var running = windows[^1];
            var own = Math.Min(alone.ThisPc, running.ThisPc);
            var credits = alone.People?.Skip(shared.People?.Count ?? 0).ToArray() ?? [];
            var parts = WeeklySplit.DeviceParts(running.ThisPc, own, devices.Select((device, i) =>
                (device.Name, i < credits.Length ? (double)credits[i] : 0, device.Side.Seen.Any(at => at >= running.StartMs))).ToArray());
            windows[^1] = running with { ThisPcOnly = own, Devices = parts };
        }
        LastWindows = windows;
        _line = WeeklyUsageMath.Line(LastWindows[^1], othersShare ? _sharedTo?.SplitNames() : null, shared.PeopleNames);
        _resetLine = _resetMs > 0 ? WeeklyUsageMath.ResetLine(_resetMs, now) : null;
        // Asked and refused (no login, expired, network…) for a while, with no newer reading from the app or another device.
        var failing = _api is not null && _apiEnabled && _apiStatus is not ("정상" or "대기");
        _staleLine = failing && now.ToUnixTimeMilliseconds() - samples[^1].AtMs > (long)StaleAfter.TotalMilliseconds
            ? WeeklyUsageMath.StaleLine(samples[^1].AtMs, _apiStatus) : null;
        // Otherwise, in the login's last three days, the same place says when it runs out.
        if (_staleLine is null && _api is not null && _apiEnabled && _loginEnd?.Invoke() is long loginEnd and > 0
            && loginEnd - now.ToUnixTimeMilliseconds() <= (long)LoginWarning.TotalMilliseconds)
            _staleLine = WeeklyUsageMath.LoginLine(loginEnd, now);
        // Claude Code's login is someone else's: neither its failures nor its expiry are the user's.
        if (codeOther is not null) _staleLine = WeeklyUsageMath.CodeOtherLine(codeOther.Name);
        DiagnosticLog.Change("claude-weekly-line", $"{_line ?? "none"} · week from {DriveShare.When(windows[^1].StartMs)}"
            + (_staleLine is { } stale ? " · " + stale : ""));
    }

    // What the share gave: the moments any of my devices was busy (this PC's presence included), whether another of
    // them was read, their weights, every device's readings of the account's percent, and other people by name.
    // Devices: each of the user's other devices as a side of its own, by shown name, the last busy first; SelfSeen:
    // busy moments of a device merged into this one.
    private sealed record SharedUse(IEnumerable<long> Seen, bool Others, List<(long AtMs, double Weight)> Weights,
        List<WeeklySample> Readings, IReadOnlyList<string>? PeopleNames, IReadOnlyList<WeeklyParty>? People,
        IReadOnlyList<(string Name, WeeklyParty Side)> Devices, IReadOnlyList<long> SelfSeen);

    // Writes the minutes this PC was using Claude (a logged response or the window in front) for the user's other
    // devices, and adds theirs: a rise while any of them was busy is then "me". Without a share: this PC only.
    // Others: at least one other device's record was read.
    // Weights: this PC's weighted use by minute is written too, and the others' read back, so a rise while two devices
    // were both busy is shared by weight.
    // Readings: this PC's readings of the account's weekly percent go along, so a device that joins mid-week (or was
    // off) knows how the percent rose before it was there, instead of leaving all of that to "다른 곳".
    // Other people's devices are read the same way but kept apart, one side per person.
    private SharedUse Share(DateTimeOffset now, long[] presence, IReadOnlyList<WeeklySample> readings)
    {
        var share = _shareSource?.Invoke();
        if (share != _sharedTo) { _sharedTo = share; _sharedKey = default; }
        var weights = new List<(long AtMs, double Weight)>();
        if (share is null) { ShareNote = "no share"; return new(presence, false, weights, [], null, null, [], []); }
        var oldest = (now - Kept).ToUnixTimeMilliseconds();
        var nowMs = now.ToUnixTimeMilliseconds();
        // Only the user's own account goes to the share: another account's use on this PC stays here.
        var used = MineResponses().Where(r => r.Tokens > 0 && r.AtMs >= oldest).ToArray();
        var own = presence.Select(ms => ms / Minute).Concat(used.Select(r => r.AtMs / Minute)).Distinct().Order().ToArray();
        var ownWeights = used.GroupBy(r => r.AtMs / Minute).OrderBy(g => g.Key)
            .Select(g => new[] { g.Key, (long)Math.Round(g.Sum(r => r.Weight)) }).Where(row => row[1] > 0).ToArray();
        var ownReadings = new List<WeeklySample>();
        foreach (var reading in readings.Where(r => r.AtMs >= oldest)) WeeklyUsageMath.AddCompressed(ownReadings, reading);
        var key = (own.Length, own.LastOrDefault(), ownWeights.Sum(row => row[1]), ownReadings.Count, ownReadings.LastOrDefault().AtMs);
        if ((own.Length > 0 || ownReadings.Count > 0) && key != _sharedKey)
        {
            share.Write("claude", new { device = share.Device, name = share.Name, minutes = Runs(own), weights = ownWeights,
                readings = ownReadings.Select(r => new[] { r.AtMs, r.Percent }) });
            _sharedKey = key;
        }
        var seen = new List<long>(presence);
        var others = false;
        var shared = new List<WeeklySample>();
        var people = new SortedDictionary<string, (List<long> Seen, List<(long AtMs, double Weight)> Weights)>(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, (List<long> Seen, List<(long AtMs, double Weight)> Weights)>(StringComparer.OrdinalIgnoreCase);
        var selfSeen = new List<long>();
        var note = new System.Text.StringBuilder($"wrote minutes {own.Length} · readings {ownReadings.Count} (last {DriveShare.When(ownReadings.LastOrDefault().AtMs)})");
        foreach (var file in share.ReadShared("claude", now))
        {
            using (file.Document)
            {
                var (busy, spent) = file.Person is { } person
                    ? people.TryGetValue(person, out var side) ? side : people[person] = ([], [])
                    : (seen, weights);
                others |= file.Person is null;
                // One of the user's devices is also kept apart by name; one merged into this PC goes with this PC.
                List<long>? apart = null;
                List<(long AtMs, double Weight)>? apartSpent = null;
                if (file.Person is null && file.Device is { } device)
                {
                    if (!devices.TryGetValue(device, out var mine)) devices[device] = mine = ([], []);
                    (apart, apartSpent) = mine;
                }
                else if (file.Person is null) apart = selfSeen;
                var (minutes, weighed, read, lastRead) = (0L, 0, 0, 0L);
                foreach (var run in DriveShare.Rows(file.Document, "minutes"))
                    if (run.Length == 2 && run[1] >= run[0] && run[1] - run[0] < 20_000 && run[1] * Minute >= oldest)
                        for (var m = run[0]; m <= run[1]; m++) { busy.Add(m * Minute); apart?.Add(m * Minute); minutes++; }
                // [minute, weight]; an older overlay writes none, and its rises stay with this PC as before.
                foreach (var row in DriveShare.Rows(file.Document, "weights"))
                    if (row.Length == 2 && row[0] * Minute >= oldest && row[0] * Minute <= nowMs + 60 * Minute && row[1] > 0)
                    { spent.Add((row[0] * Minute, row[1])); apartSpent?.Add((row[0] * Minute, row[1])); weighed++; }
                // [ms, weekly percent]; an older overlay writes none.
                foreach (var row in DriveShare.Rows(file.Document, "readings"))
                    if (row.Length == 2 && row[0] >= oldest && row[0] <= nowMs + 60 * Minute && row[1] is >= 0 and <= 100)
                    { shared.Add(new(row[0], (int)row[1])); read++; lastRead = Math.Max(lastRead, row[0]); }
                note.Append($" | {(file.Person is { } who ? "@" + who + " " : "")}{DriveShare.Label(file.Document)}: minutes {minutes}"
                    + $" · weights {weighed} · readings {read} (last {DriveShare.When(lastRead)})");
            }
        }
        ShareNote = note.ToString();
        return new(seen, others, weights, WeeklyUsageMath.Merge(shared, []).ToList(),
            people.Count == 0 ? null : people.Keys.ToArray(),
            people.Count == 0 ? null : people.Values.Select(side => new WeeklyParty(side.Seen, side.Weights)).ToArray(),
            devices.OrderByDescending(pair => pair.Value.Seen.DefaultIfEmpty().Max())
                .Select(pair => (pair.Key, new WeeklyParty(pair.Value.Seen, pair.Value.Weights))).ToArray(), selfSeen);
    }

    // Sorted minutes → [first, last] runs.
    private static long[][] Runs(IEnumerable<long> minutes)
    {
        var runs = new List<long[]>();
        foreach (var minute in minutes)
        {
            if (runs.Count > 0 && runs[^1][1] + 1 == minute) runs[^1][1] = minute;
            else if (runs.Count == 0 || runs[^1][1] != minute) runs.Add([minute, minute]);
        }
        return runs.ToArray();
    }

    internal const string CodeOtherStatus = "Claude Code가 다른 계정 · 앱의 15분 기록만 사용";
    private bool _codeWasOther;

    private void AskApi(DateTimeOffset now, long[] presence, bool codeOther = false)
    {
        if (_api is null || !_apiEnabled) return;
        // The API answers for Claude Code's sign-in: while that is another person's, its percent and reset time are
        // theirs, so it is not asked (nor its token renewed); back on the user's, it is asked at once.
        if (codeOther)
        {
            _codeWasOther = true;
            _apiStatus = CodeOtherStatus;
            return;
        }
        if (_codeWasOther)
        {
            _codeWasOther = false;
            _apiStatus = "대기";
            _apiNext = default;
        }
        // Let Claude Code renew a token that is about to run out, before asking with it.
        if (_renew is not null && now >= _renewNext)
        {
            var renewal = _renew(now);
            if (renewal.Attempted)
            {
                _renewNote = renewal.Note;
                _renewNext = now + RenewRetry;
                if (renewal.Renewed) _apiNext = default;
                lock (_presenceLock) _dirty = true;
            }
        }
        var nowMs = now.ToUnixTimeMilliseconds();
        var since = nowMs - (long)ActiveWithin.TotalMilliseconds;
        var active = _latestResponseMs >= since || (presence.Length > 0 && presence[^1] >= since);
        // Coming back from a pause: read at once, so what was spent elsewhere meanwhile is not mixed with this PC's.
        var resumed = active && !_wasActive;
        _wasActive = active;
        if (now < _apiNext && !(resumed && _apiStatus == "정상")) return;
        var result = _api(now);
        _apiStatus = result.Status;
        if (result.ResetMs is long reset && reset != _resetMs)
        {
            _resetMs = reset;
            lock (_presenceLock) _dirty = true;
        }
        if (result.WeeklyPercent is int percent)
        {
            WeeklyUsageMath.AddCompressed(_apiSamples, new WeeklySample(nowMs, percent));
            lock (_presenceLock) _dirty = true;
            _apiNext = now + (active ? ApiActiveInterval : ApiIdleInterval);
        }
        else _apiNext = now + (result.RetryLater ? ApiBackoff : ApiActiveInterval);
    }

    private WeeklyActivity[] MineResponses() => _responses.Values.Where(r => Mine(r.AtMs)).ToArray();

    // The app's usage record, the user's own organization's readings: with accounts known, those of the user's
    // account's organization (learnt from Claude Code's sign-in, or from the record while the user's account is in
    // use); else, as before, the latest organization's.
    private IReadOnlyList<WeeklySample> ReadSamples()
    {
        var file = _historyFiles().Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file is null) { _history = []; return []; }
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            _history = WeeklyUsageMath.ParseHistoryByOrg(reader.ReadToEnd());
        if (_history.Count == 0) return [];
        var mine = _accounts?.MineOf(AccountBook.Claude);
        if (mine is { Org: null } && _history[^1].Org is { } seen && _accounts!.MineNow(AccountBook.Claude)
            && _accounts.At(AccountBook.Claude, _history[^1].At)?.Mine == true)
        {
            _accounts.Note(AccountBook.Claude, mine.Key, seen);
            mine = mine with { Org = seen };
        }
        var org = mine?.Org ?? _history[^1].Org;
        return _history.Where(s => s.Org == org || s.Org is null && mine?.Org is null).Select(s => new WeeklySample(s.At, s.Percent)).ToArray();
    }

    // While another account is in use the line is that account's week: the app's readings for organizations not the
    // user's (the latest one's), and this PC's minutes and responses while that account was in use. Its percent comes
    // only from the app's own record, as the usage API answers for Claude Code's sign-in.
    private void ShowOther(DateTimeOffset now)
    {
        if (_accounts is not { } book || book.MineNow(AccountBook.Claude) || book.Current(AccountBook.Claude) is not { } other) return;
        bool Theirs(long ms) => book.At(AccountBook.Claude, ms)?.Key == other.Key;
        var mineOrg = book.MineOf(AccountBook.Claude)?.Org;
        (long At, string? Org, int Percent)[] foreign = mineOrg is null ? [] : _history.Where(s => s.Org is not null && s.Org != mineOrg).ToArray();
        var org = foreign.LastOrDefault().Org;
        var readings = foreign.Where(s => s.Org == org).Select(s => new WeeklySample(s.At, s.Percent)).ToArray();
        _resetLine = null; _staleLine = null;
        if (readings.Length == 0 || now.ToUnixTimeMilliseconds() - readings[^1].AtMs > (long)Stale.TotalMilliseconds)
        {
            _line = WeeklySplit.Unknown(other.Name, "Claude 앱");
            DiagnosticLog.Change("claude-weekly-line", "other account · no readings");
            return;
        }
        var windows = WeeklyUsageMath.Windows(readings, _responses.Values.Where(r => Theirs(r.AtMs)), _allPresence.Where(Theirs));
        _line = WeeklySplit.Named(other.Name, WeeklyUsageMath.Line(windows[^1], null, null));
        DiagnosticLog.Change("claude-weekly-line", "other account · week from " + DriveShare.When(windows[^1].StartMs));
    }

    private long[] _allPresence = [];

    private void Scan(DateTime modifiedSinceUtc)
    {
        if (!Directory.Exists(_projects)) return;
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_projects, "*.jsonl", SearchOption.AllDirectories))
        {
            present.Add(file);
            try
            {
                if (File.GetLastWriteTimeUtc(file) < modifiedSinceUtc) continue;
                ScanFile(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // Claude deletes old session logs; their read positions go with them.
        foreach (var path in _offsets.Keys.Where(path => !present.Contains(path)).ToArray()) _offsets.Remove(path);
    }

    // Continues where the last pass stopped and never consumes a line that is still being written.
    private void ScanFile(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var offset = _offsets.GetValueOrDefault(path);
        if (file.Length < offset) offset = 0;
        if (file.Length == offset) return;
        file.Position = offset;
        // Sized to what was added (most passes see a few lines), up to 1 MB; a longer line grows it.
        var buffer = new byte[(int)Math.Clamp(file.Length - offset, 4096, 1 << 20)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            var read = file.Read(buffer, filled, buffer.Length - filled);
            if (read == 0) break;
            filled += read;
            var lineStart = 0;
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', lineStart, filled - lineStart);
                if (newline < 0) break;
                var line = buffer.AsSpan(lineStart, newline - lineStart);
                if (line.IndexOf(UsageMark) >= 0 && WeeklyUsageMath.ParseLogLine(Encoding.UTF8.GetString(line)) is { } found)
                {
                    _responses[found.Key] = found.Activity;
                    if (found.Activity.Tokens > 0) _latestResponseMs = Math.Max(_latestResponseMs, found.Activity.AtMs);
                }
                lineStart = newline + 1;
            }
            offset += lineStart;
            Buffer.BlockCopy(buffer, lineStart, buffer, 0, filled - lineStart);
            filled -= lineStart;
        }
        _offsets[path] = offset;
    }

    private sealed record StoredWeek(string Start, string End, int AccountPercent, int ThisPcPercent, long Tokens);
    // Presence: minutes since the Unix epoch, as [first, last] runs. ApiSamples: [ms, weekly percent].
    private sealed record StoreFile(int Version, StoredWeek[] Weeks, long[][]? Presence, long[][]? ApiSamples, bool? UsageApi, string? RenewNote = null, long? WeeklyReset = null);
    private static readonly JsonSerializerOptions StoreJson = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (_store is null || !File.Exists(_store)) return;
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_store), StoreJson);
            if (file is null) return;
            _storedWeeks = (file.Weeks ?? []).Select(w => new WeeklyWindow(
                DateTimeOffset.Parse(w.Start, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
                DateTimeOffset.Parse(w.End, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
                w.AccountPercent, w.ThisPcPercent, w.Tokens)).ToArray();
            lock (_presenceLock)
            {
                foreach (var run in file.Presence ?? [])
                    if (run.Length == 2 && run[1] >= run[0] && run[1] - run[0] < 20_000)
                        for (var m = run[0]; m <= run[1]; m++) _presence.Add(m * Minute);
            }
            _apiSamples.InsertRange(0, (file.ApiSamples ?? []).Where(s => s.Length == 2)
                .Select(s => new WeeklySample(s[0], (int)s[1])).OrderBy(s => s.AtMs));
            // A switch flipped before the first load wins over the stored one.
            if (file.UsageApi is bool enabled && !_userSet) _apiEnabled = enabled;
            _renewNote ??= file.RenewNote;
            if (_resetMs == 0 && file.WeeklyReset is long storedReset) _resetMs = storedReset;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
    }

    private void Save(DateTimeOffset now, bool force)
    {
        if (_store is null) return;
        long[] presence;
        lock (_presenceLock)
        {
            if (!force && !(_dirty && now - _saved >= SaveInterval)) return;
            presence = _presence.ToArray();
        }
        string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O", CultureInfo.InvariantCulture);
        var runs = new List<long[]>();
        foreach (var minute in presence.Select(ms => ms / Minute))
        {
            if (runs.Count > 0 && runs[^1][1] + 1 == minute) runs[^1][1] = minute;
            else runs.Add([minute, minute]);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
        var temp = _store + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile(3,
            _storedWeeks.Select(w => new StoredWeek(Iso(w.StartMs), Iso(w.EndMs!.Value), w.Account, w.ThisPc, w.Tokens)).ToArray(),
            runs.ToArray(), _apiSamples.Select(s => new long[] { s.AtMs, s.Percent }).ToArray(), _apiEnabled, _renewNote, _resetMs > 0 ? _resetMs : null), StoreJson));
        File.Move(temp, _store, overwrite: true);
        lock (_presenceLock) { _dirty = false; _saved = now; }
    }
}
