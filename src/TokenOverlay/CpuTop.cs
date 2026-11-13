using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace TokenOverlay;

// The line over the meters (2026-11-09, the user's ask): the program group that used the most processor time over the
// last two seconds, and the Claude Code session that started it. A group is a program with all it started, up to the
// program the shell (explorer) or a service host started; a process under a session's claude.exe belongs to that
// session, which its command line names (`--resume <id>`). A session the desktop app started fresh carries no id there
// and reads "이름 모름" until its mod names its process. Work left running after the shell that started it exited has
// no parent leading to a session: its head's environment names the session (CLAUDE_CODE_SESSION_ID, the user's choice
// on 2026-11-10). Read without elevation, from the user's own processes; a command line or an environment is searched
// for the session id alone and dropped.
internal sealed partial class CpuTop : IDisposable
{
    internal static readonly TimeSpan Every = TimeSpan.FromSeconds(2);
    internal const string UnknownSession = "이름 모름";

    // A process as one reading saw it. Used: processor time since the reading before (100 ns), -1 when not known.
    // Session: null for a process that is not a Claude Code session, "" for one without an id, else its id.
    internal readonly record struct Proc(int Pid, int Parent, string Exe, long Created, long Used, string? Session);

    // Started by the system or the shell: a group stops under these.
    private static readonly HashSet<string> Roots = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "smss.exe", "csrss.exe", "System", "[System Process]", "userinit.exe",
    };

    // The line for one reading: the heaviest group's program (for a session, its heaviest process) and its share of the
    // whole processor, then the session's name. Null when nothing used the processor.
    internal static string? Line(IReadOnlyList<Proc> procs, long capacity, Func<string, string?> titleOf) => Line(procs, capacity, titleOf, out _);

    internal static string? Line(IReadOnlyList<Proc> procs, long capacity, Func<string, string?> titleOf, out int heaviest)
    {
        heaviest = 0;
        if (capacity <= 0) return null;
        var byPid = new Dictionary<int, Proc>();
        foreach (var p in procs) byPid[p.Pid] = p;
        var groups = new Dictionary<string, (long Used, int Root, string? Session, Proc Heaviest)>();
        foreach (var p in procs)
        {
            if (p.Used <= 0 || p.Pid is 0 or 4) continue;
            var (key, root, session) = GroupOf(p, byPid);
            groups.TryGetValue(key, out var g);
            groups[key] = (g.Used + p.Used, root, session, g.Heaviest.Used >= p.Used && g.Heaviest.Exe is not null ? g.Heaviest : p);
        }
        if (groups.Count == 0) return null;
        var top = groups.Values.MaxBy(g => g.Used);
        heaviest = top.Heaviest.Pid;
        var share = $"{(int)Math.Round(100.0 * top.Used / capacity)}%";
        // The heaviest process names the group too: a program whose launcher outlived its parent (`timeout` started from
        // a shell that has exited, 2026-11-09) heads its group, and its name says nothing of what is busy.
        if (top.Session is null) return $"{Name(top.Heaviest.Exe)} {share}";
        var name = top.Session.Length == 0 ? UnknownSession : titleOf(top.Session) ?? top.Session[..Math.Min(8, top.Session.Length)];
        return $"{Name(top.Heaviest.Exe)} {share} · {name}";
    }

    private static (string Key, int Root, string? Session) GroupOf(Proc p, Dictionary<int, Proc> byPid)
    {
        var node = p;
        for (var guard = 0; guard < 64; guard++)
        {
            if (node.Session is { } session) return (session.Length == 0 ? $"s?{node.Pid}" : "s:" + session, node.Pid, session);
            // A parent gone, a system root, or a pid reused by a later process: this one heads the group.
            if (!byPid.TryGetValue(node.Parent, out var parent) || parent.Pid is 0 or 4 || Roots.Contains(parent.Exe)
                || node.Parent == node.Pid || parent.Created > node.Created && node.Created > 0)
                return ("p:" + node.Pid, node.Pid, null);
            node = parent;
        }
        return ("p:" + node.Pid, node.Pid, null);
    }

    // The heads of busy groups whose parent is gone (or its pid taken by a later process): work a Claude session started
    // from a shell that has since exited (`timeout … python`, 2026-11-10), which the parents no longer tie to it.
    // Each head with its group's busiest process: Git's own tools (timeout, bash) keep their environment inside their
    // runtime, so the Windows programs they start carry the variables and the head may not (seen 2026-11-10).
    internal static Dictionary<int, int> Orphans(IReadOnlyList<Proc> procs)
    {
        var byPid = new Dictionary<int, Proc>();
        foreach (var p in procs) byPid[p.Pid] = p;
        var heads = new Dictionary<int, int>();
        var orphan = new Dictionary<int, bool>();
        foreach (var p in procs)
        {
            if (p.Used <= 0 || p.Pid is 0 or 4) continue;
            var (_, root, session) = GroupOf(p, byPid);
            if (session is not null) continue;
            if (!orphan.TryGetValue(root, out var isOrphan))
            {
                var head = byPid[root];
                // The shell itself outlives its starter (userinit) and is no session's work.
                isOrphan = !Roots.Contains(head.Exe)
                    && (!byPid.TryGetValue(head.Parent, out var parent) || parent.Created > head.Created && head.Created > 0);
                orphan[root] = isOrphan;
            }
            if (isOrphan && (!heads.TryGetValue(root, out var busiest) || byPid[busiest].Used < p.Used)) heads[root] = p.Pid;
        }
        return heads;
    }

    private static string Name(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

    // A program the line shows by its logo, not its name (the user's ask, 2026-11-10): Python, whose logo its own
    // python.exe carries. Its forms: python, pythonw, python3, python3.14.
    internal static bool HasLogo(string program) => LogoName().IsMatch(program);

    [System.Text.RegularExpressions.GeneratedRegex(@"^pythonw?(\d+(\.\d+)?)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LogoName();

    // A Claude Code session's claude.exe (the desktop app's own claude.exe processes take no --output-format) and the
    // session it resumes.
    internal static string? SessionOf(string commandLine)
    {
        if (!commandLine.Contains("--output-format", StringComparison.Ordinal)) return null;
        var id = Resume().Match(commandLine);
        return id.Success ? id.Groups[1].Value : "";
    }

    [GeneratedRegex(@"--resume\s+""?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})")]
    private static partial Regex Resume();

    // ----- Reading Windows.
    private sealed class Tracked
    {
        public required SafeProcessHandle Handle;
        public long Created;
        public long Cpu;
        public string? Session;
        // The last readings' processor time, so the line follows the last six seconds and not each two.
        public readonly long[] Recent = new long[Window];
        // The session named by an orphan's environment: not read yet (null), none (""), or its id.
        public string? Environment;
        // The program file, read for a program shown by its logo: not read yet (null), unknown (""), or its path.
        public string? Image;
    }

    private const int Window = 3;
    private readonly long[] _capacities = new long[Window];
    private int _slot;

    private readonly Dictionary<int, Tracked> _tracked = new();
    private readonly ClaudeSessionIndex _index = new();
    private readonly System.Threading.Timer? _timer;
    private long _lastAt;
    private int _busy;
    private volatile string? _text;
    private volatile string? _logo;

    public bool Enabled { get; set; }
    // The last line read; null before the second reading or when nothing used the processor.
    public string? Text => _text;
    // The program file whose icon stands for the line's first word, when that program is shown by its logo.
    public string? Logo => _logo;

    public CpuTop(bool start = true)
    {
        if (start) _timer = new System.Threading.Timer(_ => Tick(), null, Every, Every);
    }

    private void Tick()
    {
        if (!Enabled)
        {
            if (_tracked.Count > 0 && Interlocked.Exchange(ref _busy, 1) == 0) { try { Forget(); } finally { Volatile.Write(ref _busy, 0); } }
            return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try { _text = Sample(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Forget()
    {
        foreach (var t in _tracked.Values) t.Handle.Dispose();
        _tracked.Clear();
        _lastAt = 0;
        Array.Clear(_capacities);
        _text = null;
        _logo = null;
    }

    internal string? Sample()
    {
        var at = Stopwatch.GetTimestamp();
        var seen = new HashSet<int>();
        var procs = new List<Proc>();
        _slot = (_slot + 1) % Window;
        foreach (var (pid, parent, exe) in Snapshot())
        {
            seen.Add(pid);
            if (pid is 0 or 4) { procs.Add(new(pid, parent, exe, 0, -1, null)); continue; }
            if (_tracked.TryGetValue(pid, out var t) && Times(t.Handle) is { } now && now.Created == t.Created)
            {
                t.Recent[_slot] = Math.Max(0, now.Cpu - t.Cpu);
                t.Cpu = now.Cpu;
                procs.Add(new(pid, parent, exe, t.Created, t.Recent.Sum(), t.Session));
                continue;
            }
            if (t is not null) { t.Handle.Dispose(); _tracked.Remove(pid); }
            var opened = Open(pid, exe);
            if (opened is null) { procs.Add(new(pid, parent, exe, 0, -1, null)); continue; }
            _tracked[pid] = opened;
            procs.Add(new(pid, parent, exe, opened.Created, -1, opened.Session));
        }
        foreach (var gone in _tracked.Keys.Where(k => !seen.Contains(k)).ToArray()) { _tracked[gone].Handle.Dispose(); _tracked.Remove(gone); }
        // A session without an id on its command line: its mod (build 9 on) names its process in its report.
        if (procs.Any(p => p.Session is ""))
        {
            var named = SessionPids(DateTimeOffset.Now);
            for (var i = 0; i < procs.Count; i++)
                if (procs[i].Session is "" && named.TryGetValue(procs[i].Pid, out var id)) procs[i] = procs[i] with { Session = id };
        }
        // Work whose parents no longer lead to a session: the session id it inherited, from its environment (the user's
        // choice, 2026-11-10). Read once per process, only for such a group's head.
        foreach (var (head, busiest) in Orphans(procs))
        {
            string Read(int pid) => _tracked.TryGetValue(pid, out var t) ? t.Environment ??= SessionFromEnvironment(pid) ?? "" : "";
            var session = Read(head) is { Length: > 0 } own ? own : Read(busiest);
            if (session.Length == 0) continue;
            var i = procs.FindIndex(p => p.Pid == head);
            procs[i] = procs[i] with { Session = session };
        }
        var last = _lastAt;
        _lastAt = at;
        if (last == 0) return null;
        // Processor time the whole PC had over the same readings, in 100 ns.
        _capacities[_slot] = (long)((at - last) * 10_000_000.0 / Stopwatch.Frequency) * System.Environment.ProcessorCount;
        var line = Line(procs, _capacities.Sum(), id => _index.TitleOf(id), out var heaviest);
        // Written before the line: the form draws a logo only for a line whose first word has one.
        _logo = line is not null && _tracked.TryGetValue(heaviest, out var busy) && HasLogo(Name(procs.Find(p => p.Pid == heaviest).Exe ?? ""))
            ? (busy.Image ??= ImagePath(busy.Handle) ?? "") is { Length: > 0 } image ? image : null
            : null;
        return line;
    }

    // Process id → session id, from the mods' reports written in the last two minutes (read again every 30 seconds).
    private Dictionary<int, string> _sessionPids = new();
    private DateTimeOffset _sessionPidsAt;

    private Dictionary<int, string> SessionPids(DateTimeOffset now, string? bridgeFolder = null)
    {
        if (now - _sessionPidsAt < TimeSpan.FromSeconds(30)) return _sessionPids;
        _sessionPidsAt = now;
        _sessionPids = ReadSessionPids(bridgeFolder ?? CacheWatch.BridgeFolder, now);
        return _sessionPids;
    }

    internal static Dictionary<int, string> ReadSessionPids(string bridgeFolder, DateTimeOffset now)
    {
        var found = new Dictionary<int, string>();
        try
        {
            var folder = new DirectoryInfo(bridgeFolder);
            if (!folder.Exists) return found;
            foreach (var report in folder.EnumerateFiles("*.json"))
            {
                if (now.UtcDateTime - report.LastWriteTimeUtc >= TimeSpan.FromMinutes(2)) continue;
                try
                {
                    using var stream = new FileStream(report.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var doc = System.Text.Json.JsonDocument.Parse(stream);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) && pid > 0
                        && r.TryGetProperty("sessionId", out var s) && s.GetString() is { } id && Guid.TryParse(id, out _))
                        found[pid] = id;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    // CLAUDE_CODE_SESSION_ID from a process's environment, which every program a Claude Code session starts inherits.
    // The block also holds other variables (a messaging token among them): it is searched as bytes for that one name,
    // never turned into text, and wiped; nothing of it is kept, written or sent. A 64-bit process of this user only.
    internal static string? SessionFromEnvironment(int pid)
    {
        if (!System.Environment.Is64BitProcess) return null;
        using var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (handle.IsInvalid || !IsWow64Process(handle, out var wow) || wow) return null;
        var basic = new ProcessBasicInformation();
        if (NtQueryInformationProcess(handle, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 || basic.PebBaseAddress == IntPtr.Zero)
            return null;
        // PEB.ProcessParameters at 0x20; RTL_USER_PROCESS_PARAMETERS.Environment at 0x80, EnvironmentSize at 0x3F0 (x64).
        if (ReadPointer(handle, basic.PebBaseAddress + 0x20) is not { } parameters
            || ReadPointer(handle, parameters + 0x80) is not { } environment
            || ReadPointer(handle, parameters + 0x3F0) is not { } size) return null;
        var length = (int)Math.Clamp(size.ToInt64(), 0, 1 << 20);
        if (length == 0) return null;
        var block = new byte[length];
        try
        {
            if (!ReadProcessMemory(handle, environment, block, length, out var read) || read <= 0) return null;
            return FindSession(block.AsSpan(0, (int)read));
        }
        finally { Array.Clear(block); }
    }

    private static readonly byte[] SessionName = System.Text.Encoding.Unicode.GetBytes("CLAUDE_CODE_SESSION_ID=");

    // The variable's value in a UTF-16 environment block ("NAME=value\0…\0\0"), when it is a session id.
    internal static string? FindSession(ReadOnlySpan<byte> block)
    {
        for (var at = 0; at + SessionName.Length + 72 <= block.Length; at += 2)
        {
            if ((at > 0 && (block[at - 1] != 0 || block[at - 2] != 0)) || !block[at..].StartsWith(SessionName)) continue;
            var value = block.Slice(at + SessionName.Length, 72);
            Span<char> id = stackalloc char[36];
            for (var i = 0; i < 36; i++)
            {
                var c = (char)(value[2 * i] | value[2 * i + 1] << 8);
                if (!(char.IsAsciiHexDigit(c) || c == '-')) return null;
                id[i] = c;
            }
            return Guid.TryParse(id, out _) ? new string(id) : null;
        }
        return null;
    }

    private static IntPtr? ReadPointer(SafeProcessHandle handle, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        return ReadProcessMemory(handle, address, buffer, buffer.Length, out var read) && read == buffer.Length
            ? (IntPtr)BitConverter.ToInt64(buffer) : null;
    }

    private static Tracked? Open(int pid, string exe)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        if (Times(handle) is not { } times) { handle.Dispose(); return null; }
        string? session = null;
        if (exe.Equals("claude.exe", StringComparison.OrdinalIgnoreCase) && CommandLine(handle) is { } line) session = SessionOf(line);
        return new Tracked { Handle = handle, Created = times.Created, Cpu = times.Cpu, Session = session };
    }

    private static string? ImagePath(SafeProcessHandle handle)
    {
        var buffer = new char[1024];
        var size = buffer.Length;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
    }

    private static (long Created, long Cpu)? Times(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var created, out _, out var kernel, out var user) ? (created, kernel + user) : null;

    private static string? CommandLine(SafeProcessHandle handle)
    {
        // ProcessCommandLineInformation (Windows 8.1 on): a UNICODE_STRING followed by its text.
        var size = 4096;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(handle, 60, buffer, size, out var needed);
                if (status == 0)
                {
                    var length = (ushort)Marshal.ReadInt16(buffer);
                    var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
                }
                if (needed <= size) return null;
                size = needed;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    private static IEnumerable<(int Pid, int Parent, string Exe)> Snapshot()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) yield break;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) yield break;
            do yield return ((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile);
            while (Process32NextW(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        // A reading under way keeps its handles; Windows closes them when the overlay exits.
        if (Interlocked.Exchange(ref _busy, 1) == 0) Forget();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, IntPtr address, byte[] buffer, nint size, out nint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(SafeProcessHandle process, out bool wow64);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, char[] name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, IntPtr info, int length, out int returned);
}
