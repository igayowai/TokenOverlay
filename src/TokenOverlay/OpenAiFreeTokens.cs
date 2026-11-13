using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TokenOverlay;

// What is left today of OpenAI's complimentary daily tokens (data sharing), for the Ctrl list in place of the recent
// turns (user, 2026-11-12: "최근턴 토큰 표시를 빼고 거기다가 넣자"). The organization's Usage API (admin key) gives
// today's tokens per service tier and model; the complimentary ones carry the tier "incentivized-tier" (the dashboard
// calls it "data sharing incentive tier"; seen in this account's own record on 2026-11-12 and in the old Telegram bot).
// Input and output count together, shared by every model of a group; the day turns at 00:00 UTC (09:00 KST).
// The Usage API shows a request about five minutes after it ran (2026-11-12: a 00:19 request listed at 00:24).
// Without the key file nothing is asked and the list goes without this table: deleting the file turns it off.
// Tokens of any other tier were billed: past the day's limit a request goes to the paid tier (2026-11-12: 15 Astra
// requests of about 21만 each, $5.46, left the balance at -$3.54 with no payment method and the API refusing). They
// get a row of their own with today's cost (Costs API, same key), shown only when there are any.
internal sealed record FreeTokenTable(string Title, IReadOnlyList<HistoryRow> Rows, IReadOnlyList<string>? Header = null)
{
    // Same words and rows (the record alone compares the lists by reference).
    public bool Same(FreeTokenTable? other) => other is not null && Title == other.Title && Rows.SequenceEqual(other.Rows);
}

internal sealed class OpenAiFreeTokenMonitor : IDisposable
{
    internal const string IncentiveTier = "incentivized-tier";
    // This organization's band (usage tier 1-2, "Build"): 250,000 a day for the large group, 2,500,000 for the small.
    internal const long LargeLimit = 250_000;
    internal const long SmallLimit = 2_500_000;
    internal static readonly string[] Header = ["", "사용", "남음", "남은 %", "한도", "요청", "오늘 쓴 모델"];
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };

    private readonly KeyFile _key;
    private System.Threading.Timer? _timer;
    private int _busy;
    private volatile FreeTokenTable? _current;
    private Usage? _last;

    public OpenAiFreeTokenMonitor(string? keyFile = null) => _key = new(keyFile ?? KeyFile.InAppData("openai-admin-key.dpapi"), "openai-free");

    public FreeTokenTable? Current => _current;

    public void Start() => _timer ??= new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(3), Every);

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            var key = ReadKey();
            if (key is null) { Publish(null); return; }
            var now = DateTimeOffset.UtcNow;
            try
            {
                _last = Summarize(Fetch(key, now), now) with { Cost = Billed(key, now) };
                Publish(Table(_last, now, failed: false));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                DiagnosticLog.Info("openai-free", "usage not read: " + e.GetType().Name + (e is HttpRequestException h && h.StatusCode is { } code ? " " + (int)code : ""));
                // The last numbers stay, marked as not refreshed, while they are still today's.
                Publish(Table(_last is { } last && last.Day == now.UtcDateTime.Date ? last : null, now, failed: true));
            }
        }
        catch (Exception e) { DiagnosticLog.Error("openai-free", e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Publish(FreeTokenTable? table) => _current = table;

    private string? ReadKey() => _key.Read();

    private static string Fetch(string key, DateTimeOffset now) => Get(key,
        "https://api.openai.com/v1/organization/usage/completions?start_time=" + DayStart(now)
        + "&bucket_width=1d&group_by=service_tier&group_by=model&limit=1");

    private static long DayStart(DateTimeOffset now) => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeSeconds();

    // Today's cost in dollars, or null when it could not be read (the billed row then shows a dash for it).
    private static decimal? Billed(string key, DateTimeOffset now)
    {
        try { return SumCost(Get(key, "https://api.openai.com/v1/organization/costs?start_time=" + DayStart(now) + "&bucket_width=1d&limit=1")); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            DiagnosticLog.Info("openai-free", "costs not read: " + e.GetType().Name);
            return null;
        }
    }

    // The Costs API's answer added up: data[].results[].amount.value.
    internal static decimal SumCost(string json)
    {
        decimal sum = 0;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            foreach (var bucket in buckets.EnumerateArray())
                if (bucket.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    foreach (var row in results.EnumerateArray())
                        if (row.TryGetProperty("amount", out var amount) && amount.TryGetProperty("value", out var value)
                            && value.TryGetDecimal(out var dollars))
                            sum += dollars;
        return sum;
    }

    private static string Get(string key, string uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new("Bearer", key);
        using var response = Client.Send(request);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return reader.ReadToEnd();
    }

    internal sealed record GroupUsage(long Tokens, long Requests, IReadOnlyList<string> Models);
    internal sealed record Usage(DateTime Day, GroupUsage Large, GroupUsage Small, GroupUsage Billed, decimal? Cost = null);

    // Today's complimentary tokens per group and the billed ones (any other tier) apart, from the Usage API's answer.
    internal static Usage Summarize(string json, DateTimeOffset now)
    {
        var tokens = new long[3];
        var requests = new long[3];
        var models = new[] { new Dictionary<string, long>(), new Dictionary<string, long>(), new Dictionary<string, long>() };
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var buckets) && buckets.ValueKind == JsonValueKind.Array)
            foreach (var bucket in buckets.EnumerateArray())
            {
                if (!bucket.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in results.EnumerateArray())
                {
                    if (!row.TryGetProperty("service_tier", out var tier) || tier.ValueKind != JsonValueKind.String) continue;
                    var model = row.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
                    var group = tier.GetString() != IncentiveTier ? 2 : IsSmall(model) ? 1 : 0;
                    var used = Number(row, "input_tokens") + Number(row, "output_tokens");
                    tokens[group] += used;
                    requests[group] += Number(row, "num_model_requests");
                    var name = BaseName(model);
                    models[group][name] = models[group].GetValueOrDefault(name) + used;
                }
            }
        GroupUsage Group(int i) => new(tokens[i], requests[i],
            models[i].OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray());
        return new(now.UtcDateTime.Date, Group(0), Group(1), Group(2));
    }

    private static long Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.TryGetInt64(out var n) ? n : 0;

    // "gpt-5.4-mini-2026-03-17" → "gpt-5.4-mini": the Usage API names the dated snapshot.
    internal static string BaseName(string model) => Regex.Replace(model, @"-\d{4}-\d{2}-\d{2}$", "");

    // The small group of help.openai.com/en/articles/10306912 (checked 2026-11-12); everything else complimentary
    // counts in the large group. A model not on either list goes by its name: mini and nano are small.
    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.1-codex-mini", "gpt-5-mini", "gpt-5-nano",
        "gpt-4.1-mini", "gpt-4.1-nano", "gpt-4o-mini", "o4-mini", "o1-mini", "codex-mini-latest",
    };
    private static readonly HashSet<string> Large = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.5", "gpt-5.4", "gpt-5.2", "gpt-5.1",
        "gpt-5.1-codex", "gpt-5-codex", "gpt-5", "gpt-5-chat-latest", "gpt-4.1", "gpt-4o", "o3", "o1-preview", "o1",
    };

    internal static bool IsSmall(string model)
    {
        var name = BaseName(model);
        if (Small.Contains(name)) return true;
        if (Large.Contains(name)) return false;
        return name.Contains("mini", StringComparison.OrdinalIgnoreCase) || name.Contains("nano", StringComparison.OrdinalIgnoreCase);
    }

    // The two groups as rows of the list, the large first; the title says when it was read and when the day turns.
    internal static FreeTokenTable Table(Usage? usage, DateTimeOffset now, bool failed)
    {
        var at = now.ToOffset(TimeSpan.FromHours(9)).ToString("HH:mm", CultureInfo.InvariantCulture);
        var title = "OpenAI 무료 토큰 · " + (failed ? $"조회 실패 {at}" : $"{at} 조회") + " · 09:00 초기화";
        HistoryRow Row(string label, GroupUsage? group, long limit)
        {
            if (group is null) return new(label, "—", "—", "—", OverlayPresentationBuilder.FormatTokenCount(limit), "—", "—");
            var left = Math.Max(0, limit - group.Tokens);
            return new(label,
                OverlayPresentationBuilder.FormatTokenCount(group.Tokens),
                OverlayPresentationBuilder.FormatTokenCount(left),
                // Rounded down, so a little used never reads 100.0%.
                (left == limit ? "100" : left == 0 ? "0"
                    : (Math.Floor(1000.0 * left / limit) / 10).ToString("0.0", CultureInfo.InvariantCulture)) + "%",
                OverlayPresentationBuilder.FormatTokenCount(limit),
                ModelName: group.Models.Count == 0 ? "—" : string.Join(" · ", group.Models.Select(model => ModelNames.Short(model) ?? model)),
                Cache: group.Requests.ToString("N0", CultureInfo.InvariantCulture));
        }
        HistoryRow[] rows = [Row("큰 묶음", usage?.Large, LargeLimit), Row("작은 묶음", usage?.Small, SmallLimit)];
        // Billed tokens: no limit or share left, the day's cost in the limit column.
        if (usage?.Billed is { Tokens: > 0 } billed)
            rows = [.. rows, new("유료 청구", OverlayPresentationBuilder.FormatTokenCount(billed.Tokens), "—", "—",
                usage.Cost is { } cost ? "$" + cost.ToString("0.00", CultureInfo.InvariantCulture) : "금액 —",
                ModelName: string.Join(" · ", billed.Models.Select(model => ModelNames.Short(model) ?? model)),
                Cache: billed.Requests.ToString("N0", CultureInfo.InvariantCulture))];
        return new(title, rows);
    }

    public void Dispose() => _timer?.Dispose();
}

// An API key stored encrypted for this Windows user (DPAPI) by scripts/save-key.ps1, in %APPDATA%\TokenOverlay; read
// again only when the file changes. No file: null (the row it feeds is off).
internal sealed class KeyFile(string path, string log)
{
    private (DateTime Write, string Key)? _known;

    internal static string InAppData(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOverlay", name);

    public string? Read()
    {
        try
        {
            if (!File.Exists(path)) { _known = null; return null; }
            var write = File.GetLastWriteTimeUtc(path);
            if (_known is { } known && known.Write == write) return known.Key.Length == 0 ? null : known.Key;
            var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)).Trim();
            _known = (write, key);
            return key.Length == 0 ? null : key;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            DiagnosticLog.Info(log, "key not read: " + e.GetType().Name);
            return null;
        }
    }
}
