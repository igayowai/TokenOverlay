namespace TokenOverlay;

internal sealed partial class OverlayContext
{
    // The tray menu: chat status and the day-to-day actions on top, everything else under "설정".
    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_sessionMenuItem);
        _pinSessionMenuItem.CheckedChanged += (_, _) =>
        {
            _monitor.PinActiveSession = _pinSessionMenuItem.Checked;
            _pinSessionMenuItem.Text = _pinSessionMenuItem.Checked ? "채팅 고정됨" : "채팅 고정";
        };
        menu.Items.Add(new ToolStripSeparator());

        _adjustManualMenuItem.Click += (_, _) => BeginManualEditing();
        menu.Items.Add(_adjustManualMenuItem);
        _saveManualMenuItem.Click += (_, _) => SaveManualEditing();
        menu.Items.Add(_saveManualMenuItem);
        _cancelManualMenuItem.Click += (_, _) => CancelManualEditing();
        menu.Items.Add(_cancelManualMenuItem);
        _resetManualMenuItem.Click += (_, _) => ResetManualPlacement();

        // Each app keeps its own placement.
        foreach (var (app, name) in new[] { (HostApp.Claude, "Claude 배치"), (HostApp.Codex, "Codex 배치") })
        {
            var appMenu = new ToolStripMenuItem(name);
            AddAnchorMenu(appMenu, app, "제목 표시줄 오른쪽", AnchorMode.TitleBarTopRight);
            AddAnchorMenu(appMenu, app, "자동 배치", AnchorMode.Auto);
            AddAnchorMenu(appMenu, app, "창 안 오른쪽 위", AnchorMode.InsideTopRight);
            AddAnchorMenu(appMenu, app, "창 안 오른쪽 아래", AnchorMode.InsideBottomRight);
            _traditionalMenuItem.DropDownItems.Add(appMenu);
        }
        _traditionalMenuItem.DropDownItems.Add(new ToolStripSeparator());
        _traditionalMenuItem.DropDownItems.Add(_resetManualMenuItem);
        menu.Items.Add(_traditionalMenuItem);

        menu.Items.Add(new ToolStripSeparator());

        _visibilityMenuItem.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _manuallyHidden = !_manuallyHidden;
            _visibilityMenuItem.Text = _manuallyHidden ? "다시 표시" : "잠시 숨기기";
            if (_manuallyHidden)
            {
                CollapseAndHide();
            }
            else
            {
                Tick();
            }
        };
        // Top level holds what is used day to day (status, hide, move); the rest sits under "설정".
        menu.Items.Insert(2, _visibilityMenuItem);
        menu.Items.Add(BuildSettingsMenu(menu));
        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("종료");
        exitItem.Click += (_, _) => ExitOverlay();
        menu.Items.Add(exitItem);
        return menu;
    }

    private ToolStripMenuItem BuildSettingsMenu(ContextMenuStrip menu)
    {
        var settingsMenu = new ToolStripMenuItem("설정");

        var startupItem = new ToolStripMenuItem("Windows 시작 시 자동 실행");
        void RefreshStartupState()
        {
            try { startupItem.Checked = StartupRegistration.IsEnabled(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { startupItem.Checked = false; }
        }
        startupItem.Click += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled()); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { NotifySaveFailure("자동 실행 설정을 변경하지 못했음: " + e.Message); }
            RefreshStartupState();
        };
        menu.Opening += (_, _) => RefreshStartupState();
        RefreshStartupState();
        settingsMenu.DropDownItems.Add(startupItem);

        var ringItem = new ToolStripMenuItem("컨텍스트 원 표시") { Checked = _settings.ContextRingEnabled,
            ToolTipText = "캡슐 오른쪽 위에 컨텍스트 창이 얼마나 찼는지 원으로, 그 왼쪽에 모델 이름과 압축 한도를 함께 표시 (같이 켜고 꺼짐)" };
        ringItem.Click += (_, _) =>
        {
            _settings.ContextRingEnabled = !_settings.ContextRingEnabled;
            ringItem.Checked = _settings.ContextRingEnabled;
            if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("원 표시 설정을 저장하지 못했음.");
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden) ApplyLayout(_currentTarget);
        };
        settingsMenu.DropDownItems.Add(ringItem);

        var cpuItem = new ToolStripMenuItem("CPU·램·GPU 표시") { Checked = _settings.CpuMeterEnabled,
            ToolTipText = "캡슐 왼쪽 위 빈자리에 CPU·램 사용률과 그래픽카드(막대는 팬 속도, 아래 숫자는 온도 °) 막대(파랑·보라·초록)를 넣어 오버레이를 꽉 찬 직사각형으로 표시 (끄면 지금 모양)" };
        cpuItem.Click += (_, _) =>
        {
            _settings.CpuMeterEnabled = !_settings.CpuMeterEnabled;
            cpuItem.Checked = _settings.CpuMeterEnabled;
            if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("사용률 막대 설정을 저장하지 못했음.");
            RefreshPresentation();
            if (_currentTarget is not null && !_manuallyHidden) ApplyLayout(_currentTarget);
            SampleCpu(now: true);
        };
        settingsMenu.DropDownItems.Add(cpuItem);
        settingsMenu.DropDownItems.Add(AccountsMenu());

        // The rate lines under the main row, each on its own (Claude's and the worker's together).
        ToolStripMenuItem LineItem(string text, string tip, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Checked = get(), ToolTipText = tip };
            item.Click += (_, _) =>
            {
                set(!get());
                item.Checked = get();
                if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("줄 표시 설정을 저장하지 못했음.");
                RefreshPresentation();
                if (_currentTarget is not null && !_manuallyHidden) ApplyLayout(_currentTarget);
            };
            return item;
        }
        // Light or dark: as Windows' app mode, or fixed.
        var themeMenu = new ToolStripMenuItem("테마");
        foreach (var (choice, text) in new[] { (ThemePreference.Auto, "자동 (Windows 설정 따름)"), (ThemePreference.Light, "밝게"), (ThemePreference.Dark, "어둡게") })
        {
            var item = new ToolStripMenuItem(text) { Checked = _settings.Theme == choice };
            item.Click += (_, _) =>
            {
                _settings.Theme = choice;
                _theme.Preference = choice;
                foreach (ToolStripMenuItem other in themeMenu.DropDownItems) other.Checked = ReferenceEquals(other, item);
                if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("테마 설정을 저장하지 못했음.");
            };
            themeMenu.DropDownItems.Add(item);
        }
        settingsMenu.DropDownItems.Add(themeMenu);
        settingsMenu.DropDownItems.Add(LineItem("딥식 캐시히트 줄 표시", "딥식 작업 한 번의 입력 중 캐시에서 읽은 비율",
            () => _settings.CacheHitLineEnabled, on => _settings.CacheHitLineEnabled = on));
        settingsMenu.DropDownItems.Add(LineItem("딥식 제공처 표시", "딥식 작업이 부른 API 제공처(DSH 설정의 이름)",
            () => _settings.ProviderLineEnabled, on => _settings.ProviderLineEnabled = on));
        var cacheWarningItem = new ToolStripMenuItem("캐시 만료 알림") { Checked = _settings.CacheWarningEnabled,
            ToolTipText = $"압축 안 한 Claude 세션의 컨텍스트가 {CacheWatch.LargeContext / 1000}K 이상인데 캐시가 {CacheWatch.WarnMinutes}분 안에 만료되면 알림" };
        cacheWarningItem.Click += (_, _) =>
        {
            _settings.CacheWarningEnabled = !_settings.CacheWarningEnabled;
            cacheWarningItem.Checked = _cacheWatch.Enabled = _settings.CacheWarningEnabled;
            if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("캐시 만료 알림 설정을 저장하지 못했음.");
        };
        settingsMenu.DropDownItems.Add(cacheWarningItem);
        var cachePingItem = new ToolStripMenuItem("Claude 캐시 세션…") { ToolTipText = "캐시가 살아 있는 큰 세션 목록 · 골라서 핑" };
        cachePingItem.Click += (_, _) => ShowCachePing();
        settingsMenu.DropDownItems.Add(cachePingItem);
        // The partner mod inside Claude Code (pings, compaction before a lapse): off until asked for, see ClaudeModSetup.
        var claudeModItem = new ToolStripMenuItem("Claude Code 확장 (핑·자동 압축)");
        var claudeModBusy = false;
        void RefreshClaudeModState()
        {
            var state = ClaudeModSetup.Status(ClaudeTokenRenewal.FindCli());
            claudeModItem.Enabled = !claudeModBusy && state != ClaudeModSetup.State.NoClaudeCode;
            claudeModItem.Checked = state == ClaudeModSetup.State.On;
            claudeModItem.ToolTipText = state switch
            {
                ClaudeModSetup.State.NoClaudeCode => "Claude 앱에 포함된 Claude Code를 찾지 못했음 · Claude 데스크톱 앱의 Code 탭을 한 번 연 뒤 다시",
                ClaudeModSetup.State.OtherFolder => "다른 폴더의 확장이 켜져 있음 · 누르면 이 오버레이에 든 확장으로 바꿈",
                ClaudeModSetup.State.On => "켜져 있음 · 오버레이가 업데이트되면 확장도 같이 바뀜 · 누르면 끔",
                _ => "핑 버튼과 만료 5분 전 자동 압축은 Claude Code 안의 확장이 함 · 누르면 켬"
            };
        }
        claudeModItem.Click += async (_, _) =>
        {
            if (ClaudeTokenRenewal.FindCli() is not { } cli) { RefreshClaudeModState(); return; }
            var enable = ClaudeModSetup.Status(cli) != ClaudeModSetup.State.On;
            if (enable && MessageBox.Show(
                    "Claude Code에 TokenOverlay 확장을 켤까?\n\n"
                    + "· 오버레이에서 누른 세션에 캐시 유지 핑을 보냄 (대화에는 안 남음)\n"
                    + $"· 압축 안 한 {CacheWatch.LargeContext / 1000}K 이상 세션을 캐시 만료 5분 전에 자동으로 압축함\n\n"
                    + "확장은 Claude Code 안에서 내 계정 권한으로 돌고, Claude 설정(.claude\\settings.json)에 등록됨. "
                    + "새로 여는 세션부터 적용되고, 이미 열린 세션은 다시 열거나 /reload-plugins 해야 함. 끄면 등록이 지워짐.",
                    "TokenOverlay", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            claudeModBusy = true;
            RefreshClaudeModState();
            string? failed;
            try { failed = await Task.Run(() => ClaudeModSetup.Set(enable, cli)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { failed = e.Message; }
            claudeModBusy = false;
            RefreshClaudeModState();
            DiagnosticLog.Info("claude-mod", (enable ? "switched on" : "switched off") + (failed is null ? "" : " · failed"));
            if (failed is null)
                _trayIcon?.ShowBalloonTip(5000, "TokenOverlay", enable
                    ? "Claude Code 확장 켜짐 · 새로 여는 세션부터 (열린 세션은 /reload-plugins)" : "Claude Code 확장 꺼짐", ToolTipIcon.Info);
            else _trayIcon?.ShowBalloonTip(6000, "TokenOverlay", "Claude Code 확장을 바꾸지 못했음: " + failed, ToolTipIcon.Warning);
        };
        menu.Opening += (_, _) => RefreshClaudeModState();
        RefreshClaudeModState();
        settingsMenu.DropDownItems.Add(claudeModItem);

        // Model-rate telemetry is always on while the overlay runs: there is no off switch, the item shows each
        // app's state and a click connects again. Disconnecting is only offered together with quitting (below).
        var telemetryItem = new ToolStripMenuItem(TelemetryMenuText(null, null));
        void RefreshTelemetryState()
        {
            try
            {
                var (claude, codex) = TelemetrySetup.Status();
                telemetryItem.Text = TelemetryMenuText(claude, codex);
                telemetryItem.ToolTipText = _telemetryReceiver.Error ?? "항상 켜 둠 · 누르면 다시 연결";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { telemetryItem.Text = TelemetryMenuText(null, null); telemetryItem.ToolTipText = e.Message; }
        }
        telemetryItem.Click += (_, _) =>
        {
            try
            {
                var (claude, codex) = TelemetrySetup.SetEnabled(true);
                if (claude == TelemetryLinkState.Conflict || codex == TelemetryLinkState.Conflict)
                    NotifySaveFailure("기존 계측 설정이 있어 해당 앱은 바꾸지 않았음.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { NotifySaveFailure("계측 설정을 변경하지 못했음: " + e.Message); }
            RefreshTelemetryState();
        };
        menu.Opening += (_, _) => RefreshTelemetryState();
        RefreshTelemetryState();

        // Weekly limit: one-minute readings with Claude Code's login token (read-only). Off = the app's 15-minute record only.
        var usageApiItem = new ToolStripMenuItem("주간 한도 2분 갱신 (Claude 로그인 사용)");
        void RefreshUsageApiState() { usageApiItem.Checked = _weekly.ApiEnabled; usageApiItem.ToolTipText = _weekly.ApiStatus; }
        usageApiItem.Click += (_, _) => { _weekly.SetApiEnabled(!_weekly.ApiEnabled); RefreshUsageApiState(); };
        menu.Opening += (_, _) => RefreshUsageApiState();
        RefreshUsageApiState();
        settingsMenu.DropDownItems.Add(usageApiItem);
        // Codex weekly limit: asked from the Codex app's own tool about once a minute. Off = the logged readings only.
        var codexApiItem = new ToolStripMenuItem("Codex 주간 한도 1분 갱신 (Codex 앱 사용)");
        void RefreshCodexApiState() { codexApiItem.Checked = _codexWeekly.ApiEnabled; codexApiItem.ToolTipText = _codexWeekly.ApiStatus; }
        codexApiItem.Click += (_, _) =>
        {
            _codexWeekly.SetApiEnabled(!_codexWeekly.ApiEnabled);
            DiagnosticLog.Info("codex-weekly-api", _codexWeekly.ApiEnabled ? "switched on" : "switched off");
            RefreshCodexApiState();
        };
        menu.Opening += (_, _) => RefreshCodexApiState();
        RefreshCodexApiState();
        settingsMenu.DropDownItems.Add(codexApiItem);
        // Offered only while no Claude Code login is stored on this PC.
        var loginItem = new ToolStripMenuItem("Claude 로그인… (2분 갱신에 필요)");
        menu.Opening += (_, _) => loginItem.Visible = !ClaudeTokenRenewal.HasLogin;
        loginItem.Click += (_, _) =>
        {
            if (!ClaudeTokenRenewal.OpenLoginWindow())
                NotifySaveFailure("Claude 앱에 포함된 Claude Code를 찾지 못했음. Claude 데스크톱 앱의 Code 탭을 한 번 연 뒤 다시 시도.");
        };
        settingsMenu.DropDownItems.Add(loginItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        // Me / not me across the user's own devices, through Google Drive for desktop. Connects by itself.
        var shareItem = new ToolStripMenuItem("내 기기 공유 (구글 드라이브)")
            { ToolTipText = "내 다른 기기와 사용 시각·% 숫자만 주고받아 '이 PC / 내 다른 기기 / 다른 곳'으로 나눔 (기기 이름은 내 기기 관리에서 바꿈)" };
        var shareStatusItem = new ToolStripMenuItem("") { Enabled = false };
        void RefreshShareState() { shareItem.Checked = _driveLink.Enabled; shareStatusItem.Text = "    " + _driveLink.Status; }
        shareItem.Click += (_, _) =>
        {
            _driveLink.Enabled = !_driveLink.Enabled;
            DiagnosticLog.Info("share", _driveLink.Enabled ? "switched on" : "switched off");
            _settings.DriveShareEnabled = _driveLink.Enabled;
            if (!_settings.TrySave(_settingsPath)) NotifySaveFailure("공유 설정을 저장하지 못했음.");
            RefreshShareState();
            Task.Run(() => { _codexWeekly.Refresh(); _weekly.Refresh(); });
        };
        menu.Opening += (_, _) => RefreshShareState();
        RefreshShareState();
        settingsMenu.DropDownItems.Add(shareItem);
        settingsMenu.DropDownItems.Add(shareStatusItem);
        var devicesItem = new ToolStripMenuItem("내 기기 관리…") { ToolTipText = "어떤 기기를 내 기기로 셀지, 이름·합치기·공유 폴더" };
        devicesItem.Click += (_, _) => ShowDeviceManager();
        settingsMenu.DropDownItems.Add(devicesItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        settingsMenu.DropDownItems.Add(telemetryItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());

        // For removing the program: takes back the telemetry keys it added and the auto-start entry, then quits.
        var removeItem = new ToolStripMenuItem("프로그램 삭제 준비 (계측·자동 실행·Claude Code 확장 해제 후 종료)");
        removeItem.Click += (_, _) =>
        {
            try
            {
                TelemetrySetup.SetEnabled(false);
                if (StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(false);
                ClaudeModSetup.Uninstall();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or System.Security.SecurityException)
            { NotifySaveFailure("해제하지 못했음: " + e.Message); return; }
            ExitOverlay();
        };
        settingsMenu.DropDownItems.Add(removeItem);
        settingsMenu.DropDownItems.Add(new ToolStripSeparator());
        // A zip on the desktop to send along with a bug report: the last days' logs and a short summary.
        var reportItem = new ToolStripMenuItem("버그 신고용 로그 만들기")
            { ToolTipText = "바탕화면에 zip 파일을 만듦 · 대화 내용·채팅 제목·로그인 정보는 들어가지 않음" };
        reportItem.Click += async (_, _) =>
        {
            try
            {
                DiagnosticLog.Info("report", "bug report requested");
                // Reading a long chat's log can take seconds: the folders and logs are read off the UI thread.
                var (summary, claudeTitle, codexThread) = (ReportSummary(), _reportClaudeTitle, _reportCodexThread);
                reportItem.Enabled = false;
                var path = await Task.Run(() => DiagnosticLog.CreateReport(summary + ReportDiagnosis(claudeTitle, codexThread)));
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                DiagnosticLog.Error("report", e);
                _trayIcon?.ShowBalloonTip(3000, "TokenOverlay", "로그 파일을 만들지 못했음: " + e.Message, ToolTipIcon.Warning);
            }
            finally { reportItem.Enabled = true; }
        };
        settingsMenu.DropDownItems.Add(reportItem);
        // Updates from the public release repository: checked a few minutes after start and twice a day.
        var updateItem = new ToolStripMenuItem("업데이트 확인");
        // Top line of the menu: this version, and when a newer one is waiting a click puts it in right away
        // (it would otherwise go in by itself after a quiet minute). With nothing waiting a click checks now.
        var versionItem = new ToolStripMenuItem();
        var bold = new Font(menu.Font, FontStyle.Bold);
        versionItem.Disposed += (_, _) => bold.Dispose();
        void RefreshUpdateState()
        {
            updateItem.ToolTipText = _updater.Status;
            updateItem.Enabled = _updater.Installed;
            var (text, enabled, emphasized) = VersionMenuLine(AppVersion.Text, _updater.Installed, _updater.ReadyVersion, _updater.Blocked, _updater.Busy);
            versionItem.Text = text;
            versionItem.Enabled = enabled;
            versionItem.Font = emphasized ? bold : menu.Font;
            versionItem.ToolTipText = _updater.Status;
            if (_trayIcon is not null) _trayIcon.Text = TrayText(AppVersion.Text, _updater.TrayLine);
        }
        async void CheckForUpdate(bool announce)
        {
            // The hourly check finds an already waiting version again; only a new one (or a click) gets a balloon.
            var wasReady = _updater.ReadyVersion is not null;
            var checking = _updater.CheckAsync();
            RefreshUpdateState();
            var ready = await checking;
            if (Volatile.Read(ref _disposed) != 0) return;
            RefreshUpdateState();
            if (ready && wasReady && !announce) return;
            if (ready && _updater.Blocked) _trayIcon?.ShowBalloonTip(6000, "TokenOverlay 새 버전", $"{_updater.ReadyVersion} 나옴 · 트레이 메뉴 맨 위를 눌러 설치", ToolTipIcon.Info);
            else if (ready) _trayIcon?.ShowBalloonTip(6000, "TokenOverlay 새 버전", $"{_updater.ReadyVersion} 받음 · 쉬는 동안 저절로 적용 (바로 하려면 트레이 메뉴 맨 위를 누름)", ToolTipIcon.Info);
            else if (announce) _trayIcon?.ShowBalloonTip(3000, "TokenOverlay", _updater.Status, ToolTipIcon.Info);
        }
        updateItem.Click += (_, _) => CheckForUpdate(announce: true);
        async void InstallWithSetup()
        {
            _trayIcon?.ShowBalloonTip(4000, "TokenOverlay", $"{_updater.ReadyVersion} 설치 파일 받는 중 · 다 받으면 저절로 닫히고 새 버전이 켜짐", ToolTipIcon.Info);
            var started = await _updater.InstallWithSetupAsync();
            if (Volatile.Read(ref _disposed) != 0) return;
            if (started) ExitOverlay();
            else _trayIcon?.ShowBalloonTip(5000, "TokenOverlay", _updater.Status, ToolTipIcon.Warning);
        }
        versionItem.Click += (_, _) =>
        {
            if (_updater.ReadyVersion is null) CheckForUpdate(announce: true);
            else if (_updater.Blocked) InstallWithSetup();
            else if (_updater.Apply()) ExitOverlay();
            else { ShowUpdateBlockedNotice(); RefreshUpdateState(); }
        };
        menu.Opening += (_, _) => RefreshUpdateState();
        RefreshUpdateState();
        menu.Items.Insert(0, versionItem);
        settingsMenu.DropDownItems.Add(updateItem);
        _updateTimer.Interval = 3 * 60 * 1000;
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = 60 * 60 * 1000; CheckForUpdate(announce: false); };
        if (_updater.Installed) _updateTimer.Start();
        return settingsMenu;
    }

    internal static (string Text, bool Enabled, bool Emphasized) VersionMenuLine(string version, bool installed, string? ready, bool blocked, bool busy)
    {
        var plus = version.IndexOf('+');
        var shown = $"버전 {(plus < 0 ? version : version[..plus])}";
        if (!installed) return ($"{shown} · 설치본이 아니라 업데이트 없음", false, false);
        if (ready is not null) return (blocked ? $"새 버전 {ready} · 눌러서 설치 파일로 업데이트" : $"새 버전 {ready} · 눌러서 업데이트", true, true);
        if (busy) return ($"{shown} · 업데이트 확인 중…", false, false);
        return ($"{shown} · 최신 버전 (눌러서 확인)", true, false);
    }

    // Once, right after Windows refused the updater: says how the update goes in from now on.
    private void ShowUpdateBlockedNotice()
    {
        if (!_updater.TakeBlockNotice()) return;
        _trayIcon?.ShowBalloonTip(10000, "TokenOverlay 업데이트",
            $"Windows가 자동 업데이트를 막음 · 트레이 메뉴 맨 위를 누르면 설치 파일로 {_updater.ReadyVersion} 설치 (기록은 그대로)",
            ToolTipIcon.Info);
    }

    private void AddAnchorMenu(ToolStripMenuItem menu, HostApp app, string text, AnchorMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (_manualAttachment.IsEditing)
            {
                CancelManualEditing();
            }
            _settings.ManualPlacementEnabled = false;
            _settings.SetAnchor(app, mode);
            _settings.Save(_settingsPath);
            UpdateAnchorChecks();
            UpdateManualMenuState();
            if (_currentTarget is not null && !_manuallyHidden)
            {
                ApplyLayout(_currentTarget);
            }
        };
        _anchorItems[(app, mode)] = item;
        menu.DropDownItems.Add(item);
    }

    private void UpdateAnchorChecks()
    {
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = pair.Key.Mode == _settings.AnchorFor(pair.Key.App);
        }
    }

    private void UpdateManualMenuState()
    {
        var editing = _manualAttachment.IsEditing;
        _adjustManualMenuItem.Visible = !editing;
        _adjustManualMenuItem.Enabled = !editing && _currentTarget is not null;
        _saveManualMenuItem.Visible = editing;
        _saveManualMenuItem.Enabled = editing && _manualAttachment.CanSave;
        _cancelManualMenuItem.Visible = editing;
        _cancelManualMenuItem.Enabled = editing;
        _resetManualMenuItem.Enabled = !editing;
        _traditionalMenuItem.Enabled = !editing;
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = !_settings.ManualPlacementEnabled
                && pair.Key.Mode == _settings.AnchorFor(pair.Key.App);
        }
    }

    private void UpdateSessionMenuText()
    {
        var threadId = _lastSnapshot?.ThreadId ?? _pendingThreadId;
        var shortId = string.IsNullOrWhiteSpace(threadId)
            ? "확인 중"
            : OverlayPresentationBuilder.ShortThreadId(threadId);
        _sessionMenuItem.Text = $"채팅: {shortId}{RouteStatusSuffix(_pendingRouteStatus)}";
    }
}
