using System.Diagnostics;
using System.Text.Json;

namespace TokenOverlay;

internal sealed record CodexHostIdentity(long Handle, uint ProcessId);

internal sealed class CodexLaunchGate
{
    private readonly HashSet<CodexHostIdentity> _handled = new();
    public bool Observe(IReadOnlyList<CodexHostIdentity> open, Func<CodexHostIdentity, bool> alive, bool overlayRunning)
    {
        _handled.RemoveWhere(h => !alive(h));
        var fresh = open.Any(h => !_handled.Contains(h));
        _handled.UnionWith(open);
        return fresh && !overlayRunning;
    }
}

// When a downloaded update may restart the overlay by itself: after a minute in which no turn ran and nothing
// was being moved, listed or opened. Anything busy starts the minute again.
internal sealed class AutoUpdateGate
{
    internal static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(1);
    private DateTime? _quietSince;

    public bool Observe(bool ready, bool busy, DateTime now)
    {
        if (busy) { _quietSince = null; return false; }
        _quietSince ??= now;
        return ready && now - _quietSince.Value >= QuietFor;
    }
}

internal static class CodexLaunchWatcher
{
    private const string WatcherMutex = @"Local\TokenOverlayWatcher";
    internal const string OverlayMutex = @"Local\TokenOverlay";
    // Set by the overlay before an update swaps the program files, so the watcher lets go of them.
    private const string StopEvent = @"Local\TokenOverlayWatcherStop";

    public static void RequestStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEvent, out var stop)) return;
        using (stop) stop.Set();
        for (var i = 0; i < 60 && Exists(WatcherMutex); i++) Thread.Sleep(50);
    }

    public static bool Exists(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    public static void EnsureRunning()
    {
        if (!StartupRegistration.IsEnabled() || Exists(WatcherMutex)) return;
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            Arguments = "--watch-codex", UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void Run()
    {
        using var mutex = new Mutex(true, WatcherMutex, out var created);
        if (!created) return;
        var gate = new CodexLaunchGate();
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEvent);
        while (StartupRegistration.IsEnabled())
        {
            var hosts = CodexWindowLocator.FindOpenMainWindows();
            if (gate.Observe(hosts, CodexWindowLocator.IsHostAlive, Exists(OverlayMutex)))
            {
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
                {
                    // The watcher keeps going; the failure goes to the log a bug report collects.
                    DiagnosticLog.Error("watcher", e);
                }
            }
            if (stop.WaitOne(2000)) break;
        }
        GC.KeepAlive(mutex);
    }

    public static object Test()
    {
        var passed = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed.Add(name);
        }
        var gate = new CodexLaunchGate();
        using var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopEvent);
        var first = new CodexHostIdentity(100, 1);
        var second = new CodexHostIdentity(200, 2);
        Check(!gate.Observe([], _ => false, false), "Codex absent: wait without overlay");
        Check(gate.Observe([first], _ => true, false), "Codex opened: launch overlay");
        Check(!gate.Observe([first], _ => true, true), "running overlay: no duplicate");
        Check(!gate.Observe([first], _ => true, false), "manual overlay exit: no immediate resurrection");
        Check(!gate.Observe([], _ => true, false), "minimized living window: keep handled state");
        Check(!gate.Observe([first], _ => true, false), "restored same window: respect manual exit");
        Check(!gate.Observe([], _ => false, false), "Codex closed: clear old identity");
        Check(gate.Observe([second], _ => true, false), "Codex reopened: launch again");
        var update = new AutoUpdateGate();
        var t0 = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        Check(!update.Observe(true, false, t0) && !update.Observe(true, false, t0.AddSeconds(59)), "update: waits a quiet minute first");
        Check(update.Observe(true, false, t0.AddSeconds(60)), "update: applied after a quiet minute");
        Check(!update.Observe(true, true, t0.AddSeconds(61)) && !update.Observe(true, false, t0.AddSeconds(100)),
            "update: a running turn or open list starts the minute again");
        Check(update.Observe(true, false, t0.AddSeconds(161)), "update: applied a quiet minute after the busy spell");
        Check(!new AutoUpdateGate().Observe(false, false, t0) && !update.Observe(false, false, t0.AddHours(1)), "update: nothing downloaded, nothing applied");
        Check(AppUpdater.IsPolicyBlock(new System.ComponentModel.Win32Exception(4551))
            && AppUpdater.IsPolicyBlock(new InvalidOperationException("x", new System.ComponentModel.Win32Exception(1260)))
            && !AppUpdater.IsPolicyBlock(new System.ComponentModel.Win32Exception(2)) && !AppUpdater.IsPolicyBlock(new IOException()),
            "update: a refusal by Windows policy is told apart from other failures");
        Check(AppUpdater.SetupUrl("0.5.3") == "https://github.com/igayowai/TokenOverlay/releases/download/v0.5.3/TokenOverlay-win-Setup.exe",
            "update: the setup file of exactly the waiting version is downloaded");
        Check(AppUpdater.SetupOutcome("0.5.3\nstarted", "0.5.3+abc") == "0.5.3(으)로 업데이트됨"
            && AppUpdater.SetupOutcome("0.5.3\r\nstarted", "0.5.2+abc")!.StartsWith("업데이트가 끝나지 않음")
            && AppUpdater.SetupOutcome("0.5.3\nblocked", "0.5.2")!.StartsWith("Windows가 설치 파일도 막음")
            && AppUpdater.SetupOutcome("0.5.3\nfailed", "0.5.2")!.StartsWith("설치 파일을 실행하지 못함")
            && AppUpdater.SetupOutcome("", "0.5.2") is null, "update: the next start reports how the setup file went");
        var notSetup = Path.Combine(Path.GetTempPath(), "token-overlay-not-setup-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
        File.WriteAllText(notSetup, "<html>not found</html>");
        Check(!AppUpdater.LooksLikeSetup(notSetup), "update: a download that is not a program is not run");
        File.Delete(notSetup);
        Check(!AppUpdater.TryRunSetupStep(["--install-setup", "1"]) && !AppUpdater.TryRunSetupStep(["--watch-codex"]),
            "update: only the full setup-step arguments start the setup step");
        Check(AppUpdater.StartAfterCommand(42, @"C:\Users\O'Neil\AppData\Local\TokenOverlay\current\TokenOverlay.exe")
                == @"Wait-Process -Id 42 -Timeout 600 -ErrorAction SilentlyContinue; Start-Process -FilePath 'C:\Users\O''Neil\AppData\Local\TokenOverlay\current\TokenOverlay.exe'",
            "update: after the setup the overlay is started from its quoted path");
        var task = System.Xml.Linq.XDocument.Parse(StartupRegistration.LogonTaskXml(@"C:\A & B\TokenOverlay.exe", @"PC\me").Split('\n', 2)[1]);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Check(task.Descendants(ns + "Command").Single().Value == @"C:\A & B\TokenOverlay.exe"
            && task.Descendants(ns + "Arguments").Single().Value == "--watch-codex", "logon task: starts the watcher from the escaped path");
        Check(task.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value == @"PC\me"
            && task.Descendants(ns + "RunLevel").Single().Value == "LeastPrivilege"
            && task.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S", "logon task: this user only, unelevated, no time limit");
        return new { Passed = passed.Count, Checks = passed,
            ActualCodexHosts = CodexWindowLocator.FindOpenMainWindows() };
    }
}
