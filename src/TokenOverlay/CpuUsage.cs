using System.Runtime.InteropServices;

namespace TokenOverlay;

// The meters' readings: the whole PC's processor use and memory in use (the third bar, the graphics card, is GpuSensor). Processor use is read once
// a second the way Task Manager reads it ("% Processor Utility", which counts
// clock boost and can pass 100, shown capped at 100). Where that counter is missing, Windows' running totals of idle,
// kernel and user time stand in ("% Processor Time"; kernel time includes idle time).
internal sealed class CpuUsage : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public double Double;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PdhValue value);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private IntPtr _query;
    private IntPtr _counter;
    private bool _pdhTried;
    private int _opening;
    private bool _disposed;
    private (long Idle, long Total)? _last;

    // The share in use since the previous call (0 to 1); null until the counter is open, on the first reading, or when
    // Windows gives no reading.
    public double? Sample()
    {
        if (!Volatile.Read(ref _pdhTried))
        {
            // Opening the counter takes 0.5–0.9 s here, longer right after Windows starts. It is opened on a worker:
            // done inside the first Tick it held back the first poll, so the first numbers waited for it (2026-11-21).
            if (Interlocked.Exchange(ref _opening, 1) == 0) Task.Run(OpenCounter);
            return null;
        }
        if (_query != IntPtr.Zero)
        {
            // The first collection only sets the starting point.
            if (PdhCollectQueryData(_query) == 0 && PdhGetFormattedCounterValue(_counter, PdhFmtDouble, IntPtr.Zero, out var value) == 0
                && value.Status is 0 or 1) return Math.Clamp(value.Double / 100, 0, 1);
            return null;
        }
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (idle, kernel + user);
        var last = _last;
        _last = now;
        return last is { } before ? Between(before, now) : null;
    }

    private void OpenCounter()
    {
        var query = IntPtr.Zero;
        var counter = IntPtr.Zero;
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out query) == 0
                && PdhAddEnglishCounter(query, @"\Processor Information(_Total)\% Processor Utility", IntPtr.Zero, out counter) != 0)
            {
                PdhCloseQuery(query);
                query = IntPtr.Zero;
            }
            // The first collection only sets the starting point; taken here, the next Sample already has a reading.
            if (query != IntPtr.Zero) PdhCollectQueryData(query);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { query = IntPtr.Zero; }
        lock (this)
        {
            if (_disposed) { if (query != IntPtr.Zero) PdhCloseQuery(query); return; }
            _counter = counter;
            _query = query;
            Volatile.Write(ref _pdhTried, true);
        }
    }

    internal static double? Between((long Idle, long Total) before, (long Idle, long Total) after)
    {
        var total = after.Total - before.Total;
        if (total <= 0) return null;
        return Math.Clamp(1 - (double)(after.Idle - before.Idle) / total, 0, 1);
    }

    public void Dispose()
    {
        lock (this)
        {
            _disposed = true;
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    // Memory in use, as Task Manager counts it: physical memory less what is available (0 to 1).
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public static double? Memory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
        return Math.Clamp(1 - (double)status.AvailPhys / status.TotalPhys, 0, 1);
    }
}
