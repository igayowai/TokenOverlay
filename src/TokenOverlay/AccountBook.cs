using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// Which Claude and Codex account this PC is using, read from the apps' own files on this PC (no Google Drive), so a
// second person on the same Windows user (a sibling signing the apps in with their own account, 2026-11-07) keeps
// their own week: the user's week and what it shares leave out the time another account was in use, and the week
// shown is the account's in use now, under a name the user gives it.
// Accounts are kept only as short derived keys ("acct-" and 10 hex digits, as DeviceIdentity does for the PC): no
// account ID, mail address or name from the apps is stored, logged or shown.
// Claude has two sign-ins: the desktop app's (config.json, lastKnownAccountUuid) and Claude Code's in a terminal
// (~/.claude.json, oauthAccount). The app's is the account in use; the usage API is asked with Claude Code's token,
// so its readings are that account's whichever the app is on.
internal sealed class AccountBook
{
    internal const string Claude = "claude";
    internal const string Codex = "codex";
    internal static string DefaultPath => Path.Combine(AppPaths.DataFolder, "accounts.json");
    // Changes older than this are dropped, the last one before it kept so earlier times still have an account.
    private static readonly TimeSpan Kept = TimeSpan.FromDays(30);
    internal const string MineName = "나";

    // Org: the derived key of the account's Claude organization, when known (the usage record names readings by it).
    internal sealed record Account(string App, string Key, string Name, bool Mine, string? Org = null);
    // From FromMs on (Unix ms) the app was on this account, until the next period of the same app.
    internal sealed record Period(string App, string Key, long FromMs);
    private sealed record StoreFile(int Version, Account[]? Accounts, Period[]? Periods);
    private static readonly JsonSerializerOptions StoreJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<Account> _accounts = [];
    private readonly List<Period> _periods = [];

    public AccountBook(string? path = null)
    {
        _path = path;
        Load();
    }

    internal static string Key(string raw) =>
        "acct-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TokenOverlay|account|" + raw.Trim().ToLowerInvariant())))[..10];

    // The account read just now. A new one is added (the first of an app is the user's own, named "나"; later ones
    // "계정 2", "계정 3"), and a change starts a period. Returns the accounts before and after when it changed from a
    // known one (not on the first reading). org: the account's organization key, when this reading knows it.
    public (Account From, Account To)? Observe(string app, string key, long nowMs, string? org = null)
    {
        lock (_gate)
        {
            var changed = Know(app, key, org);
            var last = _periods.LastOrDefault(p => p.App == app);
            (Account, Account)? result = null;
            if (last?.Key != key)
            {
                _periods.Add(new Period(app, key, nowMs));
                changed = true;
                if (last is not null && Find(app, last.Key) is { } before) result = (before, Find(app, key)!);
            }
            changed |= Prune(nowMs);
            if (changed) Save();
            return result;
        }
    }

    // An account seen without being in use (Claude Code's terminal sign-in): known, with its organization, but no period.
    public void Note(string app, string key, string? org = null)
    {
        lock (_gate) if (Know(app, key, org)) Save();
    }

    // Claude Code's terminal sign-in as read just now (kept in memory only). The usage API is asked with it, so while it
    // is another person's (2026-11-07: the CLI signed in to someone else's account, the app still the user's), its
    // percent and reset time are that person's and must not go into the user's week. Returns whether that changed.
    private (string? Key, string? Org) _code;
    public bool NoteCode(string? key, string? org)
    {
        lock (_gate)
        {
            var before = CodeMineLocked();
            _code = (key, org);
            if (key is not null && Know(Claude, key, org)) Save();
            return CodeMineLocked() != before;
        }
    }

    // Whether Claude Code's sign-in is the user's own: its account is, or it is in the user's organization (or nothing
    // is known yet).
    public bool CodeIsMine()
    {
        lock (_gate) return CodeMineLocked();
    }

    private bool CodeMineLocked()
    {
        if (_code.Key is not { } key) return true;
        if (Find(Claude, key) is not { Mine: false }) return true;
        return _code.Org is { } org && _accounts.FirstOrDefault(a => a.App == Claude && a.Mine)?.Org == org;
    }

    // Claude Code's sign-in account, while it is not the user's own; else null.
    public Account? CodeOther()
    {
        lock (_gate) return CodeMineLocked() || _code.Key is not { } key ? null : Find(Claude, key);
    }

    private bool Know(string app, string key, string? org)
    {
        var index = _accounts.FindIndex(a => a.App == app && a.Key == key);
        if (index < 0)
        {
            var mine = !_accounts.Any(a => a.App == app);
            _accounts.Add(new Account(app, key, mine ? MineName : $"계정 {_accounts.Count(a => a.App == app) + 1}", mine, org));
            return true;
        }
        if (org is null || _accounts[index].Org == org) return false;
        _accounts[index] = _accounts[index] with { Org = org };
        return true;
    }

    private bool Prune(long nowMs)
    {
        var cut = nowMs - (long)Kept.TotalMilliseconds;
        var removed = false;
        foreach (var app in _periods.Select(p => p.App).Distinct().ToArray())
        {
            // The last period that began before the cut still covers it; anything before that goes.
            var older = _periods.Where(p => p.App == app && p.FromMs < cut).ToArray();
            foreach (var period in older.SkipLast(1)) removed |= _periods.Remove(period);
        }
        return removed;
    }

    private Account? Find(string app, string key) => _accounts.FirstOrDefault(a => a.App == app && a.Key == key);

    public IReadOnlyList<Account> Accounts(string app)
    {
        lock (_gate) return _accounts.Where(a => a.App == app).ToArray();
    }

    // The account the app is on now (the last one read), or null before the first reading.
    public Account? Current(string app)
    {
        lock (_gate) return _periods.LastOrDefault(p => p.App == app) is { } last ? Find(app, last.Key) : null;
    }

    public Account? MineOf(string app)
    {
        lock (_gate) return _accounts.FirstOrDefault(a => a.App == app && a.Mine);
    }

    // The account the app was on at a moment: the period it falls in, or the earliest one for a moment before them all.
    public Account? At(string app, long ms)
    {
        lock (_gate)
        {
            Period? found = null, earliest = null;
            foreach (var period in _periods.Where(p => p.App == app))
            {
                if (earliest is null || period.FromMs < earliest.FromMs) earliest = period;
                if (period.FromMs <= ms && (found is null || period.FromMs >= found.FromMs)) found = period;
            }
            found ??= earliest;
            return found is null ? null : Find(app, found.Key);
        }
    }

    // Whether a moment counts for the user's own week: true when no account is known for it.
    public bool IsMine(string app, long ms) => At(app, ms)?.Mine ?? true;

    // The user's account is in use now (or nothing is known yet).
    public bool MineNow(string app) => Current(app)?.Mine ?? true;

    public void Rename(string app, string key, string name)
    {
        var text = name.Trim();
        if (text.Length == 0) return;
        lock (_gate)
        {
            var index = _accounts.FindIndex(a => a.App == app && a.Key == key);
            if (index < 0) return;
            _accounts[index] = _accounts[index] with { Name = WeeklySplit.LineName(text) };
            Save();
        }
    }

    // One account per app is the user's own.
    public void SetMine(string app, string key)
    {
        lock (_gate)
        {
            if (!_accounts.Any(a => a.App == app && a.Key == key)) return;
            for (var i = 0; i < _accounts.Count; i++)
                if (_accounts[i].App == app) _accounts[i] = _accounts[i] with { Mine = _accounts[i].Key == key };
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (_path is null || !File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), StoreJson);
            foreach (var account in file?.Accounts ?? [])
                if (account is { App: Claude or Codex, Key.Length: > 0, Name.Length: > 0 } && Find(account.App, account.Key) is null)
                    _accounts.Add(account);
            _periods.AddRange((file?.Periods ?? []).Where(p => p is { App: Claude or Codex, Key.Length: > 0 } && Find(p.App, p.Key) is not null)
                .OrderBy(p => p.FromMs));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Change("accounts-read", e.GetType().Name);
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile(1, _accounts.ToArray(), _periods.ToArray()), StoreJson));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Change("accounts-write", e.GetType().Name);
        }
    }
}

// Reads which account each app is signed in with, from the apps' own files (read-only), and gives only derived keys.
// A file is parsed again only when it changed.
internal sealed class AccountProbe
{
    private readonly Func<IEnumerable<string>> _claudeConfigs;
    private readonly string _claudeCode;
    private readonly string _codexAuth;
    private readonly Dictionary<string, (DateTime Written, long Length, string? Key, string? Org)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AccountProbe() : this(
        () => ClaudeSessionIndex.DataRoots().Select(root => Path.Combine(root, "config.json")),
        Path.Combine(ConfigHome("CLAUDE_CONFIG_DIR", null), ".claude.json"),
        Path.Combine(ConfigHome("CODEX_HOME", ".codex"), "auth.json")) { }

    internal AccountProbe(Func<IEnumerable<string>> claudeConfigs, string claudeCode, string codexAuth)
    {
        _claudeConfigs = claudeConfigs; _claudeCode = claudeCode; _codexAuth = codexAuth;
    }

    // CLAUDE_CONFIG_DIR holds .claude.json itself; without it the file sits in the user's folder, not in .claude.
    private static string ConfigHome(string variable, string? folder)
    {
        var home = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(home)) return home;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return folder is null ? profile : Path.Combine(profile, folder);
    }

    // The desktop app's account (the newest config.json of its data folders), else Claude Code's.
    public (string? Key, bool App) ClaudeInUse()
    {
        var config = _claudeConfigs().Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (config is not null && Read(config, root =>
                (root.TryGetProperty("lastKnownAccountUuid", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null, null)).Key is { } key)
            return (key, true);
        return (ClaudeCode().Key, false);
    }

    // Claude Code's terminal sign-in (the usage API's account) and its organization.
    public (string? Key, string? Org) ClaudeCode() => Read(_claudeCode, root =>
        root.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object
            ? (Text(account, "accountUuid"), Text(account, "organizationUuid")) : (null, null));

    public string? CodexInUse() => Read(_codexAuth, root =>
        (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object ? Text(tokens, "account_id") : null, null)).Key;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private (string? Key, string? Org) Read(string path, Func<JsonElement, (string? Id, string? Org)> pick)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, null);
            if (_cache.TryGetValue(path, out var cached) && cached.Written == info.LastWriteTimeUtc && cached.Length == info.Length)
                return (cached.Key, cached.Org);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var (id, org) = doc.RootElement.ValueKind == JsonValueKind.Object ? pick(doc.RootElement) : (null, null);
            var result = (id is null ? null : AccountBook.Key(id), org is null ? null : AccountBook.Key(org));
            _cache[path] = (info.LastWriteTimeUtc, info.Length, result.Item1, result.Item2);
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return (null, null); }
    }

    // The derived organization key of a raw organization ID from the usage record.
    internal static string OrgKey(string raw) => AccountBook.Key(raw);
}
