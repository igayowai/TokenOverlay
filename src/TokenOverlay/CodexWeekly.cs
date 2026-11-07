using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// One reading of the account's weekly percent: when, the percent, and when that week ends.
// Local: logged with a request from one of the user's devices (so that device was busy right then);
// false for a reading asked from Codex in between, which says nothing about who was busy.
// Person: 0 for the user's own devices; 1, 2, … for a reading logged with another person's request (through the share).
internal readonly record struct CodexWeeklySample(long AtMs, int Percent, long ResetMs, bool Local = true, int Person = 0);
// The running week: when it started, the account's percent, and the part credited to this PC (together with the
// user's other devices sharing through Google Drive). ThisPcOnly: set when other devices share, this PC's part alone.
// People: other people's parts, person 1 first. Devices: the user's other devices by name with their parts of the rest
// of ThisPc (WeeklySplit.DeviceParts).
internal sealed record CodexWeek(long StartMs, long ResetMs, int Account, int ThisPc, int? ThisPcOnly = null, IReadOnlyList<int>? People = null,
    IReadOnlyList<(string Name, int Percent)>? Devices = null);

// How much of the account's Codex weekly percent was spent from this PC.
// Codex logs the percent with every request this PC sends, and the overlay also asks Codex for it about once
// a minute while this PC is busy (every ten minutes otherwise). A rise between two readings is credited to this
// PC when the two are close together and a request was logged in that stretch; any other rise was spent
// elsewhere (another device, or the web). Without the in-between readings a rise across a quiet stretch only
// shows up with this PC's next request, still as "elsewhere". Another device used during this PC's busy
// stretch is counted here too: the figure is an upper bound for this PC, and "elsewhere" a lower bound.
internal static class CodexWeeklyMath
{
    internal const long QuietMs = 15 * 60 * 1000;
    // The read percent trails real use a little: a rise may show up shortly after the request.
    internal const long LagMs = 2 * 60 * 1000;
    private const long WeekMs = 7L * 24 * 60 * 60 * 1000;
    // The same reset is reported a second apart now and then; a real new week moves it by hours or days.
    private const long SameResetMs = 60 * 60 * 1000;
    private const int DropReset = 5;

    // samples oldest first. Null when there is no reading for a week that is still running.
    // people: how many other people the samples name (Person 1 … people).
    public static CodexWeek? Week(IReadOnlyList<CodexWeeklySample> samples, DateTimeOffset now, int people = 0)
    {
        if (samples.Count == 0) return null;
        var reset = samples[^1].ResetMs;
        if (reset <= now.ToUnixTimeMilliseconds()) return null;
        var first = samples.Count - 1;
        while (first > 0 && Math.Abs(samples[first - 1].ResetMs - reset) < SameResetMs) first--;
        var requests = Enumerable.Range(0, people + 1)
            .Select(person => samples.Where(s => s.Local && s.Person == person).Select(s => s.AtMs).ToArray()).ToArray();
        bool Busy(int person, long from, long to)
        {
            var list = requests[person];
            var index = Array.BinarySearch(list, from - LagMs + 1);
            if (index < 0) index = ~index;
            return index < list.Length && list[index] <= to;
        }
        // Who spent the rise up to reading i: the user's side whole when it was busy (as before there were people),
        // else the people who were, evenly. Codex logs no weights to share it by. Null when nobody was busy.
        double[]? Shares(int i)
        {
            if (samples[i].AtMs - samples[i - 1].AtMs > QuietMs) return null;
            var busy = Enumerable.Range(0, people + 1).Where(person => Busy(person, samples[i - 1].AtMs, samples[i].AtMs)).ToArray();
            if (busy.Length == 0) return null;
            var shares = new double[people + 1];
            if (busy[0] == 0) shares[0] = 1;
            else foreach (var person in busy) shares[person] = 1.0 / busy.Length;
            return shares;
        }
        var credit = new double[people + 1];
        void Credit(double amount, int i)
        {
            if (amount > 0 && Shares(i) is { } shares)
                for (var person = 0; person <= people; person++) credit[person] += amount * shares[person];
        }
        // What the week's first reading already shows: this PC's only when this PC was busy right before it.
        if (first > 0) Credit(samples[first].Percent, first);
        var highest = samples[first].Percent;
        // Every reset seen so far (banked or for everyone) moved the end of the week; a clear drop without that
        // is taken as a reset too, starting the week at that reading. A point or two down is rounding noise.
        var start = reset - WeekMs;
        for (var i = first + 1; i < samples.Count; i++)
        {
            if (samples[i].Percent <= highest - DropReset)
            {
                start = samples[i].AtMs;
                highest = samples[i].Percent;
                Array.Clear(credit);
                Credit(samples[i].Percent, i);
                continue;
            }
            var rise = samples[i].Percent - highest;
            if (rise <= 0) continue;
            Credit(rise, i);
            highest = samples[i].Percent;
        }
        return new CodexWeek(start, reset, highest, Math.Min((int)Math.Round(credit[0]), highest),
            People: people == 0 ? null : credit[1..].Select(c => (int)Math.Round(c)).ToArray());
    }

    // This PC / the user's other devices by name (or just "나" without them) / other people / "다른 곳", as WeeklySplit words it.
    public static string Line(CodexWeek week, WeeklySplit.Names? names = null, IReadOnlyList<string>? people = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"이번주 {WeeklySplit.Since(week.StartMs)}~ · {WeeklySplit.Text(week.Account, week.ThisPc, week.ThisPcOnly, names, WeeklyUsageMath.Parts(people, week.People), week.Devices)}");

    // A token_count line → its Codex weekly reading, or null. Other limits (per model, premium) are left out.
    // {"timestamp":"…","payload":{"type":"token_count","rate_limits":{"limit_id":"codex",
    //   "primary":{"used_percent":33.0,"window_minutes":10080,"resets_at":<s>},"secondary":null}}}
    public static CodexWeeklySample? ParseLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object
                || !limits.TryGetProperty("limit_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != "codex"
                || !root.TryGetProperty("timestamp", out var stamp) || stamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(stamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return null;
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (limits.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
                    && window.TryGetProperty("window_minutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number
                    && minutes.GetInt64() == 7 * 24 * 60
                    && window.TryGetProperty("used_percent", out var used) && used.ValueKind == JsonValueKind.Number
                    && window.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.Number)
                    return new CodexWeeklySample(at.ToUnixTimeMilliseconds(), (int)Math.Round(used.GetDouble()), resets.GetInt64() * 1000);
            }
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return null; }
    }
}

// Reads the weekly readings out of this PC's Codex logs (read-only), off the UI thread, about once a minute,
// and asks Codex itself for the current percent in between (see CodexUsageApi). The first pass reads the last
// eight days of logs; later passes only what was added. The asked readings are kept in a small file of its own.
internal sealed class CodexWeeklyMonitor(IReadOnlyList<string> roots, Func<DriveShare?>? shareSource = null,
    Func<DateTimeOffset, UsageApiResult>? api = null, string? store = null) : IDisposable
{
    internal static string DefaultStorePath => Path.Combine(AppPaths.DataFolder, "codex-weekly.json");
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ApiActiveInterval = TimeSpan.FromSeconds(55);
    private static readonly TimeSpan ApiIdleInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ApiBackoff = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ApiRetry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ActiveWithin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    private readonly List<CodexWeeklySample> _apiSamples = new();
    private DateTimeOffset _apiNext;
    private bool _wasActive;
    private volatile bool _apiEnabled = true;
    private volatile bool _userSet;
    private volatile string _apiStatus = "대기";
    // Reset credits the account can still use (banked resets), from the last reading; null = not known.
    private int? _resetCredits;
    private bool _loaded;
    private bool _dirty;
    private DateTimeOffset _saved;
    private static readonly TimeSpan Kept = TimeSpan.FromDays(8);
    private static readonly byte[] Mark = Encoding.UTF8.GetBytes("\"rate_limits\"");
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    // Keyed by time: an archived chat's log moves to another folder and is read again.
    private readonly SortedDictionary<long, CodexWeeklySample> _samples = new();
    private System.Threading.Timer? _timer;
    private int _busy;
    private volatile string? _footer;

    private DriveShare? _sharedTo;
    private long _sharedLast = -1;
    private int _sharedCount = -1;

    public CodexWeeklyMonitor(string sessionRoot, Func<DriveShare?>? shareSource) : this(
        [sessionRoot, Path.Combine(Directory.GetParent(sessionRoot)?.FullName ?? sessionRoot, "archived_sessions")], shareSource,
        CodexUsageApi.Fetch, DefaultStorePath) { }

    // One-minute readings asked from Codex. Off: only the percent Codex logs with this PC's requests.
    public bool ApiEnabled => _apiEnabled;
    public string ApiStatus => _apiEnabled ? _apiStatus : "꺼짐 · 요청 기록에 남는 값만 사용";
    public void SetApiEnabled(bool value) { _apiEnabled = value; _userSet = true; _dirty = true; _apiNext = default; }
    internal IReadOnlyList<CodexWeeklySample> ApiSamples => _apiSamples.ToArray();
    // For bug reports: what the last pass wrote to the share and read from each other device there.
    internal string ShareNote { get; private set; } = "not read yet";

    // The list's bottom lines, or null while no week is known.
    public string? Footer => _footer;
    internal IReadOnlyList<CodexWeeklySample> Samples => _samples.Values.ToArray();

    public void Start() => _timer ??= new System.Threading.Timer(_ => Refresh(), null, TimeSpan.FromSeconds(5), Tick);

    public void Dispose()
    {
        _timer?.Dispose(); _timer = null;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { if (_loaded) Save(DateTimeOffset.UtcNow, force: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public void Refresh()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { Update(DateTimeOffset.UtcNow); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Change("codex-weekly-read", e.GetType().Name + ": " + e.Message); }
        // A timer thread must not end the overlay: anything else is logged for a bug report.
        catch (Exception e) { DiagnosticLog.Error("codex-weekly", e); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    internal void Update(DateTimeOffset now)
    {
        Load();
        var since = (now - Kept).UtcDateTime;
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < since) continue;
                    scanned.Add(file);
                    ScanFile(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        // A file that went quiet (or was deleted) drops its read position; written again, it is read from the
        // start, which only finds the same readings again.
        foreach (var path in _offsets.Keys.Where(path => !scanned.Contains(path)).ToArray()) _offsets.Remove(path);
        var oldest = (now - Kept).ToUnixTimeMilliseconds();
        foreach (var at in _samples.Keys.TakeWhile(at => at < oldest).ToArray()) _samples.Remove(at);
        if (_apiSamples.RemoveAll(s => s.AtMs < oldest) > 0) _dirty = true;
        var own = _samples.Values.ToArray();
        AskApi(now, own);
        var merged = new SortedDictionary<long, CodexWeeklySample>(_samples);
        foreach (var sample in _apiSamples) merged.TryAdd(sample.AtMs, sample);
        // The same time line where only this PC's requests count as busy: this PC's part alone.
        var alone = new SortedDictionary<long, CodexWeeklySample>(merged);
        var mine = false;
        var people = new List<string>();
        // The user's other devices by shown name, each with its readings: in the second time line each one is busy
        // with its own requests, after other people.
        var devices = new Dictionary<string, List<CodexWeeklySample>>(StringComparer.OrdinalIgnoreCase);
        var share = shareSource?.Invoke();
        if (share != _sharedTo) { _sharedTo = share; _sharedCount = -1; _sharedLast = -1; }
        var note = new System.Text.StringBuilder(share is null ? "no share" : $"wrote samples {own.Length} (last {DriveShare.When(own.LastOrDefault().AtMs)})");
        if (share is not null)
        {
            if (own.Length > 0 && (own.Length != _sharedCount || own[^1].AtMs != _sharedLast))
            {
                share.Write("codex", new { device = share.Device, name = share.Name, samples = own.Select(s => new[] { s.AtMs, s.Percent, s.ResetMs }) });
                _sharedCount = own.Length; _sharedLast = own[^1].AtMs;
            }
            foreach (var file in share.ReadShared("codex", now))
            {
                using (file.Document)
                {
                    // Another person's requests are theirs in both time lines; the user's other devices are "me"
                    // in the first and each their own in the second (one merged into this PC is this PC there).
                    var person = 0;
                    if (file.Person is { } name)
                    {
                        person = people.FindIndex(known => known.Equals(name, StringComparison.OrdinalIgnoreCase)) + 1;
                        if (person == 0) { people.Add(name); person = people.Count; }
                    }
                    else mine = true;
                    List<CodexWeeklySample>? apart = null;
                    if (file.Person is null && file.Device is { } device && !devices.TryGetValue(device, out apart)) devices[device] = apart = [];
                    var (read, lastRead) = (0, 0L);
                    // Another device's numbers: a percent and a reset within the week after the reading, or skipped.
                    foreach (var row in DriveShare.Rows(file.Document, "samples"))
                        if (row.Length == 3 && row[0] >= oldest && row[0] <= now.ToUnixTimeMilliseconds() + 60 * 60_000
                            && row[1] is >= 0 and <= 100 && row[2] > row[0] && row[2] - row[0] <= 8L * 24 * 60 * 60_000)
                        {
                            merged.TryAdd(row[0], new CodexWeeklySample(row[0], (int)row[1], row[2], Person: person));
                            if (apart is not null) apart.Add(new CodexWeeklySample(row[0], (int)row[1], row[2]));
                            else alone.TryAdd(row[0], new CodexWeeklySample(row[0], (int)row[1], row[2], Person: person));
                            read++; lastRead = Math.Max(lastRead, row[0]);
                        }
                    note.Append($" | {(file.Person is { } who ? "@" + who + " " : "")}{DriveShare.Label(file.Document)}: samples {read} (last {DriveShare.When(lastRead)})");
                }
            }
        }
        ShareNote = note.ToString();
        var week = CodexWeeklyMath.Week(merged.Values.ToArray(), now, people.Count);
        var named = devices.OrderByDescending(pair => pair.Value.Select(s => s.AtMs).DefaultIfEmpty().Max()).ToArray();
        for (var d = 0; d < named.Length; d++)
            foreach (var sample in named[d].Value) alone.TryAdd(sample.AtMs, sample with { Person = people.Count + 1 + d });
        if (mine && week is not null && CodexWeeklyMath.Week(alone.Values.ToArray(), now, people.Count + named.Length) is { } ownWeek)
        {
            var pcAlone = Math.Min(ownWeek.ThisPc, week.ThisPc);
            var parted = ownWeek.People?.Skip(people.Count).ToArray() ?? [];
            var start = week.StartMs;
            var parts = WeeklySplit.DeviceParts(week.ThisPc, pcAlone, named.Select((pair, d) =>
                (pair.Key, d < parted.Length ? (double)parted[d] : 0, pair.Value.Any(s => s.AtMs >= start))).ToArray());
            week = week with { ThisPcOnly = pcAlone, Devices = parts };
        }
        _footer = week is null ? null : CodexWeeklyMath.Line(week, mine ? share?.SplitNames() : null, people.Count == 0 ? null : people)
            + "\n" + WeeklyUsageMath.ResetLine(week.ResetMs, now)
            + (_apiEnabled && _resetCredits is int credits ? $" · 리셋 크레딧 {credits}개" : "");
        if (week is not null) DiagnosticLog.Change("codex-week", $"started {DateTimeOffset.FromUnixTimeMilliseconds(week.StartMs).ToLocalTime():MM-dd HH:mm} · ends {DateTimeOffset.FromUnixTimeMilliseconds(week.ResetMs).ToLocalTime():MM-dd HH:mm}");
        DiagnosticLog.Change("codex-weekly", week is null ? $"no running week ({own.Length} readings)" : $"week known · {merged.Count} readings · other devices {(mine ? "yes" : "no")}");
        DiagnosticLog.Change("codex-weekly-api", ApiStatus);
        DiagnosticLog.Change("codex-weekly-line", _footer?.Split('\n')[0] ?? "none");
        Save(now, force: false);
    }

    private void AskApi(DateTimeOffset now, CodexWeeklySample[] own)
    {
        if (api is null || !_apiEnabled) return;
        var active = own.Length > 0 && own[^1].AtMs >= (now - ActiveWithin).ToUnixTimeMilliseconds();
        // Coming back from a pause: read at once, so what was spent elsewhere meanwhile is not mixed with this PC's.
        var resumed = active && !_wasActive;
        _wasActive = active;
        if (now < _apiNext && !resumed) return;
        var result = api(now);
        _apiStatus = result.Status;
        if (result.WeeklyPercent is int percent && result.ResetMs is long reset)
        {
            _resetCredits = result.ResetCredits;
            var sample = new CodexWeeklySample(now.ToUnixTimeMilliseconds(), percent, reset, Local: false);
            // Keeps the first and the last reading of every unchanged run: all that attribution needs.
            if (_apiSamples.Count >= 2 && _apiSamples[^1] is var last && _apiSamples[^2] is var before
                && last.Percent == percent && before.Percent == percent && last.ResetMs == reset && before.ResetMs == reset)
                _apiSamples[^1] = sample;
            else _apiSamples.Add(sample);
            _dirty = true;
            _apiNext = now + (active ? ApiActiveInterval : ApiIdleInterval);
        }
        else _apiNext = now + (result.RetryLater ? ApiBackoff : ApiRetry);
    }

    private sealed record StoreFile(int Version, bool? Api, long[][]? Samples);
    private static readonly JsonSerializerOptions StoreJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (store is null || !File.Exists(store)) return;
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(store), StoreJson);
            if (file is null) return;
            if (file.Api is bool enabled && !_userSet) _apiEnabled = enabled;
            _apiSamples.InsertRange(0, (file.Samples ?? []).Where(s => s.Length == 3)
                .Select(s => new CodexWeeklySample(s[0], (int)s[1], s[2], Local: false)).OrderBy(s => s.AtMs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void Save(DateTimeOffset now, bool force)
    {
        if (store is null || !(force || (_dirty && now - _saved >= SaveInterval))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        var temp = store + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile(1, _apiEnabled,
            _apiSamples.Select(s => new[] { s.AtMs, s.Percent, s.ResetMs }).ToArray()), StoreJson));
        File.Move(temp, store, overwrite: true);
        _dirty = false; _saved = now;
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
                if (line.IndexOf(Mark) >= 0 && CodexWeeklyMath.ParseLine(Encoding.UTF8.GetString(line)) is { } sample)
                    _samples[sample.AtMs] = sample;
                lineStart = newline + 1;
            }
            offset += lineStart;
            Buffer.BlockCopy(buffer, lineStart, buffer, 0, filled - lineStart);
            filled -= lineStart;
        }
        _offsets[path] = offset;
    }
}

// The account's Codex weekly percent straight from Codex, with the Codex app's own bundled command-line tool:
// it is started hidden as "codex app-server", asked once ("account/rateLimits/read") and closed. Codex does its
// own login; the overlay never sees a token. Only the percent and the reset time are kept from the answer.
// The method is marked experimental by Codex, so any failure just leaves the logged readings to do the work.
internal static class CodexUsageApi
{
    private const int TimeoutMs = 20_000;
    internal const string AppClosed = "Codex 앱이 꺼져 있음 · 켜지면 다시 읽음";

    public static UsageApiResult Fetch(DateTimeOffset now)
    {
        if (FindCli() is not { } cli) return new(null, AppClosed);
        try
        {
            var answer = Ask(cli);
            if (answer is null) return new(null, "Codex 응답 없음", RetryLater: true);
            var (percent, reset, problem) = Parse(answer);
            return percent is int p ? new(p, "정상", ResetMs: reset, ResetCredits: ParseCredits(answer)) : new(null, problem ?? "주간 한도 없음", RetryLater: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
            or InvalidOperationException or JsonException)
        {
            DiagnosticLog.Change("codex-usage-api", e.GetType().Name + ": " + e.Message);
            return new(null, "Codex 조회 실패", RetryLater: true);
        }
    }

    // The running Codex app's folder holds resources\codex.exe.
    internal static string? FindCli()
    {
        foreach (var name in new[] { "ChatGPT", "Codex" })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } exe
                            && Path.Combine(Path.GetDirectoryName(exe)!, "resources", "codex.exe") is var cli && File.Exists(cli))
                            return cli;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                }
            }
        }
        return null;
    }

    // {"id":1,"result":{"rateLimits":{…},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":36,
    //   "windowDurationMins":10080,"resetsAt":1792649651}}}}} → (36, reset in ms); an error → its message.
    internal static (int? Percent, long? ResetMs, string? Problem) Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error))
            return (null, null, "Codex 거절: " + (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "알 수 없음"));
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return (null, null, null);
        var snapshots = new List<JsonElement>();
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) && buckets.ValueKind == JsonValueKind.Object
            && buckets.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object) snapshots.Add(codex);
        if (result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object
            && (!single.TryGetProperty("limitId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() == "codex")) snapshots.Add(single);
        foreach (var snapshot in snapshots)
        {
            foreach (var name in new[] { "primary", "secondary" })
            {
                if (snapshot.TryGetProperty(name, out var window) && window.ValueKind == JsonValueKind.Object
                    && window.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind == JsonValueKind.Number
                    && minutes.GetInt64() == 7 * 24 * 60
                    && window.TryGetProperty("usedPercent", out var used) && used.ValueKind == JsonValueKind.Number
                    && window.TryGetProperty("resetsAt", out var resets) && resets.ValueKind == JsonValueKind.Number)
                    return ((int)Math.Round(used.GetDouble()), resets.GetInt64() * 1000, null);
            }
        }
        return (null, null, null);
    }

    // {"result":{"rateLimitResetCredits":{"availableCount":2,…}}} → 2; null when Codex did not say.
    internal static int? ParseCredits(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("rateLimitResetCredits", out var credits) && credits.ValueKind == JsonValueKind.Object
            && credits.TryGetProperty("availableCount", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n)
            ? n : null;
    }

    // The answer to request 1; notifications and the initialize answer are passed over.
    internal static bool IsAnswer(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var n) && n == 1;
        }
        catch (JsonException) { return false; }
    }

    // One question over stdin/stdout (newline-delimited JSON-RPC), then the tool is closed.
    private static string? Ask(string cli)
    {
        // An empty folder of the overlay's own: no project files or project settings get involved.
        var folder = Path.Combine(AppPaths.DataFolder, "renew");
        Directory.CreateDirectory(folder);
        var start = new System.Diagnostics.ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("app-server");
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null) return null;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            var input = process.StandardInput;
            input.WriteLine(JsonSerializer.Serialize(new { method = "initialize", id = 0,
                @params = new { clientInfo = new { name = "token_overlay", title = "TokenOverlay", version = AppVersion.Text } } }));
            input.WriteLine("""{"method":"initialized"}""");
            input.WriteLine("""{"method":"account/rateLimits/read","id":1}""");
            input.Flush();
            var deadline = Environment.TickCount64 + TimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var read = process.StandardOutput.ReadLineAsync();
                if (!read.Wait(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64))) || read.Result is not { } line) return null;
                if (IsAnswer(line)) return line;
            }
            return null;
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            try { if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }
}