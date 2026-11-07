using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TokenOverlay;

// "0.3.0+abc123" from the build, for logs and bug reports.
internal static class AppVersion
{
    public static string Text { get; } =
        typeof(AppVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute { InformationalVersion: { Length: > 0 } version }, ..]
            ? version : typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "unknown";
}

// The overlay's own files (settings, caches, logs). Not under the installed folder: uninstalling deletes that
// whole folder, and these are kept.
internal static class AppPaths
{
    internal static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TokenOverlay");
}

// Who this computer is, without any account: a short ID made from the ID Windows gives the installation
// (MachineGuid), hashed so the original cannot be read back. A computer without one gets a random ID kept in
// the overlay's folder. The computer name is only for showing.
internal static class DeviceIdentity
{
    private static readonly Lazy<string> LazyId = new(Create);
    public static string Id => LazyId.Value;
    public static string Name => Environment.MachineName;

    internal static string FromMachineGuid(string guid) =>
        "pc-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TokenOverlay|" + guid.Trim().ToLowerInvariant())))[..8];

    private static string Create()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return FromMachineGuid(guid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        var path = Path.Combine(AppPaths.DataFolder, "device-id.txt");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 3 } stored && stored.StartsWith("pc-")) return stored;
            var made = "pc-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.WriteAllText(path, made);
            return made;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "pc-unknown"; }
    }
}

// A small daily log for bug reports: what the overlay saw and what went wrong, never conversation text,
// chat titles, login details or the user's folder name. Seven days are kept, each day at most 2 MB.
internal static class DiagnosticLog
{
    internal static string Folder => Path.Combine(AppPaths.DataFolder, "logs");
    private const long DayLimit = 2 * 1024 * 1024;
    private const int KeptDays = 7;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Last = new();
    private static string? _folderOverride;

    // Tests write somewhere else.
    internal static string? UseFolder(string? folder) { lock (Gate) { var previous = _folderOverride; _folderOverride = folder; Last.Clear(); return previous; } }
    private static string CurrentFolder => _folderOverride ?? Folder;

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Error(string area, string message) => Write("ERROR", area, message);
    public static void Error(string area, Exception error) =>
        Write("ERROR", area, $"{error.GetType().Name}: {error.Message}\n{error.StackTrace}");

    // Writes only when the value differs from the last one written under this key: state, not every poll.
    public static void Change(string key, string value)
    {
        lock (Gate)
        {
            if (Last.TryGetValue(key, out var previous) && previous == value) return;
            Last[key] = value;
        }
        Write("INFO", key, value);
    }

    internal static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(profile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        // The profile folder can be a cut-down form of the sign-in name (Microsoft accounts get the first five letters).
        foreach (var user in new[] { Environment.UserName, Path.GetFileName(profile.TrimEnd('\\', '/')) })
            if (user.Length >= 3) text = text.Replace(user, "%USERNAME%", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private static void Write(string level, string area, string message)
    {
        try
        {
            var now = DateTimeOffset.Now;
            var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {area}: {Scrub(message)}")
                .Replace("\r", "").Replace("\n", "\n    ") + Environment.NewLine;
            lock (Gate)
            {
                var folder = CurrentFolder;
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, $"overlay-{now:yyyyMMdd}.log");
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length >= DayLimit) return;
                if (length == 0) Prune(folder, now);
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Prune(string folder, DateTimeOffset now)
    {
        foreach (var old in Directory.EnumerateFiles(folder, "overlay-*.log"))
        {
            if (File.GetLastWriteTime(old) < now.LocalDateTime.AddDays(-KeptDays))
            {
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // "TokenOverlay-log-pc-3f9a1c2b-20261016-1930.zip" on the desktop: the kept logs and a short summary.
    public static string CreateReport(string summary, string? destination = null)
    {
        var folder = destination ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"TokenOverlay-log-{DeviceIdentity.Id}-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        lock (Gate)
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var info = zip.CreateEntry("summary.txt");
            using (var writer = new StreamWriter(info.Open(), new UTF8Encoding(true))) writer.Write(Scrub(summary));
            if (Directory.Exists(CurrentFolder))
            {
                foreach (var log in Directory.EnumerateFiles(CurrentFolder, "overlay-*.log").Order())
                {
                    var entry = zip.CreateEntry(Path.GetFileName(log));
                    using var target = entry.Open();
                    using var source = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    source.CopyTo(target);
                }
            }
            // The installer's own log shows every install and update it applied (the overlay's logs keep only a week).
            var velopack = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "velopack", "velopack_TokenOverlay.log");
            if (File.Exists(velopack))
            {
                string[] lines;
                using (var source = new FileStream(velopack, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(source)) lines = reader.ReadToEnd().Split('\n');
                var entry = zip.CreateEntry("velopack.log");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
                writer.Write(Scrub(string.Join('\n', lines.TakeLast(3000))));
            }
        }
        return path;
    }
}
