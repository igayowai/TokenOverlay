namespace TokenOverlay;

// The program's own icon (two stacked discs, drawn by docs/codex-work/claude-shots/make-icon.ps1), embedded in the exe.
internal static class TrayIcon
{
    // The size the notification area asks for at the current display scale; the plain system icon if it cannot load.
    public static Icon ForTray() => Load(SystemInformation.SmallIconSize) ?? SystemIcons.Information;

    internal static Icon? Load(Size size)
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("TokenOverlay.app.ico");
            return stream is null ? null : new Icon(stream, size);
        }
        catch (Exception e) when (e is ArgumentException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
