namespace TokenOverlay;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Installer and updater hooks (Velopack) run first and may end the process; a build-folder copy passes through.
        // Uninstalling takes back what this copy set up outside its folder: auto-start and the apps' telemetry links.
        // After Windows refused the updater once, a downloaded update is not put in at start (it would be refused again).
        Velopack.VelopackApp.Build().OnBeforeUninstallFastCallback(_ => StartupRegistration.Uninstall())
            .OnRestarted(version => AppUpdater.RestartedInto = version.ToString())
            .SetAutoApplyOnStartup(!AppUpdater.BlockedBefore).Run();
        if (AppUpdater.TryRunSetupStep(args)) return;
        if (StartupRegistration.TryRun(args)) return;
        if (TelemetrySetup.TryRun(args)) return;
        if (ClaudeModSetup.TryRun(args)) return;
        if (args.Length > 0 && args[0] == "--quit")
        {
            // Asks the running overlay to exit normally (it saves its request-time cache), then waits for it to be gone.
            if (EventWaitHandle.TryOpenExisting(OverlayContext.QuitEventName, out var quit))
            {
                using (quit) quit.Set();
                for (var i = 0; i < 100 && CodexLaunchWatcher.Exists(CodexLaunchWatcher.OverlayMutex); i++) Thread.Sleep(50);
            }
            Environment.ExitCode = CodexLaunchWatcher.Exists(CodexLaunchWatcher.OverlayMutex) ? 1 : 0;
            return;
        }
        if (args.Length > 0 && args[0] == "--watch-codex")
        {
            CodexLaunchWatcher.Run();
            return;
        }
        var sessionRoot = SessionPathResolver.Resolve(args);
        if (ProbeRunner.TryRun(args, sessionRoot))
        {
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: CodexLaunchWatcher.OverlayMutex,
            createdNew: out var createdNew);
        if (!createdNew)
        {
            return;
        }

        CodexLaunchWatcher.EnsureRunning();

        // Bug reports: what started, and anything that would otherwise end the overlay silently.
        DiagnosticLog.Info("start", $"TokenOverlay {AppVersion.Text} · device {DeviceIdentity.Id} · "
            + $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · .NET {Environment.Version} · screens {Screen.AllScreens.Length}");
        Application.ThreadException += (_, e) => DiagnosticLog.Error("crash-ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) DiagnosticLog.Error("crash", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => DiagnosticLog.Error("task", e.Exception.GetBaseException());

        var settingsPath = OverlaySettings.ResolveSettingsOverride(args);
        Application.Run(new OverlayContext(sessionRoot, settingsPath));
        DiagnosticLog.Info("stop", "overlay closed normally");
        GC.KeepAlive(singleInstanceMutex);
    }
}

internal static class SessionPathResolver
{
    public static string Resolve(IReadOnlyList<string>? arguments = null)
    {
        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals("--sessions", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Normalize(arguments[index + 1]);
                }
            }
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(Normalize(codexHome), "sessions");
    }

    private static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}
