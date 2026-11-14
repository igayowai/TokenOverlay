using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TokenOverlay;

// OpenCode Go's subscription windows in the Ctrl list (user, 2026-11-12: "오픈코드go까지 붙이고싶은데"), first a line,
// then a table like OpenAI's above it ("너무 그지같은데 openai것처럼 만들고 그 위에다 붙여넣어"). The limits are dollar amounts per model (5 hours
// 20% of the month's, a week 50%), but the Go API key reads only each window's share used, whether it is reached and
// when it resets: GET opencode.ai/zen/go/v1/usage → {"usage":{"rolling"|"weekly"|"monthly":{"status","percent",
// "resetsAt"}}}. Not in OpenCode's docs; OpenCode's own console and tools use it (pistonex/opencode-go-usage
// docs/RESEARCH.md, onWatch). A shape it does not know reads as a failed read, never as wrong numbers.
// Without the key file (scripts/save-key.ps1 opencode) nothing is asked and the line is left out.
internal sealed class OpenCodeGoMonitor : IDisposable
{
    private const string Url = "https://opencode.ai/zen/go/v1/usage";
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };

    private readonly KeyFile _key;
    private readonly string _name;
    private readonly string _log;
    private System.Threading.Timer? _timer;
    private int _busy;
    private volatile FreeTokenTable? _table;
    private Window[]? _last;

    // A second Go account has its own key file and name (user, 2026-11-14: "오픈코드 계정 2개돼서 api도 2개됐는데"):
    // scripts/save-key.ps1 opencode2 → opencode-go-key-2.dpapi, titled "OpenCode Go 2".
    public OpenCodeGoMonitor(string? keyFile = null, string name = "OpenCode Go", string log = "opencode-go")
    {
        _key = new(keyFile ?? KeyFile.InAppData("opencode-go-key.dpapi"), log);
        _name = name;
        _log = log;
    }

    public FreeTokenTable? Table => _table;

    public void Start() => _timer ??= new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(4), Every);

    internal sealed record Window(string Name, int Percent, bool Reached, DateTimeOffset? ResetsAt);

    // The same seven columns as the OpenAI table under it, so 사용, 남은 % and 한도 line up: share used, time to the
    // reset, share left, the window's limit as a share of the month's (docs: 5 hours 20%, a week 50%, the month 100%),
    // whether it is reached, and the reset in KST.
    internal static readonly string[] Header = ["", "사용", "초기화까지", "남은 %", "한도", "상태", "초기화 시각"];
    private static readonly (string Field, string Name, string Limit)[] Windows =
        [("rolling", "5시간", "월의 20%"), ("weekly", "주간", "월의 50%"), ("monthly", "월간", "월 한도")];

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            var key = _key.Read();
            if (key is null) { _table = null; _last = null; return; }
            var now = DateTimeOffset.UtcNow;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.Authorization = new("Bearer", key);
                using var response = Client.Send(request);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    DiagnosticLog.Info(_log, "usage refused: " + (int)response.StatusCode);
                    _table = Build(null, now, response.StatusCode == HttpStatusCode.Forbidden ? "구독 확인 안 됨" : "키가 맞지 않음", _name);
                    return;
                }
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(response.Content.ReadAsStream());
                _last = Parse(reader.ReadToEnd()) ?? throw new JsonException("unknown shape");
                _table = Build(_last, now, null, _name);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                DiagnosticLog.Info(_log, "usage not read: " + e.GetType().Name);
                var at = now.ToOffset(TimeSpan.FromHours(9)).ToString("HH:mm", CultureInfo.InvariantCulture);
                _table = Build(_last, now, (_last is null ? "조회 실패 " : "갱신 실패 ") + at, _name);
            }
        }
        catch (Exception e) { DiagnosticLog.Error(_log, e); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    // The three windows in order; null when the answer has none of them.
    internal static Window[]? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var windows = new List<Window>();
        foreach (var (field, name, _) in Windows)
        {
            if (!usage.TryGetProperty(field, out var w) || w.ValueKind != JsonValueKind.Object
                || !w.TryGetProperty("percent", out var p) || !p.TryGetDouble(out var percent)) continue;
            var reached = w.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() != "ok";
            DateTimeOffset? resets = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            windows.Add(new(name, (int)Math.Round(percent, MidpointRounding.AwayFromZero), reached, resets));
        }
        return windows.Count == 0 ? null : windows.ToArray();
    }

    // The table: its title says when it was read (KST) or what went wrong; without numbers the rows show dashes.
    internal static FreeTokenTable Build(IReadOnlyList<Window>? windows, DateTimeOffset now, string? problem, string name = "OpenCode Go")
    {
        var kst = TimeSpan.FromHours(9);
        var title = name + " · " + (problem ?? now.ToOffset(kst).ToString("HH:mm", CultureInfo.InvariantCulture) + " 조회");
        var korean = CultureInfo.GetCultureInfo("ko-KR");
        var rows = Windows.Select(window =>
        {
            if (windows?.FirstOrDefault(w => w.Name == window.Name) is not { } w)
                return new HistoryRow(window.Name, "—", "—", "—", window.Limit, "—", "—");
            var percent = Math.Clamp(w.Percent, 0, 100);
            return new HistoryRow(w.Name, $"{percent}%", w.ResetsAt is { } at ? Until(at - now) : "—", $"{100 - percent}%", window.Limit,
                ModelName: w.ResetsAt is { } reset ? reset.ToOffset(kst).ToString("MM-dd (ddd) HH:mm", korean) : "—",
                Cache: w.Reached ? TokenStripForm.BlockedState : "정상");
        }).ToArray();
        return new(title, rows, Header);
    }

    // Time to a reset: "40분" and "3시간" rounded up, hours up to two days ("31시간"), then whole days ("9일").
    internal static string Until(TimeSpan left) => left <= TimeSpan.Zero ? "곧"
        : left < TimeSpan.FromHours(1) ? $"{Math.Ceiling(left.TotalMinutes):0}분"
        : left < TimeSpan.FromDays(2) ? $"{Math.Ceiling(left.TotalHours):0}시간"
        : $"{Math.Floor(left.TotalDays):0}일";

    public void Dispose() => _timer?.Dispose();
}
