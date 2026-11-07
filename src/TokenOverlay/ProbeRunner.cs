using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

internal static partial class ProbeRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static bool TryRun(IReadOnlyList<string> args, string sessionRoot)
    {
        // Checks and probes keep their log lines out of the user's own log.
        if (args.Count > 0 && args[0].StartsWith("--") && (args[0].EndsWith("-tests") || args[0].EndsWith("-probe")))
            DiagnosticLog.UseFolder(Path.Combine(Path.GetTempPath(), "TokenOverlay-check-logs"));
        if (args.Count >= 3 && args[0] == "--autosize-tests")
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], AutoCapsuleChecks.Run(args[2]));
            return true;
        }
        if (args.Count >= 2 && args[0] == "--visible-thread-probe")
        {
            var reader = new VisibleThreadRouteMonitor(sessionRoot);
            var route = Task.Run(reader.Poll).GetAwaiter().GetResult();
            WriteJson(args[1], new { Route = route, reader.VisibleTitle });
            return true;
        }
        if (args.Count >= 2 && args[0] == "--codex-weekly-probe")
        {
            // Numbers only: the weekly footer from this PC's Codex logs, and how long the first full read took.
            // Asks Codex once too, but keeps nothing: no store file, no share.
            var monitor = new CodexWeeklyMonitor([sessionRoot, Path.Combine(Directory.GetParent(sessionRoot)?.FullName ?? sessionRoot, "archived_sessions")],
                null, CodexUsageApi.Fetch, null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            monitor.Update(DateTimeOffset.UtcNow);
            var firstMs = watch.ElapsedMilliseconds;
            watch.Restart();
            monitor.Update(DateTimeOffset.UtcNow);
            WriteJson(args[1], new { monitor.Footer, Samples = monitor.Samples.Count, monitor.ApiStatus, Asked = monitor.ApiSamples.Select(s => new { s.Percent, s.ResetMs }), FirstMs = firstMs, NextMs = watch.ElapsedMilliseconds });
            return true;
        }
        if (args.Count >= 2 && args[0] == "--cache-ping-probe")
        {
            // A picture of the cache session window with made-up sessions (args[1] = png path).
            PrepareWindowProbeDpiAwareness();
            var now = DateTimeOffset.Now;
            static CacheWatch.Bridge Mod(DateTimeOffset alive, DateTimeOffset? pingAt = null, bool ok = false, string? reason = null, int code = CacheWatch.ModCode) =>
                new(alive, null, "", pingAt, ok, reason, ok ? 351_406 : 0, 0, null, Code: code, PingAuto: ok);
            // 유지 on for the second session, in a folder of the picture's own.
            var keepFolder = Path.Combine(Path.GetTempPath(), "TokenOverlay-cache-ping-probe");
            if (Directory.Exists(keepFolder)) Directory.Delete(keepFolder, true);
            CacheWatch.SetKeep("00000000-0000-0000-0000-000000000002", true, now.AddHours(-1.5), keepFolder);
            using var watch = new CacheWatch(start: false);
            // A compaction the mod tried that the log never got (kept past the lapse), and one being retried.
            CacheWatch.Attempt Tried(double minutesAgo, string by) => new(now.AddMinutes(-minutesAgo), by, now.AddMinutes(0.1 - minutesAgo), "api", true, null);
            watch.ShowSessions([
                new("00000000-0000-0000-0000-000000000011", "Tesira 트레이 관리자", now.AddMinutes(-34), 271_000, Mod(now) with { Attempt = Tried(39, "auto") }, true,
                    CacheWatch.Compaction.Failed),
                new("00000000-0000-0000-0000-000000000012", "정류기", now.AddMinutes(1.5), 230_000, Mod(now) with { Attempt = Tried(1, "retry") }, true,
                    CacheWatch.Compaction.Retrying),
                new("00000000-0000-0000-0000-000000000001", "오버레이 캐시 알림", now.AddMinutes(7.2), 351_000, Mod(now), true),
                new("00000000-0000-0000-0000-000000000002", "블로그 글 퇴고", now.AddMinutes(23.5), 212_000, Mod(now, now.AddMinutes(-36.5), true), true),
                new("00000000-0000-0000-0000-000000000006", "보고서 정리", now.AddMinutes(33), 287_000, Mod(now), true),
                new("00000000-0000-0000-0000-000000000007", "코드 리뷰", now.AddMinutes(38), 176_000, Mod(now), true),
                new("00000000-0000-0000-0000-000000000003", "dsh-worker", now.AddMinutes(41), 168_000, Mod(now, now.AddMinutes(-3), false, "busy"), true),
                new("00000000-0000-0000-0000-000000000005", "어제 연 세션", now.AddMinutes(45), 240_000, Mod(now, code: 2), true),
                new("00000000-0000-0000-0000-000000000004", "터미널 세션", now.AddMinutes(48), 190_000, null, false)]);
            // 압축 pressed once on 보고서 정리 (waiting for the second press), twice on 코드 리뷰 (asked).
            var pings = new CachePings(keepFolder);
            pings.Compact("00000000-0000-0000-0000-000000000007", now);
            pings.Compact("00000000-0000-0000-0000-000000000007", now);
            pings.Compact("00000000-0000-0000-0000-000000000006", now);
            using var form = new CachePingForm(watch, () => now, live: false, pings);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            Application.DoEvents();
            form.Fill();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(args[1]);
            return true;
        }
        if (args.Count >= 2 && args[0] == "--device-manager-probe")
        {
            // A picture of the device manager filled with made-up devices in a scratch folder (args[1] = png path).
            PrepareWindowProbeDpiAwareness();
            var root = Path.Combine(Path.GetTempPath(), "TokenOverlay-device-probe");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            var devices = Path.Combine(root, DriveShare.FolderName, "devices");
            Directory.CreateDirectory(devices);
            File.WriteAllText(Path.Combine(devices, "pc-7a01b2c3.codex.json"), """{"device":"pc-7a01b2c3","name":"LAPTOP-1"}""");
            File.WriteAllText(Path.Combine(devices, "pc-5d9e0f11.claude.json"), """{"device":"pc-5d9e0f11","name":"OLD-PC"}""");
            File.SetLastWriteTimeUtc(Path.Combine(devices, "pc-5d9e0f11.claude.json"), DateTime.UtcNow.AddDays(-12));
            var link = new DriveShareLink(() => null, true, root);
            if (link.Current() is { } probeShare)
            {
                probeShare.Write("codex", new { device = probeShare.Device, name = probeShare.Name });
                foreach (var d in probeShare.ReadOthers("codex", DateTimeOffset.UtcNow)) d.Dispose();
                probeShare.Rename("pc-7a01b2c3", "노트북");
                probeShare.Merge("pc-5d9e0f11", DeviceIdentity.Id);
            }
            using var form = new DeviceManagerForm(link, _ => { });
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.Show();
            Application.DoEvents();
            form.Fill();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(args[1]);
            return true;
        }
        if (args.Count >= 2 && args[0] == "--telemetry-tests")
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], TelemetryChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            return true;
        }
        if (args.Count >= 2 && args[0] == "--deepseek-tests")
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], DeepSeekChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            return true;
        }
        if (args.Count >= 2 && args[0] == "--claude-tests")
        {
            WriteJson(args[1], ClaudeChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            return true;
        }
        if (args.Count >= 2 && args[0] == "--renew-probe")
        {
            // Starts the bundled Claude Code the way the overlay does near token expiry (both steps), and reports
            // only whether each ran and whether the expiry moved. Costs one tiny Haiku request.
            var before = ClaudeTokenRenewal.ReadExpiry();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var status = ClaudeTokenRenewal.Run(ClaudeTokenRenewal.StatusCommand, 20_000);
            var statusMs = clock.ElapsedMilliseconds; clock.Restart();
            var request = ClaudeTokenRenewal.Run(ClaudeTokenRenewal.TinyRequest, 90_000);
            WriteJson(args[1], new { Cli = ClaudeTokenRenewal.FindCli(), StatusRan = status, StatusMs = statusMs,
                RequestRan = request, RequestMs = clock.ElapsedMilliseconds,
                ExpiryBefore = before, ExpiryAfter = ClaudeTokenRenewal.ReadExpiry() });
            return true;
        }
        if (args.Count >= 2 && args[0] == "--weekly-probe")
        {
            // This PC's share of the weekly limit, computed from the real files, written as JSON (no text from the logs).
            var monitor = new WeeklyUsageMonitor(() => ClaudeSessionIndex.DataRoots().Select(root => Path.Combine(root, "plan-usage-history.json")),
                ClaudeSessionIndex.ProjectsRoot(), null, ClaudeUsageApi.Fetch);
            monitor.Update(DateTimeOffset.UtcNow);
            WriteJson(args[1], new { monitor.Line, monitor.ApiStatus,
                ApiSamples = monitor.ApiSamples.Select(s => new { At = DateTimeOffset.FromUnixTimeMilliseconds(s.AtMs).ToLocalTime(), s.Percent }), Weeks = monitor.LastWindows.Select(w => new {
                Start = DateTimeOffset.FromUnixTimeMilliseconds(w.StartMs).ToLocalTime(),
                End = w.EndMs is long end ? DateTimeOffset.FromUnixTimeMilliseconds(end).ToLocalTime() : (DateTimeOffset?)null,
                w.Account, w.ThisPc, w.Tokens }) });
            return true;
        }
        if (args.Count >= 2 && args[0] == "--claude-probe")
        {
            WriteJson(args[1], ClaudeChecks.Probe());
            return true;
        }
        if (args.Count >= 3 && args[0] == "--claude-turn-probe")
        {
            var claudeReader = new ClaudeTurnReader();
            // A read takes at most 16 MB; keep reading until a long session is caught up.
            var claudeTurn = claudeReader.Read(args[2]);
            for (var pass = 0; !claudeReader.CaughtUp && pass < 64; pass++) claudeTurn = claudeReader.Read(args[2]);
            WriteJson(args[1], new { Turn = claudeTurn, claudeReader.Cache, CacheLine = CacheStatus.Line(claudeReader.Cache, DateTimeOffset.UtcNow),
                CacheNote = CacheStatus.CapsuleNote(claudeReader.Cache, DateTimeOffset.UtcNow),
                claudeReader.Context, claudeReader.LastModel, ContextFill = ContextUsage.ClaudeFill(claudeReader.Context, claudeReader.LastModel),
                Rows = TurnPresentation.HistoryRows(new TurnHistory(claudeReader.Recent(TurnHistory.Size).Select(r => r.Turn).ToArray())) });
            return true;
        }
        if (args.Count >= 2 && args[0] == "--account-tests")
        {
            WriteJson(args[1], AccountChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            return true;
        }
        if (args.Count >= 2 && args[0] == "--watcher-tests")
        {
            WriteJson(args[1], CodexLaunchWatcher.Test());
            return true;
        }
        if (args.Count >= 3 && args[0] == "--turn-preview")
        {
            PrepareWindowProbeDpiAwareness();
            TurnChecks.Render(args[1], args[2]);
            return true;
        }
        if (args.Count >= 2 && args[0] == "--turn-tests")
        {
            WriteJson(args[1], TurnChecks.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!));
            return true;
        }
        if (args.Count >= 3 && args[0] == "--turn-probe")
        {
            var turnReader = new TurnStatisticsReader();
            var turnClock = System.Diagnostics.Stopwatch.StartNew();
            var probed = turnReader.Read(args[2]);
            // A long log takes several reads; "--timed" also reports how many and how long.
            var reads = 1;
            for (; !turnReader.CaughtUp && reads < 100_000; reads++) probed = turnReader.Read(args[2]);
            if (args.Count >= 4 && args[3] == "--timed")
                WriteJson(args[1], new { Ms = turnClock.ElapsedMilliseconds, Reads = reads, Turn = probed,
                    Recent = turnReader.Recent(TurnHistory.Size).Select(r => r.Turn).ToArray() });
            else WriteJson(args[1], probed);
            return true;
        }
        if (args.Count >= 2 && args[0].Equals("--theme-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], ExecuteThemeProbe());
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--attachment-probe", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(args[1], ExecuteAttachmentProbe(args[2]));
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--window-classification-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<WindowClassificationProbeRequest>(args[2]);
            WriteJson(args[1], WindowClassificationProbe.Execute(request));
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--settings-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<SettingsProbeRequest>(args[2]);
            var result = SettingsProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--presentation-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<PresentationProbeRequest>(args[2]);
            var result = PresentationProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--layout-probe", StringComparison.OrdinalIgnoreCase))
        {
            var result = ExecuteLayoutProbe(args[2]);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--interaction-probe", StringComparison.OrdinalIgnoreCase))
        {
            var request = ReadJson<InteractionProbeRequest>(args[2]);
            var result = InteractionProbe.Execute(request);
            WriteJson(args[1], result);
            return true;
        }

        if (args.Count >= 3 && args[0].Equals("--form-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            var request = ReadJson<FormProbeRequest>(args[2]);
            var result = ExecuteFormProbe(request);
            WriteJson(args[1], result);
            return true;
        }

        // The IPC probe checks Codex's broadcast of the visible chat, without starting the overlay.
        if (args.Count >= 2 && args[0].Equals("--ipc-probe", StringComparison.OrdinalIgnoreCase))
        {
            using var routeMonitor = new CodexIpcActiveThreadMonitor();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            ActiveThreadRouteStatus status;
            do
            {
                Thread.Sleep(100);
                status = routeMonitor.GetStatus();
            }
            while (DateTime.UtcNow < deadline && string.IsNullOrWhiteSpace(status.ThreadId));

            WriteJson(args[1], status);
            return true;
        }

        // The window probe checks the Codex top-level windows visible on the interactive desktop.
        if (args.Count >= 2 && args[0].Equals("--window-probe", StringComparison.OrdinalIgnoreCase))
        {
            PrepareWindowProbeDpiAwareness();
            WriteJson(args[1], CodexWindowLocator.GetForegroundWindowProbe());
            return true;
        }

        // The chat-switch probe checks switching between idle logs within one monitor instance.
        if (args.Count >= 4 && args[0].Equals("--thread-switch-probe", StringComparison.OrdinalIgnoreCase))
        {
            using var monitor = new TokenLogMonitor(sessionRoot);
            monitor.PreferredThreadId = args[2];
            var first = monitor.Poll(forceFullScan: true);
            var firstVersion = monitor.ActiveSessionVersion;
            monitor.PreferredThreadId = args[3];
            var second = monitor.Poll();
            var secondVersion = monitor.ActiveSessionVersion;
            WriteJson(args[1], new
            {
                FirstSnapshot = first,
                FirstVersion = firstVersion,
                SecondSnapshot = second,
                SecondVersion = secondVersion
            });
            return true;
        }

        // Probe mode checks real session logs after a build, without starting any window.
        if (args.Count >= 2 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
        {
            using var monitor = new TokenLogMonitor(sessionRoot);
            if (args.Count >= 3 && !args[2].StartsWith("--", StringComparison.Ordinal))
            {
                monitor.PreferredThreadId = args[2];
            }
            WriteJson(args[1], monitor.Poll(forceFullScan: true));
            return true;
        }

        return false;
    }

    internal static void PrepareWindowProbeDpiAwareness()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    }

    private static T ReadJson<T>(string path)
    {
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new JsonException($"Empty JSON probe request: {path}");
    }

    private static Task RunCaptured(Action action, List<string> errors) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            lock (errors)
            {
                errors.Add(exception.ToString());
            }
        }
    });

    private static bool PumpUntil(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        Application.DoEvents();
        return condition();
    }

    private static bool WaitWithoutPumping(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 3000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(1);
        }
        return condition();
    }

    private static Point ScreenCenter(IntRect windowBounds, IntRect clientBounds) =>
        new(
            windowBounds.X + clientBounds.X + (clientBounds.Width / 2),
            windowBounds.Y + clientBounds.Y + (clientBounds.Height / 2));

    private static int SendHitTest(IntPtr handle, Point screenPoint)
    {
        var packed = unchecked((screenPoint.Y << 16) | (screenPoint.X & 0xffff));
        return SendMessage(handle, WmNcHitTest, IntPtr.Zero, (IntPtr)packed).ToInt32();
    }

    private static bool RegionMatchesLayout(TokenStripForm form, OverlayLayoutResult layout)
    {
        using var path = new GraphicsPath();
        var dpi = layout.Dpi == 0 ? 96u : layout.Dpi;
        if (!layout.CapsuleBounds.IsEmpty)
        {
            using var capsule = CreateRoundedRectanglePath(
                layout.CapsuleBounds.ToRectangle(),
                ScaleDip(10, dpi));
            path.AddPath(capsule, connect: false);
        }
        if (!layout.PanelBounds.IsEmpty)
        {
            using var panel = CreateRoundedRectanglePath(
                layout.PanelBounds.ToRectangle(),
                ScaleDip(14, dpi));
            path.AddPath(panel, connect: false);
        }

        using var expected = new Region(path);
        using var graphics = form.CreateGraphics();
        return form.Region?.Equals(expected, graphics) == true;
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var clampedRadius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
        if (clampedRadius == 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }

        var diameter = clampedRadius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static int ScaleDip(int dip, uint dpi) =>
        (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static IntPtr GetWindowLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : (IntPtr)GetWindowLong32(windowHandle, index);

    private static IntPtr GetClassLongPtr(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetClassLongPtr64(windowHandle, index)
            : (IntPtr)unchecked((int)GetClassLong32(windowHandle, index));

    internal static void WriteJson(string path, object? value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    private sealed class LayoutProbeEnvelopeRequest
    {
        public IReadOnlyList<JsonElement> Cases { get; init; } = [];
    }

    private sealed class AttachmentProbeEnvelopeRequest
    {
        public IReadOnlyList<JsonElement> Cases { get; init; } = [];
    }

    private class AttachmentProbeCaseRequest
    {
        public string Name { get; init; } = string.Empty;
    }

    private sealed class AttachmentReferencePointsProbeRequest : AttachmentProbeCaseRequest
    {
        public IntRect Target { get; init; }
        public uint Dpi { get; init; }
    }

    private class AttachmentReferencePointProbeRequest : AttachmentProbeCaseRequest
    {
        public IntRect Target { get; init; }
        public Point Center { get; init; }
    }

    private sealed class AttachmentCaptureResolveProbeRequest : AttachmentReferencePointProbeRequest
    {
        public uint Dpi { get; init; }
    }

    private sealed class AttachmentTargetSelectionProbeRequest : AttachmentProbeCaseRequest
    {
        public required AttachmentTargetBounds Targets { get; init; }
        public IReadOnlyList<Point> Points { get; init; } = [];
        public IReadOnlyList<bool> HostSurfaceHits { get; init; } = [];
    }

    private sealed class AttachmentScaleProbeRequest : AttachmentProbeCaseRequest
    {
        public IReadOnlyList<AttachmentScaleProbeCase> Cases { get; init; } = [];
    }

    private sealed class AttachmentScaleProbeCase
    {
        public int StartWidth { get; init; }
        public int StartHeight { get; init; }
        public int StartScale { get; init; }
        public int DeltaX { get; init; }
        public int DeltaY { get; init; }
    }

    private sealed class AttachmentEditStateProbeRequest : AttachmentProbeCaseRequest
    {
        public required ManualPlacementSnapshot CommitOriginal { get; init; }
        public required WindowAttachment CommitAttachment { get; init; }
        public int CommitScale { get; init; }
        public required ManualPlacementSnapshot CancelOriginal { get; init; }
        public required WindowAttachment CancelAttachment { get; init; }
        public int CancelScale { get; init; }
    }

    private sealed class CaptionBoundsProbeRequest
    {
        public string Name { get; init; } = string.Empty;
        public IntRect WindowBounds { get; init; }
        public IntRect RelativeBounds { get; init; }
    }

    private sealed record CaptionBoundsProbeResult(string Name, IntRect ScreenBounds);

    private sealed class FormProbeRequest
    {
        public IReadOnlyList<FormProbeCaseRequest> Cases { get; init; } = [];
    }

    private sealed class FormProbeCaseRequest
    {
        public string Name { get; init; } = string.Empty;
        public required OverlayLayoutResult CollapsedLayout { get; init; }
        public required OverlayLayoutResult Collapsed60Layout { get; init; }
        public required OverlayLayoutResult Collapsed130Layout { get; init; }
        public required OverlayLayoutResult ExpandedLayout { get; init; }
    }

    private sealed record FormProbeResult(IReadOnlyList<FormProbeCaseResult> Cases);

    private sealed class ProbeOverlayThemeSource : IOverlayThemeSource
    {
        private readonly object _gate = new();
        private EventHandler? _changed;
        private EventHandler? _lastSubscribedHandler;
        private OverlayThemeKind _current;
        private int _sequence;
        private bool _disposed;

        public ProbeOverlayThemeSource(OverlayThemeKind current)
        {
            _current = current;
        }

        public OverlayThemeKind Current
        {
            get
            {
                lock (_gate)
                {
                    return _current;
                }
            }
        }

        public bool UnsubscribedBeforeDispose { get; private set; }

        public event EventHandler? Changed
        {
            add
            {
                lock (_gate)
                {
                    _changed += value;
                    _lastSubscribedHandler = value;
                }
            }
            remove
            {
                lock (_gate)
                {
                    _changed -= value;
                    _sequence++;
                }
            }
        }

        public void Set(OverlayThemeKind kind, bool forceEvent)
        {
            EventHandler? changed;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var kindChanged = kind != _current;
                _current = kind;
                if (!forceEvent && !kindChanged)
                {
                    return;
                }
                changed = _changed;
            }

            changed?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseCapturedEvent()
        {
            EventHandler? captured;
            lock (_gate)
            {
                captured = _lastSubscribedHandler;
            }
            captured?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _sequence++;
                UnsubscribedBeforeDispose = _changed is null && _sequence == 2;
                _changed = null;
            }
        }
    }

    private sealed record EditCaptureLossProbeResult(
        OverlayEditGestureKind ExpectedKind,
        OverlayEditPreviewEventArgs? Preview,
        OverlayEditPreviewEventArgs? Completed,
        int CompletionCount,
        bool ActiveBeforeInterruption,
        bool ActiveAfterInterruption,
        bool ActiveAfterRepeatedSignals,
        bool CaptureAfterInterruption,
        int CancelRequestCount);

    private sealed record FormProbeCaseResult(
        string Name,
        bool WsExToolWindowPresent,
        bool WsExNoActivatePresent,
        bool WsExTransparentPresent,
        bool CsDropShadowPresent,
        int MouseActivateResult,
        int CapsuleCenterHitTest,
        int PanelCenterHitTest,
        int TopLeftHitTest,
        IntRect CollapsedBounds,
        IntRect ExpandedBounds,
        int ExpandedSetBoundsCoreDelta,
        bool ExpandedRegionMatchesUnion,
        int NormalCapsuleClickCount,
        bool NormalCommandIntercepted,
        OverlayRenderDecorationState NormalRenderDecorations,
        bool BeginEditRejectsExpanded,
        bool EditWsExToolWindowPresent,
        bool EditWsExNoActivatePresent,
        int EditMouseActivateResult,
        bool EditIsCollapsed,
        int EditCapsuleClickCount,
        IntRect EditResizeHandle60,
        IntRect EditResizeHandle100,
        IntRect EditResizeHandle130,
        OverlayRenderDecorationState EditRenderDecorations60,
        OverlayRenderDecorationState EditRenderDecorations100,
        OverlayRenderDecorationState EditRenderDecorations130,
        OverlayEditPreviewEventArgs? MovePreview,
        IntRect MovePreviewBounds,
        bool MovePreservedSize,
        OverlayEditPreviewEventArgs MinimumResizePreview,
        OverlayEditPreviewEventArgs MaximumResizePreview,
        EditCaptureLossProbeResult LostMoveCapture,
        EditCaptureLossProbeResult LostResizeCapture,
        EditCaptureLossProbeResult CancelCapture,
        EditCaptureLossProbeResult DisposeCapture,
        int EditGestureCompletionCount,
        int SaveRequestCount,
        int CancelRequestCount,
        bool RestoredWsExNoActivatePresent,
        int RestoredMouseActivateResult,
        OverlayRenderMetrics Metrics60,
        OverlayRenderMetrics Metrics100,
        OverlayRenderMetrics Metrics130,
        bool HighlightWsExToolWindowPresent,
        bool HighlightWsExNoActivatePresent,
        bool HighlightWsExTransparentPresent,
        bool HighlightShowInTaskbar,
        int HighlightSetBoundsCoreDelta,
        IntRect HighlightBounds,
        int HighlightDeviceDpi,
        int HighlightExpectedRingThicknessPixels,
        bool HighlightHasRingRegion,
        int HighlightHitTest,
        bool HighlightHiddenAfterClear,
        bool HighlightRegionCleared);

    private const int GwlExStyle = -20;
    private const int GclStyle = -26;
    private const long WsExTransparent = 0x00000020;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long CsDropShadow = 0x00020000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW")]
    private static extern uint GetClassLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam);
}
