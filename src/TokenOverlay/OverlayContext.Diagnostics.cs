namespace TokenOverlay;

internal sealed partial class OverlayContext
{
    private void WriteDiagnostics()
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--diagnostics");
        if (i < 0 || i + 1 >= args.Length || DateTime.UtcNow - _lastDiagnostic < TimeSpan.FromSeconds(1)) return;
        _lastDiagnostic = DateTime.UtcNow;
        // Verification aid: rate the turn of a session that is not on screen, using the live receiver data.
        // The text file named after --probe-claude-log-file holds that session log's path.
        TurnStatistics? probe = null;
        var probeIndex = Array.IndexOf(args, "--probe-claude-log-file");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            try
            {
                var reader = new ClaudeTurnReader();
                probe = reader.Read(System.IO.File.ReadAllText(args[probeIndex + 1]).Trim());
                if (probe is { Seconds: not null }) probe = probe.With(_telemetry.ClaudeTurn(reader.DisplayedRequestIds));
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        TurnStatistics? codexProbe = null;
        probeIndex = Array.IndexOf(args, "--probe-codex-log-file");
        if (probeIndex >= 0 && probeIndex + 1 < args.Length)
        {
            try
            {
                var reader = new TurnStatisticsReader();
                codexProbe = reader.Read(System.IO.File.ReadAllText(args[probeIndex + 1]).Trim());
                if (codexProbe is { Seconds: not null })
                    codexProbe = codexProbe.With(_telemetry.CodexTurn(codexProbe.TurnId, reader.DisplayedResponses));
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var value = new { At = DateTime.UtcNow, Visible = _form.Visible,
            Bounds = new { _form.Left, _form.Top, _form.Width, _form.Height },
            Route = _pendingRouteStatus, VisibleTitle = _pendingVisibleTitle, App = _lastSnapshot?.App,
            Telemetry = new { _telemetryReceiver.Error, Accepted = Interlocked.Read(ref _telemetryReceiver.Accepted), _telemetry.ClaudeRequests, _telemetry.ClaudeRequestsWithTtft, _telemetry.CodexStreams },
            Weekly = new { _weekly.Line, _weekly.ApiEnabled, _weekly.ApiStatus, ApiSamples = _weekly.ApiSamples.Count },
            ClaudeProbe = probe, CodexProbe = codexProbe,
            Turn = _lastSnapshot?.Turn,
            Presentation = _presentation,
            // Why the Ctrl list would or would not open right now.
            Gesture = new { HistoryMode = _historyMode, Control = (Control.ModifierKeys & Keys.Control) != 0,
                HasTarget = _currentTarget is not null, ManuallyHidden = _manuallyHidden, _form.HoverHidden,
                Interaction = _interaction.State.ToString(), HistoryRows = _presentation.History.Count,
                CursorOnCapsule = _form.CapsuleScreenBounds.Contains(Cursor.Position),
                Capsule = _form.CapsuleScreenBounds, Cursor = Cursor.Position, PaneForms = _paneForms.Count(f => f.Visible) },
            Host = CodexWindowLocator.GetForegroundWindowProbe() };
        try { System.IO.File.WriteAllText(args[i+1], System.Text.Json.JsonSerializer.Serialize(value)); }
        catch (System.IO.IOException) { }
    }

    // State changes only, for bug reports: never the chat title itself, only whether one was seen and its length.
    private void LogPollState(HostApp app, ActiveThreadRouteStatus route, string? title, TokenSnapshot? snapshot)
    {
        var shownTitle = title is null ? "none" : IsCodexHomeTitle(title) ? "home" : $"{title.Length} chars";
        DiagnosticLog.Change("route", $"{app} · connected {route.IsConnected} · windows {route.ActiveWindowCount} · "
            + $"chat {(string.IsNullOrWhiteSpace(route.ThreadId) ? "none" : OverlayPresentationBuilder.ShortThreadId(route.ThreadId))} · "
            + $"title {shownTitle}{(route.LastError is { Length: > 0 } reason ? " · " + reason : "")}");
        DiagnosticLog.Change("snapshot", snapshot is null ? "none"
            : $"{snapshot.App} chat {OverlayPresentationBuilder.ShortThreadId(snapshot.ThreadId)} · turn {(snapshot.Turn is null ? "none" : snapshot.Turn.Running ? "running" : "done")}");
        // The bug report is made from the tray, when neither app is in front: the last chat each app showed is kept for it.
        if (app == HostApp.Claude && title is not null) _reportClaudeTitle = title;
        if (app == HostApp.Codex && route.IsConnected && !string.IsNullOrWhiteSpace(route.ThreadId)) _reportCodexThread = route.ThreadId;
        DiagnosticLog.Change("telemetry", _telemetryReceiver.Error ?? "listening");
        DiagnosticLog.Change("claude-weekly", _weekly.ApiStatus);
    }

    // Everything a bug report needs besides the logs; no titles, conversation text or login details.
    private string ReportSummary()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"TokenOverlay {AppVersion.Text}");
        text.AppendLine($"device {DeviceIdentity.Id} · created {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine($"windows {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · .NET {Environment.Version}");
        foreach (var screen in Screen.AllScreens)
            text.AppendLine($"screen {screen.Bounds.Width}x{screen.Bounds.Height} at {screen.Bounds.X},{screen.Bounds.Y}{(screen.Primary ? " (primary)" : "")}");
        try
        {
            var (claude, codex) = TelemetrySetup.Status();
            text.AppendLine($"telemetry claude {claude} · codex {codex} · receiver {_telemetryReceiver.Error ?? "listening"}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { text.AppendLine("telemetry status unreadable: " + e.Message); }
        text.AppendLine($"share {_driveLink.Status}");
        text.AppendLine($"claude weekly {_weekly.ApiStatus} · line {_weekly.Line ?? "none"} · asked readings {_weekly.ApiSamples.Count}");
        if (_weekly.LastWindows is [.., var claudeWeek])
            text.AppendLine($"claude week from {DriveShare.When(claudeWeek.StartMs)} · account {claudeWeek.Account} · this pc {claudeWeek.ThisPc}"
                + $" · this pc alone {claudeWeek.ThisPcOnly?.ToString() ?? "-"} · people {(claudeWeek.People is { } p ? string.Join("/", p) : "-")}");
        text.AppendLine($"claude share {_weekly.ShareNote}");
        text.AppendLine($"codex weekly {_codexWeekly.ApiStatus} · line {_codexWeekly.Footer?.Split('\n')[0] ?? "none"} · asked readings {_codexWeekly.ApiSamples.Count}");
        text.AppendLine($"codex share {_codexWeekly.ShareNote}");
        text.AppendLine($"route {_pendingApp} · connected {_pendingRouteStatus.IsConnected} · windows {_pendingRouteStatus.ActiveWindowCount} · {_pendingRouteStatus.LastError}");
        text.AppendLine($"overlay visible {_form.Visible} · bounds {_form.Bounds} · manually hidden {_manuallyHidden}");
        text.AppendLine("settings " + _settings.Serialize());
        return text.ToString();
    }

    private string? _reportClaudeTitle;
    private string? _reportCodexThread;

    // The slow half of a bug report (reads app folders and logs), run off the UI thread.
    private string ReportDiagnosis(string? claudeTitle, string? codexThread)
    {
        var text = new System.Text.StringBuilder();
        text.Append(InstallDiagnosis());
        try { text.Append(ClaudeSessionIndex.Describe(claudeTitle, DateTime.UtcNow)); }
        catch (Exception e) { text.AppendLine("claude diagnosis failed: " + e.GetType().Name); }
        try { text.Append(_monitor.Describe(codexThread)); }
        catch (Exception e) { text.AppendLine("codex diagnosis failed: " + e.GetType().Name); }
        return text.ToString();
    }

    // Whether this copy came from the installer and which update packages it has received.
    private static string InstallDiagnosis()
    {
        var current = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var install = Path.GetDirectoryName(current);
        var installed = Path.GetFileName(current).Equals("current", StringComparison.OrdinalIgnoreCase)
            && install is not null && File.Exists(Path.Combine(install, "Update.exe"));
        if (!installed) return "install not from the installer" + Environment.NewLine;
        var packages = Path.Combine(install!, "packages");
        var names = Directory.Exists(packages)
            ? Directory.EnumerateFiles(packages, "*.nupkg").Select(f => $"{Path.GetFileName(f)} ({File.GetLastWriteTime(f):MM-dd HH:mm})").ToArray()
            : [];
        return $"install installer · update packages {(names.Length == 0 ? "none" : string.Join(", ", names))}" + Environment.NewLine;
    }
}
