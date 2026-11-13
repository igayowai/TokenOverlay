namespace TokenOverlay;

// Windows' own snipping surfaces and common capture tools. While one of them owns the foreground the overlay
// is left untouched so it appears in the capture.
internal static class ScreenCaptureTools
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "ScreenClippingHost", "SnippingTool", "ScreenSketch", "ShareX", "Greenshot", "Lightshot", "PicPick",
        "Snagit32", "SnagitCapture", "SnagitEditor", "bandicam", "bdcam", "Flameshot", "ksnip", "GameBar", "XboxGameBar"
    };
    private static IntPtr _lastWindow;
    private static bool _lastResult;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    internal static bool IsCaptureProcess(string processName) => Names.Contains(processName);

    public static bool IsForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;
        if (window == _lastWindow) return _lastResult;
        _lastWindow = window;
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return _lastResult = IsCaptureProcess(process.ProcessName);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return _lastResult = false;
        }
    }
}
