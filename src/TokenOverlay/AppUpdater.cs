using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace TokenOverlay;

// New versions come from the public release repository on GitHub (Velopack packages). Only an installed copy
// updates itself; a copy run from a build folder says so and does nothing. A found version is downloaded in the
// background and put in by itself once the overlay has been quiet for a minute (AutoUpdateGate), when the user
// says so, or on the next start, restarting the overlay.
// Windows can refuse to start Velopack's updater (Smart App Control and other application control policies stop
// unsigned files without a reputation), while the release's setup file still runs. Such a PC is remembered: from
// then on a new version is only looked up, and the user's click downloads its setup file and runs it silently,
// which installs over this copy and starts the new version.
internal sealed class AppUpdater
{
    // Set at start when Velopack restarted the overlay after putting in an update: the version now running.
    public static string? RestartedInto { get; set; }

    internal const string ReleaseRepository = "https://github.com/igayowai/TokenOverlay";
    internal const string DownloadPage = ReleaseRepository + "/releases/latest";
    internal const string InstallSetupArgument = "--install-setup";
    internal static string BlockedMarkerPath => Path.Combine(AppPaths.DataFolder, "update-blocked.txt");
    // "<version>\n<started|blocked>": left by the setup step for the next start to report on.
    internal static string SetupStatePath => Path.Combine(AppPaths.DataFolder, "update-setup.txt");
    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;
    private int _busy;
    private bool _autoApplyTried;
    private bool _blockNoticePending;
    private bool _blocked = BlockedBefore;
    private volatile string _status;

    // True when Windows refused the updater on this PC before: Velopack is then not asked to put anything in.
    public static bool BlockedBefore
    {
        get
        {
            try { return File.Exists(BlockedMarkerPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public AppUpdater()
    {
        try
        {
            _manager = new UpdateManager(Source());
            _status = _manager.IsInstalled ? $"현재 {AppVersion.Text}" : "설치본이 아님 · 업데이트 안 함";
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("update", e);
            _status = "업데이트 준비 실패";
        }
    }

#if TEST_FEED
    // Test builds (release.ps1 -TestFeed) update only from the folder named in update-source.txt; public builds have
    // no such switch. Without the file they look on GitHub, where their channel has no releases.
    internal static string TestFeedPath => Path.Combine(AppPaths.DataFolder, "update-source.txt");
    private static IUpdateSource Source()
    {
        var folder = File.Exists(TestFeedPath) ? File.ReadAllText(TestFeedPath).Trim() : "";
        if (folder.Length > 0 && Directory.Exists(folder))
        {
            DiagnosticLog.Info("update", "test build: updates from a local folder");
            return new SimpleFileSource(new DirectoryInfo(folder));
        }
        return new GithubSource(ReleaseRepository, null, false);
    }
#else
    private static IUpdateSource Source() => new GithubSource(ReleaseRepository, null, false);
#endif

    public bool Installed => _manager?.IsInstalled == true;
    public string Status => _status;
    // "0.4.1" once a newer version has been found (and, where the updater works, downloaded) and waits to go in.
    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();
    // A newer version waits on a PC where Windows blocks the updater: it goes in through its setup file, on a click.
    public bool Blocked => _blocked && _ready is not null;
    public bool Busy => Volatile.Read(ref _busy) == 1;
    private DateTime? _checkedAt;
    private bool _lastFailed;

    // The tray tooltip's second line: whether a newer version waits, else when the last check found none.
    public string TrayLine => TrayLineFor(Installed, ReadyVersion, Blocked, Volatile.Read(ref _busy) == 1, _lastFailed, _checkedAt);

    internal static string TrayLineFor(bool installed, string? ready, bool blocked, bool busy, bool failed, DateTime? checkedAt) =>
        !installed ? "설치본이 아님 · 업데이트 확인 안 함"
        : ready is not null ? (blocked ? $"새 버전 {ready} 나옴 · 메뉴 맨 위를 눌러 설치" : $"새 버전 {ready} 받음 · 메뉴 맨 위를 눌러 업데이트")
        : busy ? "업데이트 확인 중…"
        : failed ? "업데이트 확인 실패 · 나중에 다시 시도"
        : checkedAt is { } at ? $"최신 버전 · {at:HH:mm} 확인"
        : "업데이트 확인 전";

    // Looks for a newer release and downloads it (only looks where the updater is blocked). True when one waits.
    public async Task<bool> CheckAsync()
    {
        if (_manager is not { IsInstalled: true } manager) return false;
        if (_ready is not null) return true;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            _status = "확인 중…";
            var found = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (found is null)
            {
                _status = $"최신 ({AppVersion.Text}) · {DateTime.Now:HH:mm} 확인";
                _checkedAt = DateTime.Now; _lastFailed = false;
                DiagnosticLog.Change("update", "up to date");
                return false;
            }
            if (!_blocked)
            {
                _status = $"{found.TargetFullRelease.Version} 받는 중…";
                await manager.DownloadUpdatesAsync(found).ConfigureAwait(false);
            }
            _ready = found;
            _checkedAt = DateTime.Now; _lastFailed = false;
            _status = _blocked ? $"{found.TargetFullRelease.Version} 나옴 · 누르면 설치 파일로 설치" : $"{found.TargetFullRelease.Version} 준비됨 · 누르면 적용";
            DiagnosticLog.Info("update", _blocked ? $"found {found.TargetFullRelease.Version} (setup file route)" : $"downloaded {found.TargetFullRelease.Version}");
            return true;
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("update", e);
            _status = "확인 실패 · 나중에 다시 시도";
            _lastFailed = true;
            return false;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // Applied by itself at most once per run: a failure waits for the menu or the next start instead of retrying.
    public bool TryAutoApply()
    {
        if (_ready is null || _autoApplyTried || _blocked) return false;
        _autoApplyTried = true;
        return Apply();
    }

    // Hands the downloaded version to Velopack, which waits for this process to end, swaps the files and starts
    // the overlay again. The caller then exits normally (saving what it keeps); the watcher is asked to stop first.
    public bool Apply()
    {
        if (_manager is null || _ready is null || _blocked) return false;
        try
        {
            CodexLaunchWatcher.RequestStop();
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true);
            DiagnosticLog.Info("update", $"applying {_ready.TargetFullRelease.Version}");
            return true;
        }
        catch (Exception e) when (IsPolicyBlock(e))
        {
            DiagnosticLog.Error("update", e);
            _blocked = true;
            _blockNoticePending = true;
            _status = $"Windows가 업데이트 도구를 막음 · 누르면 설치 파일로 설치";
            try { File.WriteAllText(BlockedMarkerPath, $"{ReadyVersion}\n{DateTimeOffset.Now:O}"); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException) { DiagnosticLog.Error("update", write); }
            return false;
        }
        catch (Exception e)
        {
            DiagnosticLog.Error("update", e);
            _status = "적용 실패 · 다음 시작 때 다시 시도";
            return false;
        }
    }

    // True once, right after Windows refused the updater: the caller tells the user about the setup-file route.
    public bool TakeBlockNotice()
    {
        var pending = _blockNoticePending;
        _blockNoticePending = false;
        return pending;
    }

    // Downloads the waiting version's setup file and starts this program's setup step, which waits for the overlay
    // to end and then runs the setup file silently. True when the caller should now exit normally.
    public async Task<bool> InstallWithSetupAsync()
    {
        if (_ready is null || ReadyVersion is not { } version || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        try
        {
            _status = $"{version} 설치 파일 받는 중…";
            var folder = Path.Combine(Path.GetTempPath(), "TokenOverlay-update");
            Directory.CreateDirectory(folder);
            var setup = Path.Combine(folder, $"TokenOverlay-{version}-Setup.exe");
            var temp = setup + ".part";
            using (var response = await Download.GetAsync(SetupUrl(version), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var target = File.Create(temp);
                await source.CopyToAsync(target).ConfigureAwait(false);
            }
            if (!LooksLikeSetup(temp)) throw new InvalidDataException("the downloaded setup file is not a program");
            File.Move(temp, setup, overwrite: true);
            File.WriteAllText(SetupStatePath, $"{version}\nstarted");
            CodexLaunchWatcher.RequestStop();
            using var step = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, ArgumentList = { InstallSetupArgument, Environment.ProcessId.ToString(), setup } });
            DiagnosticLog.Info("update", $"installing {version} with its setup file");
            return step is not null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
            or InvalidDataException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            DiagnosticLog.Error("update", e);
            _status = "설치 파일을 받지 못함 · 다운로드 페이지에서 직접 받기";
            OpenDownloadPage();
            return false;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    internal static string SetupUrl(string version) => $"{ReleaseRepository}/releases/download/v{version}/TokenOverlay-win-Setup.exe";

    // A Windows program of a setup file's size.
    internal static bool LooksLikeSetup(string path)
    {
        using var file = File.OpenRead(path);
        return file.Length > 5_000_000 && file.ReadByte() == 'M' && file.ReadByte() == 'Z';
    }

    // The setup step: "--install-setup <overlay pid> <setup file>". Waits for the overlay to end, then runs the setup
    // file silently (it answers its own "install over?" question and installs, but starts nothing afterwards). This
    // step runs from the install folder, which the setup clears of running programs, so Windows' own PowerShell
    // waits for the setup instead and then starts the overlay. When Windows will not start the setup file either,
    // the overlay is started again and told so. True when the arguments were for this.
    public static bool TryRunSetupStep(string[] args)
    {
        if (args.Length != 3 || args[0] != InstallSetupArgument) return false;
        try
        {
            if (int.TryParse(args[1], out var pid))
            {
                try { using var overlay = System.Diagnostics.Process.GetProcessById(pid); overlay.WaitForExit(60_000); }
                catch (ArgumentException) { }
            }
            using var setup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args[2])
                { UseShellExecute = false, ArgumentList = { "--silent" } })
                ?? throw new InvalidOperationException("the setup file did not start");
            using var waiter = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WindowsPowerShell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", StartAfterCommand(setup.Id, Environment.ProcessPath!) }
            });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Error("update", e);
            try
            {
                var version = File.Exists(SetupStatePath) ? File.ReadAllLines(SetupStatePath).FirstOrDefault() ?? "" : "";
                File.WriteAllText(SetupStatePath, $"{version}\n{(IsPolicyBlock(e) ? "blocked" : "failed")}");
                using var again = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
            }
            catch (Exception restart) when (restart is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("update", restart);
            }
        }
        return true;
    }

    private static string WindowsPowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    // Waits up to ten minutes for the setup, then starts the overlay (path quoted for PowerShell).
    internal static string StartAfterCommand(int setupPid, string overlayExe) =>
        $"Wait-Process -Id {setupPid} -Timeout 600 -ErrorAction SilentlyContinue; Start-Process -FilePath '{overlayExe.Replace("'", "''")}'";

    // What the setup step left behind, as a message for this start (null: nothing to say). Removes the note.
    public static string? TakeSetupOutcome()
    {
        try
        {
            if (!File.Exists(SetupStatePath)) return null;
            var note = File.ReadAllText(SetupStatePath);
            File.Delete(SetupStatePath);
            return SetupOutcome(note, AppVersion.Text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? SetupOutcome(string note, string current)
    {
        var lines = note.Split('\n', StringSplitOptions.TrimEntries);
        var version = lines.ElementAtOrDefault(0) ?? "";
        var installed = Version.TryParse(version, out var wanted) && Version.TryParse(current.Split('+', '-')[0], out var now) && now >= wanted;
        return lines.ElementAtOrDefault(1) switch
        {
            "started" when installed => $"{version}(으)로 업데이트됨",
            "started" => "업데이트가 끝나지 않음 · 트레이 메뉴에서 다시 설치",
            "blocked" => "Windows가 설치 파일도 막음 · 지금 버전 그대로 씀",
            "failed" => "설치 파일을 실행하지 못함 · 트레이 메뉴에서 다시 설치",
            _ => null
        };
    }

    public static void OpenDownloadPage()
    {
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadPage) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { DiagnosticLog.Error("update", e); }
    }

    // Refusals by policy: blocked by group policy (1260), an application control policy such as Smart App Control
    // (4551), or a reputation verdict (4580-4583).
    internal static bool IsPolicyBlock(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is System.ComponentModel.Win32Exception { NativeErrorCode: 1260 or 4551 or (>= 4580 and <= 4583) }) return true;
        return false;
    }
}
