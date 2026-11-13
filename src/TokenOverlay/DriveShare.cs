using System.Text.Json;

namespace TokenOverlay;

// How the user sorted one device in the device manager: a name of their choosing, left out (not counted at all),
// folded into another device (the same computer after a Windows reinstall got a new ID), or whose it is.
// Owner: the person using it; null for the folder's default owner. Devices with the same owner count as one "나".
internal sealed record DeviceEntry(string? Name = null, bool Excluded = false, string? MergedInto = null, string? Owner = null);

// One device as the device manager lists it. Owner: as shown (the default owner's name when none is set);
// Mine: the same owner as this device.
internal sealed record DeviceRow(string Id, string Name, bool IsThis, DateTime? LastSeenUtc, bool Excluded, string? MergedInto,
    string Owner = "", bool Mine = true);

// Another device's file read from the share: the devices of this device's owner (Person null) count as "나",
// the others as their owner. Device: one of the user's own devices by its shown name (a device merged into another
// goes under that one's); null for another person's, or for one merged into this device.
internal sealed record SharedFile(JsonDocument Document, string? Person, string? Device = null);

// The user's own devices share when they were using Codex or Claude through a folder that Google Drive for
// desktop keeps in sync, so each overlay can tell "me" (any of my devices) from "not me" on the shared account.
// Someone else on the same account can join through the same folder shared with them: their devices get them
// as owner and count as that person instead of "다른 곳".
// Every device writes only its own small file and reads the others'. Numbers only: times and percents,
// never conversation text, paths or login details. Without Google Drive nothing is shared.
// Which devices count as "mine" is kept next to them in group.json, so a change on one device reaches all.
// device: this computer's ID (DeviceIdentity), used for the file name; name: the computer name, for showing only.
internal sealed class DriveShare(string folder, string device, string? name = null)
{
    internal const string FolderName = "TokenOverlay";
    private static readonly TimeSpan Fresh = TimeSpan.FromDays(9);
    private static readonly JsonSerializerOptions GroupJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly string[] Kinds = ["codex", "claude"];

    // A device seen for the first time (its shown name), for a notice on screen.
    public static event Action<string>? DeviceAdded;

    private readonly object _gate = new();
    private readonly Dictionary<string, string[]> _others = new();
    private readonly Dictionary<string, string[]> _people = new();
    private readonly Dictionary<string, string> _shownNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _migrated = new();
    private Dictionary<string, DeviceEntry>? _group;
    private string? _defaultOwner;
    private (DateTime, long) _groupStamp;

    public string Folder => folder;
    public string Device => device;
    public string Name => name ?? device;
    // The folder holding devices\ and group.json (…\내 드라이브\TokenOverlay).
    public string BaseFolder => Path.GetDirectoryName(folder)!;
    private string GroupPath => Path.Combine(BaseFolder, "group.json");
    // The synced drive root (…\내 드라이브), or the folder the user picked, is still there.
    public bool Available => Directory.Exists(Path.GetDirectoryName(BaseFolder));

    // "연결됨 · 내 기기 2대 (DESKTOP-1, 노트북)", from the files read last time; merged devices count once.
    public string Status
    {
        get
        {
            lock (_gate)
            {
                var group = Group();
                var seen = _others.Values.SelectMany(ids => ids).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(id => !IsExcluded(group, id)).Select(id => Root(group, id))
                    .Where(id => !id.Equals(device, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(id => Shown(group, id)).Order().ToArray();
                var self = Shown(group, Root(group, device));
                var people = _people.Values.SelectMany(ids => ids).Select(id => PersonName(group, id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                return (seen.Length == 0
                    ? $"연결됨 · 이 기기만 ({self}) · 다른 기기에도 설치하면 합쳐짐"
                    : $"연결됨 · 내 기기 {seen.Length + 1}대 ({string.Join(", ", seen.Prepend(self))})")
                    + (people.Length == 0 ? "" : $" · 다른 사람 {string.Join(", ", people)}");
            }
        }
    }

    // The name devices without an owner of their own go by on someone else's screen; null until it is given.
    public string? DefaultOwner
    {
        get { lock (_gate) { Group(); return _defaultOwner; } }
    }

    // The weekly line's two names: this device, and the other device read last time ("내 다른 기기" for two or more),
    // as the device manager shows them (renamed there, or the computer name).
    public WeeklySplit.Names SplitNames()
    {
        lock (_gate)
        {
            var group = Group();
            var self = Root(group, device);
            var others = _others.Values.SelectMany(ids => ids).Where(id => !IsExcluded(group, id)).Select(id => Root(group, id))
                .Where(id => !id.Equals(self, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(Shown(group, self), others.Length == 1 ? Shown(group, others[0]) : "내 다른 기기");
        }
    }

    // <drive>:\내 드라이브\TokenOverlay\devices (or "My Drive" in English), or null when Drive is not there.
    // Without a TokenOverlay folder of its own, one someone else shared and this account added to its drive as a
    // shortcut: Drive for desktop keeps that under <drive>:\.shortcut-targets-by-id\<id>\TokenOverlay.
    public static DriveShare? Detect()
    {
        try
        {
            // Streaming mode mounts a drive letter; mirroring mode keeps the folder in the user profile.
            var parents = DriveInfo.GetDrives()
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Network && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .Append(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach (var parent in parents)
            {
                foreach (var name in new[] { "내 드라이브", "My Drive" })
                {
                    var root = Path.Combine(parent, name);
                    if (!Directory.Exists(root)) continue;
                    if (Directory.Exists(Path.Combine(root, FolderName))) return At(root);
                    return SharedTarget(parent) is { } shared ? At(shared) : At(root);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // <parent>\.shortcut-targets-by-id\<id> holding a TokenOverlay folder, or null.
    internal static string? SharedTarget(string parent)
    {
        var targets = Path.Combine(parent, ".shortcut-targets-by-id");
        if (!Directory.Exists(targets)) return null;
        return Directory.EnumerateDirectories(targets).FirstOrDefault(dir => Directory.Exists(Path.Combine(dir, FolderName)));
    }

    // The share inside a folder the user picked (or the Drive root): <folder>\TokenOverlay\devices.
    // Picking the TokenOverlay folder itself means the same.
    public static DriveShare At(string root) =>
        new(Path.Combine(PickedRoot(root), FolderName, "devices"), DeviceIdentity.Id, DeviceIdentity.Name);

    internal static string PickedRoot(string picked)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(picked);
        return Path.GetFileName(trimmed).Equals(FolderName, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(Path.Combine(trimmed, "devices")) || File.Exists(Path.Combine(trimmed, "group.json")))
            && Path.GetDirectoryName(trimmed) is { } parent ? parent : picked;
    }

    internal static string DeviceName() =>
        new string(Environment.MachineName.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());

    // kind: "codex" or "claude". Written whole through a temporary file so Drive never uploads half of it.
    public void Write(string kind, object payload)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{device}.{kind}.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload));
            File.Move(temp, path, overwrite: true);
            // The first version named the files after the computer name: this computer's old file goes.
            if (_migrated.Add(kind))
            {
                var legacy = Path.Combine(folder, $"{DeviceName()}.{kind}.json");
                if (!legacy.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy) && name == Environment.MachineName)
                {
                    File.Delete(legacy);
                    DiagnosticLog.Info("share", $"removed the old {kind} file named after the computer");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // The user's other devices' files of this kind (see ReadShared); other people's are left out.
    public List<JsonDocument> ReadOthers(string kind, DateTimeOffset now)
    {
        var result = new List<JsonDocument>();
        foreach (var file in ReadShared(kind, now))
            if (file.Person is null) result.Add(file.Document);
            else file.Document.Dispose();
        return result;
    }

    // The other devices' files of this kind written in the last nine days, minus the ones left out in the
    // device manager, each with its owner when that is someone else. A device seen for the first time joins the
    // group (with the default owner) and is announced.
    public List<SharedFile> ReadShared(string kind, DateTimeOffset now)
    {
        var result = new List<SharedFile>();
        var ids = new List<string>();
        var people = new List<string>();
        var added = new List<string>();
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(folder)) return result;
                var group = Group();
                var changed = group.TryAdd(device, new DeviceEntry());
                foreach (var path in Directory.EnumerateFiles(folder, $"*.{kind}.json"))
                {
                    var fileDevice = Path.GetFileName(path)[..^$".{kind}.json".Length];
                    if (fileDevice.Equals(device, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (now - File.GetLastWriteTimeUtc(path) > Fresh) continue;
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var document = JsonDocument.Parse(stream);
                        _shownNames[fileDevice] = PayloadName(document) ?? fileDevice;
                        if (group.TryAdd(fileDevice, new DeviceEntry())) { changed = true; added.Add(Shown(group, fileDevice)); }
                        if (IsExcluded(group, fileDevice)) { document.Dispose(); continue; }
                        if (IsMine(group, fileDevice))
                        {
                            var root = Root(group, fileDevice);
                            result.Add(new(document, null, root.Equals(device, StringComparison.OrdinalIgnoreCase) ? null : Shown(group, root)));
                            ids.Add(fileDevice);
                        }
                        else { result.Add(new(document, PersonName(group, fileDevice))); people.Add(fileDevice); }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                }
                if (changed) SaveGroup(group);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            _others[kind] = ids.ToArray();
            _people[kind] = people.ToArray();
        }
        foreach (var shown in added)
        {
            DiagnosticLog.Info("share", "new device joined");
            DeviceAdded?.Invoke(shown);
        }
        return result;
    }

    // Every device that ever wrote here, this one first, with how the user sorted it.
    public IReadOnlyList<DeviceRow> ListDevices()
    {
        lock (_gate)
        {
            var group = Group();
            var seen = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var kind in Kinds)
                    {
                        foreach (var path in Directory.EnumerateFiles(folder, $"*.{kind}.json"))
                        {
                            var id = Path.GetFileName(path)[..^$".{kind}.json".Length];
                            var at = File.GetLastWriteTimeUtc(path);
                            if (!seen.TryGetValue(id, out var known) || at > known) seen[id] = at;
                            if (!_shownNames.ContainsKey(id)) _shownNames[id] = ReadName(path) ?? id;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var all = seen.Keys.Concat(group.Keys).Append(device).Distinct(StringComparer.OrdinalIgnoreCase);
            return all.Select(id => new DeviceRow(id, Shown(group, id), id.Equals(device, StringComparison.OrdinalIgnoreCase),
                    seen.TryGetValue(id, out var at) ? at : null, IsExcluded(group, id), group.GetValueOrDefault(id)?.MergedInto,
                    PersonName(group, id), IsMine(group, id)))
                .OrderByDescending(row => row.IsThis).ThenByDescending(row => row.LastSeenUtc).ToArray();
        }
    }

    public void Rename(string id, string? shown) => Change(id, entry => entry with { Name = string.IsNullOrWhiteSpace(shown) ? null : shown.Trim() });

    // Whose device it is; empty (or the default owner's own name) puts it back with the default owner.
    public void SetOwner(string id, string? owner)
    {
        var shown = ShownName(owner);
        lock (_gate)
        {
            Group();
            if (shown is not null && shown.Equals(_defaultOwner, StringComparison.OrdinalIgnoreCase)) shown = null;
        }
        Change(id, entry => entry with { Owner = shown });
    }

    public void SetDefaultOwner(string? owner)
    {
        lock (_gate)
        {
            var group = Group();
            _defaultOwner = ShownName(owner);
            SaveGroup(group);
        }
    }

    public void SetExcluded(string id, bool excluded)
    {
        if (id.Equals(device, StringComparison.OrdinalIgnoreCase)) return;
        Change(id, entry => entry with { Excluded = excluded });
    }

    // The same computer under two IDs: the older one is shown and counted as the newer one.
    public void Merge(string from, string into)
    {
        if (from.Equals(into, StringComparison.OrdinalIgnoreCase)) return;
        lock (_gate)
        {
            var group = Group();
            // Whatever pointed at the old one now points at the new one, so no chain is left.
            foreach (var (id, entry) in group.ToArray())
                if (entry.MergedInto?.Equals(from, StringComparison.OrdinalIgnoreCase) == true) group[id] = entry with { MergedInto = into };
            group[from] = (group.GetValueOrDefault(from) ?? new DeviceEntry()) with { MergedInto = into };
            group.TryAdd(into, new DeviceEntry());
            SaveGroup(group);
        }
    }

    public void Unmerge(string id) => Change(id, entry => entry with { MergedInto = null });

    // Takes a device off the list and removes its files here; if it is still running it simply comes back.
    public void Remove(string id)
    {
        // group.json is written by every device: an id that is not a plain file name never reaches the delete.
        if (id.Equals(device, StringComparison.OrdinalIgnoreCase) || !IsPlainId(id)) return;
        lock (_gate)
        {
            foreach (var kind in Kinds)
            {
                var path = Path.Combine(folder, $"{id}.{kind}.json");
                try { if (File.Exists(path)) File.Delete(path); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            var group = Group();
            group.Remove(id);
            foreach (var (other, entry) in group.ToArray())
                if (entry.MergedInto?.Equals(id, StringComparison.OrdinalIgnoreCase) == true) group[other] = entry with { MergedInto = null };
            _shownNames.Remove(id);
            SaveGroup(group);
        }
    }

    private void Change(string id, Func<DeviceEntry, DeviceEntry> change)
    {
        lock (_gate)
        {
            var group = Group();
            group[id] = change(group.GetValueOrDefault(id) ?? new DeviceEntry());
            SaveGroup(group);
        }
    }

    private string Shown(Dictionary<string, DeviceEntry> group, string id) =>
        ShownName(group.GetValueOrDefault(id)?.Name)
        ?? (id.Equals(device, StringComparison.OrdinalIgnoreCase) ? Name : _shownNames.GetValueOrDefault(id) ?? id);

    private static string Root(Dictionary<string, DeviceEntry> group, string id) =>
        group.GetValueOrDefault(id)?.MergedInto is { Length: > 0 } into ? into : id;

    private static bool IsExcluded(Dictionary<string, DeviceEntry> group, string id) =>
        group.GetValueOrDefault(id)?.Excluded == true || group.GetValueOrDefault(Root(group, id))?.Excluded == true;

    // A merged device goes with the owner of the one it was merged into.
    private static string? Owner(Dictionary<string, DeviceEntry> group, string id) =>
        ShownName(group.GetValueOrDefault(Root(group, id))?.Owner);

    private bool IsMine(Dictionary<string, DeviceEntry> group, string id) =>
        string.Equals(Owner(group, id), Owner(group, device), StringComparison.OrdinalIgnoreCase);

    // Callers hold _gate, after Group().
    private string PersonName(Dictionary<string, DeviceEntry> group, string id) => Owner(group, id) ?? _defaultOwner ?? "공유 폴더 주인";

    internal static bool IsPlainId(string id) =>
        id.Length > 0 && id == Path.GetFileName(id) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id is not ("." or "..");

    // Another device's own name for itself, as shown in menus and notices: one line, at most 40 characters.
    private static string? PayloadName(JsonDocument document) =>
        document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("name", out var shown)
        && shown.ValueKind == JsonValueKind.String && ShownName(shown.GetString()) is { } text ? text : null;

    internal static string? ShownName(string? name)
    {
        var text = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length == 0 ? null : text.Length <= 40 ? text : text[..39] + "…";
    }

    private static string? ReadName(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return PayloadName(document);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // group.json, read again only when another device changed it. Callers hold _gate.
    private Dictionary<string, DeviceEntry> Group()
    {
        try
        {
            var info = new FileInfo(GroupPath);
            var stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
            if (_group is not null && stamp == _groupStamp) return _group;
            var loaded = info.Exists ? JsonSerializer.Deserialize<GroupFile>(File.ReadAllText(GroupPath), GroupJson) : null;
            _group = new Dictionary<string, DeviceEntry>(loaded?.Devices ?? [], StringComparer.OrdinalIgnoreCase);
            _defaultOwner = ShownName(loaded?.DefaultOwner);
            _groupStamp = stamp;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _group ??= new Dictionary<string, DeviceEntry>(StringComparer.OrdinalIgnoreCase);
        }
        return _group;
    }

    private void SaveGroup(Dictionary<string, DeviceEntry> group)
    {
        try
        {
            Directory.CreateDirectory(BaseFolder);
            var temp = GroupPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new GroupFile(1, group, _defaultOwner), GroupJson));
            File.Move(temp, GroupPath, overwrite: true);
            var info = new FileInfo(GroupPath);
            _group = group;
            _groupStamp = (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Change("share-group", e.Message); }
    }

    // DefaultOwner: the name of the devices without an owner, as other people's overlays show them.
    private sealed record GroupFile(int Version, Dictionary<string, DeviceEntry>? Devices, string? DefaultOwner = null);

    // [[a, b, c], …] under the given property → rows of numbers; anything else is skipped.
    public static IEnumerable<long[]> Rows(JsonDocument document, string property)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) yield break;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array) continue;
            var values = new List<long>();
            foreach (var cell in row.EnumerateArray())
                if (cell.ValueKind == JsonValueKind.Number && cell.TryGetInt64(out var n)) values.Add(n);
            yield return values.ToArray();
        }
    }

    // Which device a shared file came from, for bug reports: its name, else its id.
    public static string Label(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return "?";
        foreach (var property in new[] { "name", "device" })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text;
        return "?";
    }

    // Unix ms → local "MM-dd HH:mm" for bug reports; 0 → "-".
    public static string When(long ms) => ms <= 0 ? "-" : DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm");
}

// Keeps the share connected on its own: while Google Drive is missing (it may start after the overlay at sign-in,
// or its drive went away) it is looked for again once a minute. Switched off, nothing is written or read.
// A folder picked in the device manager replaces the Google Drive root.
internal sealed class DriveShareLink(Func<DriveShare?> detect, bool enabled, string? folderOverride = null)
{
    private const long RetryMs = 60_000;
    private readonly object _lock = new();
    private DriveShare? _current;
    private long? _nextMs;
    private volatile bool _enabled = enabled;
    private volatile string? _folderOverride = folderOverride;

    public DriveShareLink(bool enabled, string? folderOverride) : this(DriveShare.Detect, enabled, folderOverride) { }

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; lock (_lock) _nextMs = null; }
    }

    // Null: the Google Drive root found on its own.
    public string? FolderOverride
    {
        get => _folderOverride;
        set { _folderOverride = string.IsNullOrWhiteSpace(value) ? null : value; lock (_lock) { _current = null; _nextMs = null; } }
    }

    public DriveShare? Current()
    {
        if (!_enabled) return null;
        lock (_lock)
        {
            var now = Environment.TickCount64;
            if (_current is { Available: false }) { _current = null; _nextMs = null; }
            if (_current is null && (_nextMs is not long next || now >= next))
            {
                _current = _folderOverride is { } chosen ? (Directory.Exists(chosen) ? DriveShare.At(chosen) : null) : detect();
                _nextMs = now + RetryMs;
                DiagnosticLog.Change("share", _current is null ? "share folder not found" : "connected · " + _current.Folder);
            }
            return _current;
        }
    }

    public string Status
    {
        get
        {
            if (!_enabled) return "꺼짐 · 이 PC 사용만 '나'로 셈";
            lock (_lock)
                return _current is { } share ? share.Status
                    : _folderOverride is null ? "구글 드라이브 찾는 중… (데스크톱 앱이 켜지면 자동 연결)" : "지정한 공유 폴더를 찾는 중…";
        }
    }
}
