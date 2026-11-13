using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TokenOverlay;

// The Anthropic Console's credit balance in the Ctrl list (user, 2026-11-12: "앤트로픽 잔액도 어떻게 안되나 ... 저기 숫자만
// 긁어오면 되는데", then "쿠키로 붙이기"): only the dollars left, bold, in the middle of the week's reset line ("저기
// 빨간곳에 굵은검정글씨로 남은한도 달러 값만"). This account is an individual organization, so there is no admin key; the
// billing page's own numbers come from GET platform.claude.com/api/organizations/{org}/prepaid/credits, which opens
// only with the Console login cookie (without it: 403 account_session_invalid; Cloudflare lets a plain client through).
// The cookie is a login to the whole account: kept only DPAPI-encrypted for this Windows user (scripts/save-key.ps1
// anthropic-session), never logged. Its name on the site is not readable from the page (HttpOnly), so sessionKey is
// tried first, then sessionKeyV3, and the one answered is kept. Not a published API: a shape it does not know reads
// as a failed read, never as wrong numbers. The organization is the one with the "api" capability.
internal sealed class AnthropicCreditMonitor : IDisposable
{
    private const string Site = "https://platform.claude.com";
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly string[] CookieNames = ["sessionKey", "sessionKeyV3"];

    private readonly KeyFile _key;
    private System.Threading.Timer? _timer;
    private int _busy;
    private volatile string? _left;
    private Credits? _last;
    private string? _org;
    private string _cookieName = CookieNames[0];

    public AnthropicCreditMonitor(string? keyFile = null) =>
        _key = new(keyFile ?? KeyFile.InAppData("anthropic-session.dpapi"), "anthropic-credits");

    public string? Left => _left;

    public void Start() => _timer ??= new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), Every);

    internal sealed record Grant(string Name, decimal Granted, decimal Left, DateTimeOffset? Expires);
    // Promotional grants, and the paid balance (credits bought), which can go below zero.
    internal sealed record Credits(IReadOnlyList<Grant> Grants, decimal Paid);

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            var cookie = _key.Read();
            if (cookie is null) { _left = null; _last = null; _org = null; return; }
            var now = DateTimeOffset.UtcNow;
            try
            {
                var (status, body) = Get(cookie, "/api/organizations");
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // The other cookie name, once; then the login has run out.
                    var other = CookieNames.First(name => name != _cookieName);
                    var (retry, retryBody) = Get(cookie, "/api/organizations", other);
                    if (retry is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        DiagnosticLog.Info("anthropic-credits", "login refused: " + (int)status + "/" + (int)retry);
                        _left = Text(_last, now, "로그인 만료");
                        return;
                    }
                    _cookieName = other;
                    (status, body) = (retry, retryBody);
                }
                if (status != HttpStatusCode.OK) throw new HttpRequestException("organizations " + (int)status);
                _org = ApiOrganization(body) ?? throw new JsonException("no api organization");
                var (creditStatus, credits) = Get(cookie, $"/api/organizations/{_org}/prepaid/credits");
                if (creditStatus != HttpStatusCode.OK) throw new HttpRequestException("credits " + (int)creditStatus);
                _last = Parse(credits) ?? throw new JsonException("unknown shape");
                _left = Text(_last, now, null);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                DiagnosticLog.Info("anthropic-credits", "credits not read: " + e.GetType().Name);
                _left = Text(_last, now, "조회 실패");
            }
        }
        catch (Exception e) { DiagnosticLog.Error("anthropic-credits", e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private (HttpStatusCode Status, string Body) Get(string cookie, string path, string? name = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Site + path);
        request.Headers.Add("Cookie", (name ?? _cookieName) + "=" + cookie);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = Client.Send(request);
        using var reader = new StreamReader(response.Content.ReadAsStream());
        return (response.StatusCode, reader.ReadToEnd());
    }

    // The organization the Console bills (capability "api"), from /api/organizations.
    internal static string? ApiOrganization(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var org in doc.RootElement.EnumerateArray())
            if (org.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                && caps.EnumerateArray().Any(cap => cap.ValueKind == JsonValueKind.String && cap.GetString() == "api")
                && org.TryGetProperty("uuid", out var uuid) && Guid.TryParse(uuid.GetString(), out var id))
                return id.ToString();
        return null;
    }

    // prepaid/credits: promo_tranches[] {name, granted_amount_minor_units, remaining_amount_minor_units, expires_at} and
    // amount_without_scoped_credits (the paid balance), all in cents. Null without the promo list.
    internal static Credits? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("promo_tranches", out var promos)
            || promos.ValueKind != JsonValueKind.Array) return null;
        var grants = new List<Grant>();
        foreach (var promo in promos.EnumerateArray())
        {
            if (!Cents(promo, "granted_amount_minor_units", out var granted) || !Cents(promo, "remaining_amount_minor_units", out var left)) continue;
            var name = promo.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "크레딧";
            DateTimeOffset? expires = promo.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            grants.Add(new(name, granted, left, expires));
        }
        Cents(root, "amount_without_scoped_credits", out var paid);
        return new(grants, paid);
    }

    private static bool Cents(JsonElement element, string name, out decimal dollars)
    {
        dollars = 0;
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt64(out var cents)) return false;
        dollars = cents / 100m;
        return true;
    }

    // What is left in dollars: the running grants and the paid balance together ("$188.03"). Without numbers yet, what
    // went wrong; a failed refresh keeps the last value (credits move slowly).
    internal static string Text(Credits? credits, DateTimeOffset now, string? problem)
    {
        if (credits is null) return problem ?? "—";
        var left = credits.Grants.Where(grant => grant.Expires is null || grant.Expires > now).Sum(grant => Math.Max(0, grant.Left))
            + credits.Paid;
        return (left < 0 ? "-$" : "$") + Math.Abs(left).ToString("0.00", CultureInfo.InvariantCulture);
    }

    public void Dispose() => _timer?.Dispose();
}
