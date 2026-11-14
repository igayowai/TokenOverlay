using System.Runtime.InteropServices;

namespace TokenOverlay;

internal sealed partial class OverlayContext : ApplicationContext
{
    private readonly OverlaySettings _settings;
    private readonly VisibleThreadRouteMonitor _routeMonitor;
    private readonly TokenLogMonitor _monitor;
    // One reader per recently shown Codex chat: looking at another chat for a moment keeps how far each was read.
    private readonly List<TurnStatisticsReader> _turnReaders = new();

    private TurnStatisticsReader TurnReaderFor(string path)
    {
        var reader = _turnReaders.FirstOrDefault(r => string.Equals(r.LogPath, path, StringComparison.OrdinalIgnoreCase)) ?? new TurnStatisticsReader();
        _turnReaders.Remove(reader);
        _turnReaders.Insert(0, reader);
        if (_turnReaders.Count > 4) _turnReaders.RemoveAt(4);
        return reader;
    }
    private readonly TelemetryStore _telemetry = new();
    private readonly TelemetryReceiver _telemetryReceiver;
    private readonly WeeklyUsageMonitor _weekly;
    private readonly CodexWeeklyMonitor _codexWeekly;
    private readonly CacheWatch _cacheWatch = new();
    private readonly DriveShareLink _driveLink;
    private readonly ClaudeDesktopSource _claude;
    private readonly DeepSeekRateSource _deepSeek = DeepSeekRateSource.ForThisPc();
    private string? _pendingVisibleTitle;
    private HostApp _pendingApp;
    private IReadOnlyList<ClaudePane> _pendingPanes = [];
    // Split view: the main capsule follows the focused pane; these show the other panes' sessions (display only).
    private readonly List<TokenStripForm> _paneForms = new();
    // Ctrl held with the pointer over the capsule lists the recent turns below it.
    private readonly System.Windows.Forms.Timer _historyTimer = new() { Interval = 80 };
    private bool _historyMode;
    // Pinned list: stays open without Ctrl and regardless of which app is in front, so any capture tool can take it.
    private DateTime? _historyPinnedUntil;
    private static readonly TimeSpan HistoryPinDuration = TimeSpan.FromSeconds(60);
    private bool HistoryPinned => _historyMode && _historyPinnedUntil is { } until && DateTime.UtcNow < until;
    private DateTime _lastTelemetrySave = DateTime.UtcNow;
    // Set by "TokenOverlay.exe --quit": leave through the normal exit path so the collected request times are saved.
    internal const string QuitEventName = @"Local\TokenOverlayQuit";
    private readonly EventWaitHandle _quitEvent = new(false, EventResetMode.ManualReset, QuitEventName);
    private int _telemetrySaveInFlight;

    private void SaveTelemetryIfDue()
    {
        if (!_telemetry.Dirty || DateTime.UtcNow - _lastTelemetrySave < TimeSpan.FromSeconds(10)
            || Interlocked.CompareExchange(ref _telemetrySaveInFlight, 1, 0) != 0) return;
        _lastTelemetrySave = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            try { _telemetry.SaveTo(TelemetryStore.DefaultCachePath); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
            finally { Interlocked.Exchange(ref _telemetrySaveInFlight, 0); }
        });
    }
    private readonly ThreadTitleReader _titles;
    private readonly TokenStripForm _form = new();
    private readonly AttachmentTargetHighlightForm _targetHighlight = new();
    private readonly OverlayThemeBinding _themeBinding;
    private readonly PreferredOverlayThemeSource _theme;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _outsideClickTimer;
    private readonly ToolStripMenuItem _sessionMenuItem = new("채팅: 확인 중") { Enabled = false };
    private readonly ToolStripMenuItem _visibilityMenuItem = new("잠시 숨기기");
    private readonly ToolStripMenuItem _pinSessionMenuItem = new("채팅 고정") { Enabled = false, CheckOnClick = true, Visible = false };
    private readonly ToolStripMenuItem _adjustManualMenuItem = new("위치·크기 조절…");
    private readonly ToolStripMenuItem _saveManualMenuItem = new("조절 완료") { Visible = false };
    private readonly ToolStripMenuItem _cancelManualMenuItem = new("조절 취소") { Visible = false };
    private readonly ToolStripMenuItem _resetManualMenuItem = new("기본 위치로 복원");
    private readonly ToolStripMenuItem _traditionalMenuItem = new("배치");
    private readonly Dictionary<(HostApp App, AnchorMode Mode), ToolStripMenuItem> _anchorItems = new();
    private readonly OverlayInteractionState _interaction = new();
    private readonly ActiveRouteThreadState _activeRouteThread = new();
    private readonly OverlayAnchorTargetState _anchorTargetState = new();
    private readonly ManualAttachmentCoordinator _manualAttachment = new();
    private readonly string? _settingsPath;
    private OverlayPresentation _presentation;
    private CodexWindowTarget? _currentTarget;
    private ManualPlacementSnapshot? _settingsSnapshotBeforeEdit;
    private bool _saveFailureNotified;
    private TokenSnapshot? _lastSnapshot;
    private bool _manuallyHidden;
    private int _pollInFlight;
    private int _disposed;
    private TokenSnapshot? _pendingSnapshot;
    private long _pendingSessionVersion = -1;
    private string? _pendingThreadId;
    private long _observedSessionVersion = -1;
    private string? _observedThreadId;
    private ActiveThreadRouteStatus _pendingRouteStatus = new(null, 0, false, 0, null);
    private long _observedRouteVersion = -1;
    private DateTime _lastDiagnostic;

    public OverlayContext(string sessionRoot, string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _telemetry.LoadFrom(TelemetryStore.DefaultCachePath);
        _telemetryReceiver = new TelemetryReceiver(_telemetry);
        _telemetryReceiver.Start();
        _settings = OverlaySettings.Load(_settingsPath);
        // The user's own devices tell each other when they were busy through Google Drive, when it is installed.
        _driveLink = new DriveShareLink(_settings.DriveShareEnabled, _settings.DriveShareFolder);
        // Which Claude and Codex account is in use, read once before the weeks start so they know it from their first pass.
        ObserveAccounts(announce: false);
        _weekly = new WeeklyUsageMonitor(_driveLink.Current, _accounts);
        _claude = new ClaudeDesktopSource(_telemetry, _weekly);
        _weekly.Start();
        _routeMonitor = new VisibleThreadRouteMonitor(sessionRoot);
        _monitor = new TokenLogMonitor(sessionRoot);
        _titles = new ThreadTitleReader(sessionRoot);
        _codexWeekly = new CodexWeeklyMonitor(sessionRoot, _driveLink.Current, _accounts);
        _codexWeekly.Start();
        _openCodeGo.Start();
        _openCodeGo2.Start();
        _openCodeGo3.Start();
        _anthropicCredits.Start();
        DriveShare.DeviceAdded += OnDeviceAdded;
        _cacheWatch.Enabled = _settings.CacheWarningEnabled;
        _cacheWatch.Warned += OnCacheWarned;
        _presentation = OverlayPresentationBuilder.CreateWaiting(
            "현재 채팅 확인 중…",
            _settings.CollapsedPrimaryField,
            _settings.CollapsedSecondaryField,
            _settings.VisibleFields);
        _ = _targetHighlight.Handle;
        _theme = new PreferredOverlayThemeSource(new WindowsOverlayThemeSource(), _settings.Theme);
        _themeBinding = new OverlayThemeBinding(
            _targetHighlight,
            _theme,
            ApplyTheme);

        var menu = BuildTrayMenu();

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIcon.ForTray(),
            Text = TrayText(AppVersion.Text, _updater.TrayLine),
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.BalloonTipShown += (_, _) => { _cacheBalloonShown = _cacheBalloonNext; _cacheBalloonNext = false; };
        _trayIcon.BalloonTipClicked += (_, _) => { if (_cacheBalloonShown) ShowCachePing(); };
        if (AppUpdater.RestartedInto is { } version)
            _trayIcon.ShowBalloonTip(4000, TrayName, $"{version}(으)로 업데이트됨", ToolTipIcon.Info);
        else if (AppUpdater.TakeSetupOutcome() is { } outcome)
        {
            DiagnosticLog.Info("update", "setup file: " + outcome);
            _trayIcon.ShowBalloonTip(5000, TrayName, outcome, ToolTipIcon.Info);
        }

        _form.SetPresentation(_presentation);
        _form.CapsuleClicked += HandleCapsuleClicked;
        _form.PingClicked += HandlePingClicked;
        _form.KeepClicked += HandleKeepClicked;
        _form.CompactClicked += HandleCompactClicked;
        // The week's login line: the same sign-in window as the tray menu's, at most one every 10 seconds.
        var loginOpened = DateTime.MinValue;
        _form.LoginClicked += (_, _) =>
        {
            if (DateTime.UtcNow - loginOpened < TimeSpan.FromSeconds(10)) return;
            loginOpened = DateTime.UtcNow;
            if (!ClaudeTokenRenewal.OpenLoginWindow())
                NotifySaveFailure("Claude 앱에 포함된 Claude Code를 찾지 못했음. Claude 데스크톱 앱의 Code 탭을 한 번 연 뒤 다시 시도.");
        };
        _form.EditPreviewChanged += HandleEditPreviewChanged;
        _form.EditGestureCompleted += HandleEditGestureCompleted;
        _form.EditSaveRequested += (_, _) => SaveManualEditing();
        _form.EditCancelRequested += (_, _) => CancelManualEditing();
        UpdateAnchorChecks();
        UpdateManualMenuState();

        if (_settingsPath is null) ScheduleFirstRunSetup();
        _timer = new System.Windows.Forms.Timer { Interval = 350 };
        _quitEvent.Reset();
        _timer.Tick += (_, _) =>
        {
            if (_quitEvent.WaitOne(0)) { _quitEvent.Reset(); ExitOverlay(); return; }
            Tick(); WriteDiagnostics(); SaveTelemetryIfDue();
        };
        _outsideClickTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _outsideClickTimer.Tick += (_, _) => PollOutsidePointer();
        _timer.Start();
        _historyTimer.Tick += (_, _) => PollHistoryGesture();
        _historyTimer.Start();
    }

    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        SampleCpu();
        WatchAccounts();

        if (_autoUpdate.Observe(_updater.ReadyVersion is not null && !_updater.Blocked, UpdateWouldInterrupt(), DateTime.UtcNow))
        {
            if (_updater.TryAutoApply())
            {
                DiagnosticLog.Info("update", "put in by itself after a quiet minute");
                ExitOverlay();
                return;
            }
            ShowUpdateBlockedNotice();
        }

        RequestBackgroundPoll();

        // Pinned list or a capture tool in front: freeze what is shown, contents included, so the capture gets it.
        if (_form.Visible && !_manuallyHidden && (HistoryPinned || ScreenCaptureTools.IsForeground()))
        {
            return;
        }

        if (_pendingRouteStatus.Version != _observedRouteVersion)
        {
            _observedRouteVersion = _pendingRouteStatus.Version;
            UpdateSessionMenuText();
        }

        if (_activeRouteThread.ObserveAndCollapse(_pendingRouteStatus, _interaction))
        {
            StopOutsideClickPolling();
        }

        var activeThreadChanged = !string.Equals(
            _pendingThreadId,
            _observedThreadId,
            StringComparison.OrdinalIgnoreCase);
        if (_pendingSessionVersion != _observedSessionVersion)
        {
            _observedSessionVersion = _pendingSessionVersion;
            _observedThreadId = _pendingThreadId;
            _lastSnapshot = null;
            var shortPendingId = string.IsNullOrWhiteSpace(_pendingThreadId)
                ? "확인 중"
                : OverlayPresentationBuilder.ShortThreadId(_pendingThreadId);
            _pinSessionMenuItem.Enabled = !string.IsNullOrWhiteSpace(_pendingThreadId);
            _presentation = OverlayPresentationBuilder.CreateWaiting(
                $"채팅 {shortPendingId} 집계 대기…",
                _settings.CollapsedPrimaryField,
                _settings.CollapsedSecondaryField,
                _settings.VisibleFields) with { App = _pendingApp, HistoryFooter = WeeklyFooter(_pendingApp), Sessions = SessionsFor(_pendingApp) };
            _presentation = WithFreeTokens(_presentation);
            _form.SetPresentation(_presentation);
            UpdateSessionMenuText();
        }

        if (!_pendingRouteStatus.IsConnected || _pendingRouteStatus.ActiveWindowCount != 1
            || string.IsNullOrWhiteSpace(_pendingRouteStatus.ThreadId))
        {
            _lastSnapshot = null;
            _presentation = OverlayPresentationBuilder.CreateWaiting(
                "현재 채팅 확인 대기", DisplayField.Total, DisplayField.ContextPercent, DisplayField.Total)
                with { App = _pendingApp, HistoryFooter = WeeklyFooter(_pendingApp), Sessions = SessionsFor(_pendingApp) };
            _presentation = WithFreeTokens(_presentation);
            _form.SetPresentation(_presentation);
        }
        var snapshot = _pendingRouteStatus.IsConnected && _pendingSnapshot?.ThreadId == _pendingRouteStatus.ThreadId
            ? _pendingSnapshot : null;
        if (snapshot is not null && snapshot != _lastSnapshot)
        {
            _lastSnapshot = snapshot;
            RefreshPresentation();
            _pinSessionMenuItem.Enabled = true;
            UpdateSessionMenuText();
        }

        if (_manualAttachment.IsEditing)
        {
            if (_currentTarget is null
                || !CodexWindowLocator.TryRefreshKnownCodexTarget(
                    _currentTarget,
                    out var refreshedTarget))
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
                CollapseAndHide();
                return;
            }

            _currentTarget = refreshedTarget;
            HidePaneForms();
            if (_manualAttachment.ShouldApplyStaticDraft)
            {
                ApplyEditDraftLayout(refreshedTarget);
            }
            UpdateManualMenuState();
            return;
        }

        // Keep the overlay exactly as it is while it is pinned or a screen-capture tool is in front.
        if (_form.Visible && !_manuallyHidden && (HistoryPinned || ScreenCaptureTools.IsForeground()))
        {
            return;
        }

        if (_manuallyHidden || !CodexWindowLocator.TryGetForegroundCodexTarget(out var target))
        {
            CollapseAndHide();
            UpdateManualMenuState();
            return;
        }

        if (activeThreadChanged)
        {
            _interaction.CollapseForHostChange();
            StopOutsideClickPolling();
        }

        _currentTarget = target;
        ApplyLayout(target);
        UpdatePaneForms(target);
        UpdateManualMenuState();
    }

    private CodexWindowTarget InFocusedPane(CodexWindowTarget target) =>
        target.App == HostApp.Claude && _pendingPanes.FirstOrDefault(p => p.Focused) is { } pane
            ? target with { HostWindow = pane.Host(target.HostWindow) } : target;

    private void UpdatePaneForms(CodexWindowTarget target)
    {
        var others = target.App == HostApp.Claude
            ? _pendingPanes.Where(p => !p.Focused && p.Snapshot is not null).ToArray() : [];
        // No pane matched the focused chat: the main capsule stays at the window corner, over the rightmost pane.
        if (!_pendingPanes.Any(p => p.Focused)) others = others.Where(p => p.RightInset != 0 || p.TopInset != 0).ToArray();
        while (_paneForms.Count < others.Length)
        {
            var form = new TokenStripForm();
            form.ApplyTheme(_form.CurrentThemePalette);
            _paneForms.Add(form);
        }
        for (var i = 0; i < _paneForms.Count; i++)
        {
            var form = _paneForms[i];
            if (i >= others.Length) { form.Hide(); continue; }
            var paneTarget = target with { HostWindow = others[i].Host(target.HostWindow) };
            Point? manualCenter = null;
            if (_settings.ManualPlacementEnabled)
            {
                manualCenter = ManualAttachmentCoordinator.ResolveCenter(SnapshotFromSettings(), CreateAttachmentTargets(paneTarget));
                if (manualCenter is null) { form.Hide(); continue; }
            }
            var presentation = Present(others[i].Snapshot);
            form.SetPresentation(presentation);
            var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
                paneTarget.HostWindow, InnerAnchor(_settings.AnchorFor(paneTarget.App), others[i]), false, presentation.ExpandedRows.Count,
                presentation.ShowContextProgress, manualCenter, _settings.OverlayScalePercent, presentation));
            form.ApplyLayout(layout);
            if (layout.State == OverlayVisualState.HiddenForSpace) form.Hide();
            else if (!form.Visible && !form.HoverHidden) form.Show();
        }
    }

    private void HidePaneForms()
    {
        foreach (var form in _paneForms) form.Hide();
    }

    private void RequestBackgroundPoll()
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
        {
            return;
        }

        var uiScheduler = TaskScheduler.FromCurrentSynchronizationContext();
        _ = Task.Run(() =>
            {
                // Each app has its own collector; a failure in one never feeds the other's display.
                if (CodexWindowLocator.TryGetForegroundCodexTarget(out var foreground)
                    && foreground.App == HostApp.Claude)
                {
                    var claude = _claude.Poll(foreground);
                    // The DeepSeek line is extra: a failure reading DSH's sessions never stops the Claude capsule.
                    DeepSeekTurn? deepSeek = null;
                    try
                    {
                        // Only a task the shown session started, itself or through one of its sub-agents.
                        if (claude.Snapshot is { } calling)
                            deepSeek = _deepSeek.Latest(DateTimeOffset.UtcNow,
                                [.. calling.WorkerCalls, .. _deepSeek.SubagentCalls(calling.LogPath, DateTimeOffset.UtcNow)]);
                    }
                    catch (Exception e) { DiagnosticLog.Error("deepseek", e); }
                    return (
                        Snapshot: claude.Snapshot is { } shown ? shown with { DeepSeek = deepSeek } : null,
                        Version: claude.SessionVersion,
                        ThreadId: claude.Route.ThreadId,
                        RouteStatus: claude.Route,
                        VisibleTitle: _claude.VisibleTitle,
                        App: HostApp.Claude,
                        Panes: claude.Panes);
                }

                var routeStatus = _routeMonitor.Poll();
                var known = routeStatus.IsConnected && routeStatus.ActiveWindowCount == 1
                    && !string.IsNullOrWhiteSpace(routeStatus.ThreadId);
                _monitor.PreferredThreadId = known ? routeStatus.ThreadId : null;
                var snapshot = known ? _monitor.Poll() : null;
                if (snapshot is not null)
                {
                    var turnReader = TurnReaderFor(snapshot.LogPath);
                    var turn = turnReader.Read(snapshot.LogPath);
                    CodexCacheLog.Append(turnReader.TakeRequests());
                    if (turn is { Seconds: not null })
                        turn = turn.With(_telemetry.CodexTurn(turn.TurnId, turnReader.DisplayedResponses));
                    var history = new TurnHistory(turnReader.Recent(TurnHistory.Size)
                        .Select(recent => recent.Turn.Seconds is null
                            ? recent.Turn
                            : recent.Turn.With(_telemetry.CodexTurn(recent.Turn.TurnId, recent.Responses))).ToArray());
                    snapshot = snapshot with { Turn = turn, ThreadTitle = _titles.Read(snapshot.ThreadId), History = history,
                        HistoryFooter = CacheStatus.JoinLines(CacheStatus.Line(turnReader.Cache, DateTimeOffset.UtcNow), _codexWeekly.Footer),
                        CacheNote = CacheStatus.CapsuleNote(turnReader.Cache, DateTimeOffset.UtcNow),
                        CacheAlert = CacheStatus.CapsuleAlert(turnReader.Cache, DateTimeOffset.UtcNow),
                        CacheBar = CacheStatus.CapsuleBar(turnReader.Cache, DateTimeOffset.UtcNow),
                        ContextFill = ContextUsage.CodexFill(snapshot), RingNote = ContextUsage.Note(turn?.Model, snapshot.ContextWindowTokens) };
                }
                return (
                    Snapshot: snapshot,
                    Version: _monitor.ActiveSessionVersion,
                    ThreadId: known ? _monitor.ActiveThreadId : null,
                    RouteStatus: routeStatus,
                    VisibleTitle: _routeMonitor.VisibleTitle,
                    App: foreground?.App ?? HostApp.None,
                    Panes: (IReadOnlyList<ClaudePane>)Array.Empty<ClaudePane>());
            })
            .ContinueWith(task =>
            {
                var switched = false;
                try
                {
                    if (Volatile.Read(ref _disposed) == 0 && task.Status == TaskStatus.RanToCompletion)
                    {
                        // Another chat in front, or its numbers just read in: shown now, not at the next tick.
                        switched = task.Result.Version != _pendingSessionVersion
                            || task.Result.RouteStatus.Version != _pendingRouteStatus.Version
                            || task.Result.App != _pendingApp
                            || (task.Result.Snapshot?.Turn is null) != (_pendingSnapshot?.Turn is null);
                        _pendingSnapshot = task.Result.Snapshot;
                        _pendingSessionVersion = task.Result.Version;
                        _pendingThreadId = task.Result.ThreadId;
                        _pendingRouteStatus = task.Result.RouteStatus;
                        _pendingVisibleTitle = task.Result.VisibleTitle;
                        _pendingApp = task.Result.App;
                        _pendingPanes = task.Result.Panes;
                        LogPollState(task.Result.App, task.Result.RouteStatus, task.Result.VisibleTitle, task.Result.Snapshot);
                    }
                    else if (task.IsFaulted && task.Exception is { } failure)
                        DiagnosticLog.Error("poll", failure.GetBaseException());
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
                // Also starts the next poll at once, so the chat that follows a switch is looked up without a wait.
                if (switched) Tick();
            }, CancellationToken.None, TaskContinuationOptions.None, uiScheduler);
    }

    private DeviceManagerForm? _deviceManager;

    // Something the user is doing or watching that a restart would cut off.
    private bool UpdateWouldInterrupt() =>
        _lastSnapshot?.Turn?.Running == true || _manualAttachment.IsEditing || _historyMode || HistoryPinned
        || _trayIcon.ContextMenuStrip?.Visible == true || _deviceManager is { IsDisposed: false, Visible: true };
    private readonly AppUpdater _updater = new();
    private readonly AutoUpdateGate _autoUpdate = new();
    private readonly System.Windows.Forms.Timer _updateTimer = new();

    private void ShowDeviceManager()
    {
        if (_deviceManager is { IsDisposed: false })
        {
            Reveal(_deviceManager);
            return;
        }
        _deviceManager = new DeviceManagerForm(_driveLink, folder =>
        {
            _settings.DriveShareFolder = folder;
            if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("공유 폴더 설정을 저장하지 못했음.");
            _driveLink.FolderOverride = folder;
            DiagnosticLog.Info("share", folder is null ? "share folder back to automatic" : "share folder picked by hand");
            Task.Run(() => { _codexWeekly.Refresh(); _weekly.Refresh(); });
        });
        _deviceManager.FormClosed += (_, _) => { _deviceManager = null; Task.Run(() => { _codexWeekly.Refresh(); _weekly.Refresh(); }); };
        _deviceManager.Show();
    }

    // A window already open is brought back as it is, even one left hidden or minimised: Activate alone does nothing
    // for a hidden window, so the menu seemed dead (seen 2026-10-25 with the device manager).
    internal static void Reveal(Form window)
    {
        if (!window.Visible)
        {
            DiagnosticLog.Info("ui", $"{window.Text}: was hidden, shown again");
            window.Show();
        }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }

    // A device seen for the first time in the share folder: say so once, from the UI thread.
    private void OnDeviceAdded(string name)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_form.IsHandleCreated) return;
            _form.BeginInvoke(() => _trayIcon?.ShowBalloonTip(5000, "새 기기 연결됨",
                $"{name} · 내 기기로 묶였음. 다른 사람 기기면 설정 → 내 기기 관리에서 주인을 정하면 됨.", ToolTipIcon.Info));
        }
        catch (InvalidOperationException) { }
    }

    // Uncompacted Claude sessions about to lose a large cache: one tray notice for all of them, from the UI thread.
    private void OnCacheWarned(IReadOnlyList<CacheWatch.Warning> due)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_form.IsHandleCreated) return;
            var (title, text) = CacheWatch.Notice(due);
            DiagnosticLog.Info("cache", $"lapse notice for {due.Count} session(s)");
            _form.BeginInvoke(() =>
            {
                _cacheBalloonNext = true;
                _trayIcon?.ShowBalloonTip(10000, title, text, ToolTipIcon.Warning);
            });
        }
        catch (InvalidOperationException) { }
    }

    // Which balloon is up: every balloon raises the same events, so the cache notice marks itself just before it shows.
    private bool _cacheBalloonNext, _cacheBalloonShown;
    private CachePingForm? _cachePing;

    private void ShowCachePing()
    {
        if (_cachePing is { IsDisposed: false })
        {
            Reveal(_cachePing);
            return;
        }
        _cachePing = new CachePingForm(_cacheWatch, pings: _cachePings);
        _cachePing.FormClosed += (_, _) => _cachePing = null;
        _cachePing.Show();
    }

    // The context ring and the rate lines are left out while they are switched off in the settings.
    private OverlayPresentation Present(TokenSnapshot? snapshot)
    {
        var shown = TurnPresentation.ChooseLines(TurnPresentation.Create(snapshot), _settings.CacheHitLineEnabled, _settings.ProviderLineEnabled);
        shown = WithFreeTokens(shown with { Sessions = SessionsFor(shown.App), CpuMeter = _settings.CpuMeterEnabled });
        return _settings.ContextRingEnabled ? shown : shown with { ContextFill = null, RingNote = null };
    }

    // The Ctrl list's table holds OpenCode Go's windows, no longer the recent turns, and the week's reset line the
    // Anthropic Console's dollars left. OpenAI's complimentary tokens were taken out again (user, 2026-11-12: "openai는
    // 빼자 저거는 유료청구때문에 못쓰겠다", after a day's limit passed went to the paid tier); OpenAiFreeTokens.cs stays
    // with its checks, not started. Without a key file its part is left out.
    private readonly OpenCodeGoMonitor _openCodeGo = new();
    // The second Go account, shown above the first (user, 2026-11-14).
    private readonly OpenCodeGoMonitor _openCodeGo2 = new(KeyFile.InAppData("opencode-go-key-2.dpapi"), "OpenCode Go 2", "opencode-go-2");
    // The third, above the second (user, 2026-11-15: "오픈코드 계정 3도 만듦 ... 오픈코드2 위에 붙여줘").
    private readonly OpenCodeGoMonitor _openCodeGo3 = new(KeyFile.InAppData("opencode-go-key-3.dpapi"), "OpenCode Go 3", "opencode-go-3");
    private readonly AnthropicCreditMonitor _anthropicCredits = new();

    private FreeTokenTable[] GoTables() =>
        new[] { _openCodeGo3.Table, _openCodeGo2.Table, _openCodeGo.Table }.OfType<FreeTokenTable>().ToArray();

    private OverlayPresentation WithFreeTokens(OverlayPresentation p) =>
        WithTables(p, GoTables(), _anthropicCredits.Left);

    // A PC without the Go key (a friend's) keeps the recent turns, as before (user, 2026-11-13, before 0.10.4: "키
    // 없으면 최근 턴").
    internal static OverlayPresentation WithTables(OverlayPresentation p, IReadOnlyList<FreeTokenTable> go, string? credit) =>
        p with { History = go.Count == 0 ? p.History : [], GoTables = go, CreditLeft = credit };

    // New numbers read since the list was last built: put them in, and lay the open list out again.
    private void RefreshFreeTokens()
    {
        var go = GoTables();
        var credit = _anthropicCredits.Left;
        var shown = _presentation.GoTables;
        if ((go.Length == 0 || _presentation.History.Count == 0)
            && go.Length == shown.Count && go.Select((table, i) => table.Same(shown[i])).All(same => same)
            && credit == _presentation.CreditLeft) return;
        _presentation = WithFreeTokens(_presentation);
        _form.SetPresentation(_presentation);
        if (_historyMode && _currentTarget is not null && _form.Visible) ApplyLayout(_currentTarget);
    }

    // Claude in front: the Ctrl list shows these sessions instead of the recent turns, as last read while it was open.
    // Without the mod running on this PC nothing could be pinged, so the list keeps the turns.
    private readonly CachePings _cachePings = new();
    private IReadOnlyList<SessionRow> _sessionRows = [];
    private bool _modHere;
    private DateTime _sessionScanAt;
    private int _sessionTicks;

    private IReadOnlyList<SessionRow>? SessionsFor(HostApp app) => app == HostApp.Claude && _modHere ? _sessionRows : null;

    // The rows again, after a fresh scan off the UI thread now and then (the watch's own scans stop while its notice is
    // switched off); redrawn only when something shown changed.
    private bool RefreshSessionRows(bool scan)
    {
        if (scan && DateTime.UtcNow - _sessionScanAt > TimeSpan.FromSeconds(20))
        {
            _sessionScanAt = DateTime.UtcNow;
            _ = Task.Run(_cacheWatch.ScanNow);
        }
        var now = DateTimeOffset.Now;
        var rows = _cacheWatch.Sessions.Where(s => s.Shown(now))
            .Select(s => _cachePings.Line(s, now) is var line
                ? new SessionRow(s.Id, s.Name, line.Left, line.Soon, line.State, line.CanPing, line.Keep is not null, line.CanKeep,
                    line.CanCompact, line.Arming) : null!)
            .ToArray();
        var modHere = CacheWatch.PartnerRunning(now);
        if (rows.SequenceEqual(_sessionRows) && modHere == _modHere) return true;
        _sessionRows = rows;
        _modHere = modHere;
        if (_presentation.App != HostApp.Claude) return true;
        _presentation = _presentation with { Sessions = SessionsFor(HostApp.Claude) };
        _form.SetPresentation(_presentation);
        if (_historyMode && _currentTarget is not null && _form.Visible) ApplyLayout(_currentTarget);
        return true;
    }

    private void HandleKeepClicked(object? sender, string sessionId)
    {
        var on = _sessionRows.FirstOrDefault(row => row.Id == sessionId) is not { Keeping: true };
        try { _cachePings.Keep(sessionId, on, DateTimeOffset.Now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info("cache", "keep switch not written: " + e.GetType().Name);
        }
        RefreshSessionRows(scan: false);
    }

    private void HandleCompactClicked(object? sender, string sessionId)
    {
        try { _cachePings.Compact(sessionId, DateTimeOffset.Now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info("cache", "compact request not written: " + e.GetType().Name);
        }
        RefreshSessionRows(scan: false);
    }

    private void HandlePingClicked(object? sender, string sessionId)
    {
        try { _cachePings.Ask(sessionId, DateTimeOffset.Now); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Info("cache", "ping request not written: " + e.GetType().Name);
        }
        RefreshSessionRows(scan: false);
    }

    // The meters' readings (CPU, memory, page file), about once a second (Task Manager's pace) while they are switched on.
    private readonly CpuUsage _cpu = new();
    private readonly GpuSensor _gpu = new();
    // The line over the meters: read on its own thread every 2 seconds while the meters are on.
    private readonly CpuTop _cpuTop = new();
    private long _cpuAt;

    private void SampleCpu(bool now = false)
    {
        _cpuTop.Enabled = _settings.CpuMeterEnabled;
        if (!_settings.CpuMeterEnabled) return;
        var at = Environment.TickCount64;
        if (!now && at - _cpuAt < 950) return;
        _cpuAt = at;
        _form.SetMeters(_cpu.Sample(), CpuUsage.Memory(), _gpu.Read());
        var logo = _cpuTop.Logo;
        _form.SetTopLine(_cpuTop.Text, logo);
    }

    private void RefreshPresentation()
    {
        _presentation = Present(_lastSnapshot);
        _form.SetPresentation(_presentation);
    }

    private void ApplyLayout(CodexWindowTarget target)
    {
        target = InFocusedPane(target);
        Point? manualCenter = null;
        if (_settings.ManualPlacementEnabled)
        {
            var snapshot = SnapshotFromSettings();
            var targets = CreateAttachmentTargets(target);
            manualCenter = ManualAttachmentCoordinator.ResolveCenter(snapshot, targets);
            if (manualCenter is null)
            {
                return;
            }

            if (_anchorTargetState.ObserveAndCollapse(
                target.HostWindow.Handle.ToInt64(),
                snapshot.MainAttachment.ReferencePoint,
                _interaction))
            {
                StopOutsideClickPolling();
            }
        }
        else if (_anchorTargetState.ObserveAndCollapse(
            target.HostWindow.Handle.ToInt64(),
            AttachmentReferencePoint.TopLeft,
            _interaction))
        {
            StopOutsideClickPolling();
        }

        var layout = CalculateLayout(target, manualCenter);
        if (_historyMode && layout.State != OverlayVisualState.Expanded)
        {
            // No room for the list: fall back to the plain capsule.
            _historyMode = false;
            _form.SetShowHistory(false);
        }
        if (_interaction.State == OverlayVisualState.Expanded
            && layout.State != OverlayVisualState.Expanded)
        {
            _interaction.CollapseForExpandedLayoutFailure();
            StopOutsideClickPolling();
            layout = CalculateLayout(target, manualCenter);
        }

        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _interaction.HideForSpace();
            StopOutsideClickPolling();
        }
        else
        {
            _interaction.RestoreAfterSpace();
        }

        _form.ApplyLayout(layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible && !_form.HoverHidden)
        {
            _form.Show();
        }

        UpdateOutsideClickPolling();
    }

    private OverlayLayoutResult CalculateLayout(CodexWindowTarget target, Point? manualCenter)
    {
        var chosen = InnerAnchor(_settings.AnchorFor(target.App),
            target.App == HostApp.Claude ? _pendingPanes.FirstOrDefault(p => p.Focused) : null);
        var anchor = target.App == HostApp.Codex
            ? CodexAnchor(chosen, IsCodexHomeTitle(_pendingVisibleTitle), target.HostWindow.Maximized) : chosen;
        var request = CreateLayoutRequest(target.HostWindow, anchor, manualCenter);
        if (target.App == HostApp.Codex) request = request with { HeaderOffsetDip = OverlayLayoutCalculator.CodexHeaderOffsetDip };
        return OverlayLayoutCalculator.Calculate(request);
    }

    // Each app uses its own configured anchor. Codex's bottom right moves to the top right (under the header row) on its
    // home screen (new chat), whose wide composer and model picker fill that corner, and in a window that is not
    // maximized, where the capsule would cover the composer (user's choice, 2026-11-08). The home page is titled with
    // the app name; a chat page carries the chat's title.
    internal static AnchorMode CodexAnchor(AnchorMode chosen, bool home, bool maximized) =>
        chosen == AnchorMode.InsideBottomRight && (home || !maximized) ? AnchorMode.InsideTopRight : chosen;

    // A capsule moved inward, into a split-view pane left of another or a chat column beside a side panel, sits at
    // that area's top right rather than its bottom right, where it would cover the composer next to the boundary.
    internal static AnchorMode InnerAnchor(AnchorMode chosen, ClaudePane? pane) =>
        chosen == AnchorMode.InsideBottomRight && pane is { RightInset: not 0 } ? AnchorMode.InsideTopRight : chosen;

    // While no chat is known the Ctrl list still shows the account's weekly line for the app in front.
    private string? WeeklyFooter(HostApp app) => app switch
    {
        HostApp.Claude => _weekly.Footer,
        HostApp.Codex => _codexWeekly.Footer,
        _ => null
    };

    internal static bool IsCodexHomeTitle(string? title) =>
        title is "ChatGPT" or "Codex";

    private OverlayLayoutRequest CreateLayoutRequest(
        CodexWindowInfo hostWindow,
        AnchorMode anchor,
        Point? manualCenter = null) => new(
        hostWindow,
        anchor,
        _interaction.State == OverlayVisualState.Expanded || _historyMode,
        _historyMode ? _presentation.HistoryPanelRows : _presentation.ExpandedRows.Count,
        !_historyMode && _presentation.ShowContextProgress,
        manualCenter,
        _settings.OverlayScalePercent,
        _presentation,
        CompactPanel: _historyMode);

    private void ApplyTheme(OverlayThemePalette palette)
    {
        _form.ApplyTheme(palette);
        foreach (var form in _paneForms) form.ApplyTheme(palette);
        _targetHighlight.ApplyTheme(palette);
    }

    private static string TrimTrayText(string value) =>
        value.Length <= 63 ? value : value[..63];

    internal const string TrayName = "TokenOverlay";

    // Tray tooltip: the version running ("0.6.9", without the build's commit) and whether an update waits.
    // Windows cuts a tooltip at 127 characters.
    internal static string TrayText(string version, string updateLine)
    {
        var plus = version.IndexOf('+');
        var text = $"{TrayName} {(plus < 0 ? version : version[..plus])}\n{updateLine}";
        return text.Length <= 127 ? text : text[..127];
    }

    private static string RouteStatusSuffix(ActiveThreadRouteStatus status)
    {
        if (status.ActiveWindowCount > 1)
        {
            return $" · 여러 창 {status.ActiveWindowCount}";
        }
        return status.IsConnected ? " · 연결됨" : " · 연결 대기";
    }

    private void ExitOverlay()
    {
        if (_manualAttachment.IsEditing)
        {
            CancelManualEditing(restoreFocus: false, relayout: false);
        }
        CollapseAndHide();
        _timer.Stop();
        _outsideClickTimer.Stop();
        _trayIcon.Visible = false;
        ExitThread();
        Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing(restoreFocus: false, relayout: false);
            }
            _interaction.CollapseForHostChange();
            _timer.Stop();
            _outsideClickTimer.Stop();
            _timer.Dispose();
            _historyTimer.Dispose();
            _outsideClickTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _monitor.Dispose();
            _telemetryReceiver.Dispose();
            _weekly.Dispose();
            _codexWeekly.Dispose();
            _openCodeGo.Dispose();
            _openCodeGo2.Dispose();
            _openCodeGo3.Dispose();
            _anthropicCredits.Dispose();
            _updateTimer.Dispose();
            _cpu.Dispose();
            _gpu.Dispose();
            _cpuTop.Dispose();
            DriveShare.DeviceAdded -= OnDeviceAdded;
            _cacheWatch.Dispose();
            _deviceManager?.Dispose();
            try { if (_telemetry.Dirty) _telemetry.SaveTo(TelemetryStore.DefaultCachePath); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
            _quitEvent.Dispose();
            DisposeThemeAndForms();
        }
        base.Dispose(disposing);
    }

    private void DisposeThemeAndForms()
    {
        _themeBinding.Dispose();
        _targetHighlight.Dispose();
        _form.Dispose();
        foreach (var form in _paneForms) form.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}
