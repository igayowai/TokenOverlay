using System.Collections.Concurrent;

namespace TokenOverlay;

// The mark shown in the capsule is the running app's own icon, read from its executable.
// Nothing is bundled, so it always matches the installed app.
internal static class AppIconCache
{
    private static readonly ConcurrentDictionary<HostApp, string> Paths = new();
    private static readonly Dictionary<(HostApp App, int Size), Bitmap?> Icons = new();

    public static void Remember(HostApp app, string executablePath)
    {
        if (app != HostApp.None) Paths[app] = executablePath;
    }

    // UI thread only.
    public static Bitmap? Get(HostApp app, int size)
    {
        if (Icons.TryGetValue((app, size), out var cached)) return cached;
        if (!Paths.TryGetValue(app, out var path)) return null; // not seen yet; try again on a later paint
        Bitmap? bitmap = null;
        try
        {
            using var icon = Icon.ExtractIcon(path, 0, size);
            bitmap = icon?.ToBitmap();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or System.ComponentModel.Win32Exception)
        {
        }
        return Icons[(app, size)] = bitmap;
    }
}
