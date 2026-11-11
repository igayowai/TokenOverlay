using System.Globalization;
using System.Runtime.InteropServices;

namespace TokenOverlay;

// The third meter (the user's ask, 2026-11-10, in place of the commit charge): the graphics card's fan speed fills the
// bar and its temperature sits at the foot. Read through NVIDIA's own library (nvml.dll, installed with the driver
// in System32), the way nvidia-smi reads them, without starting a program each second. Only the first NVIDIA card is
// read. A PC without one, or a card that reports no fan, shows an empty bar or "—".
internal sealed class GpuSensor : IDisposable
{
    private const int Success = 0;
    private const int TemperatureGpu = 0;

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int Shutdown();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int DeviceByIndex(uint index, out IntPtr device);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetFanSpeed")]
    private static extern int FanSpeed(IntPtr device, out uint percent);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int Temperature(IntPtr device, int sensor, out uint celsius);

    private IntPtr _device;
    private bool _open;
    // When to try the library again after it failed to open or stopped answering (a driver update, no NVIDIA card).
    private long _retryAt;

    // The fan speed and temperature now; null when neither can be read.
    public GpuReading? Read()
    {
        try
        {
            if (!_open && !Open()) return null;
            int? fan = FanSpeed(_device, out var percent) == Success ? (int)percent : null;
            int? temp = Temperature(_device, TemperatureGpu, out var celsius) == Success ? (int)celsius : null;
            if (fan is null && temp is null)
            {
                Close();
                _retryAt = Environment.TickCount64 + 30_000;
                return null;
            }
            return new GpuReading(fan, temp);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _retryAt = long.MaxValue;
            return null;
        }
    }

    private bool Open()
    {
        if (Environment.TickCount64 < _retryAt) return false;
        if (Init() != Success)
        {
            _retryAt = Environment.TickCount64 + 30_000;
            return false;
        }
        if (DeviceByIndex(0, out _device) != Success)
        {
            Shutdown();
            _retryAt = Environment.TickCount64 + 30_000;
            return false;
        }
        _open = true;
        return true;
    }

    private void Close()
    {
        if (!_open) return;
        _open = false;
        _device = IntPtr.Zero;
        try { Shutdown(); } catch (DllNotFoundException) { }
    }

    public void Dispose() => Close();
}

// The fan speed (percent of its top speed, which NVIDIA lets pass 100 on some cards; the bar stops at full) and the
// temperature in whole degrees Celsius.
internal readonly record struct GpuReading(int? Fan, int? Temp)
{
    public double? Fill => Fan is int f ? Math.Clamp(f, 0, 100) / 100.0 : null;

    // The bar's foot: the temperature as a whole number with "°" (the user's choice, 2026-11-10: "온도 뒤 ° 표시").
    public static string Label(GpuReading? gpu) =>
        gpu?.Temp is int t ? t.ToString(CultureInfo.InvariantCulture) + "°" : "—";
}
