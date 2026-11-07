namespace TokenOverlay;

internal sealed partial class OverlayContext
{
    internal static string TelemetryMenuText(TelemetryLinkState? claude, TelemetryLinkState? codex)
    {
        static string Word(TelemetryLinkState? state) => state switch
        {
            TelemetryLinkState.On => "켜짐",
            TelemetryLinkState.Off => "꺼짐",
            TelemetryLinkState.Conflict => "다른 설정 있음",
            TelemetryLinkState.AppMissing => "앱 없음",
            _ => "확인 못 함",
        };
        return $"모델 요청 속도 계측 · Claude {Word(claude)} · Codex {Word(codex)}";
    }

    // Every start after the first: connect any app whose telemetry is off (turned off elsewhere, or installed later).
    // An app that already has its own exporter settings is left alone.
    private void EnsureTelemetry()
    {
        try
        {
            var (claude, codex) = TelemetrySetup.Status();
            if (claude != TelemetryLinkState.Off && codex != TelemetryLinkState.Off) return;
            TelemetrySetup.SetEnabled(true);
            _trayIcon.ShowBalloonTip(6000, TrayName,
                "모델 속도 계측을 다시 켰음. Codex와 Claude를 한 번 껐다 켜면 '모델' 줄이 나옴.", ToolTipIcon.Info);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    // First start on a PC: switch on what would otherwise need the tray menu (start with Windows, model-rate
    // telemetry) and offer the one sign-in the one-minute weekly reading needs. Done once; the tray menu still
    // turns each of them off, and a later start never switches them back on.
    private void ScheduleFirstRunSetup()
    {
        var marker = Path.Combine(AppPaths.DataFolder, "first-run-done");
        if (File.Exists(marker))
        {
            try { if (StartupRegistration.Repoint()) DiagnosticLog.Info("startup", "auto-start moved to this copy"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { DiagnosticLog.Error("startup", e); }
            EnsureTelemetry();
            return;
        }
        var once = new System.Windows.Forms.Timer { Interval = 1500 };
        once.Tick += (_, _) =>
        {
            once.Stop(); once.Dispose();
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                File.WriteAllText(marker, DateTime.Now.ToString("s"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            var telemetryChanged = false;
            try { if (!StartupRegistration.IsEnabled()) StartupRegistration.SetEnabled(true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            try
            {
                var (claude, codex) = TelemetrySetup.Status();
                if (claude == TelemetryLinkState.Off || codex == TelemetryLinkState.Off)
                {
                    TelemetrySetup.SetEnabled(true);
                    telemetryChanged = true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            var restart = telemetryChanged ? "\n\n'모델' 줄은 Codex와 Claude를 한 번 껐다 켠 뒤부터 나옵니다." : "";
            if (_weekly.ApiEnabled && !ClaudeTokenRenewal.HasLogin && ClaudeTokenRenewal.CanLogin)
            {
                var answer = MessageBox.Show(
                    "처음 실행이라 Windows 시작 시 자동 실행과 모델 속도 계측을 켰습니다. (자동 실행은 트레이 메뉴에서 끌 수 있음)" + restart
                    + "\n\nClaude 주간 한도를 2분마다 읽고 초기화 시각을 보려면 Claude 로그인이 한 번 필요합니다."
                    + "\n지금 브라우저로 로그인할까요? (나중에 트레이 메뉴 → 설정 → 'Claude 로그인…'에서도 가능)",
                    "TokenOverlay 첫 실행", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes && !ClaudeTokenRenewal.OpenLoginWindow())
                    NotifySaveFailure("Claude 앱에 포함된 Claude Code를 찾지 못했음. Claude 데스크톱 앱의 Code 탭을 한 번 연 뒤 다시 시도.");
            }
            else if (telemetryChanged)
            {
                _trayIcon.ShowBalloonTip(6000, "TokenOverlay 첫 실행",
                    "자동 실행과 모델 속도 계측을 켰음. Codex와 Claude를 한 번 껐다 켜면 '모델' 줄이 나옴.", ToolTipIcon.Info);
            }
        };
        once.Start();
    }

    private void NotifySaveFailure(string message)
    {
        DiagnosticLog.Error("notice", message);
        if (_saveFailureNotified)
        {
            return;
        }

        _saveFailureNotified = true;
        _trayIcon.ShowBalloonTip(
            3000,
            TrayName,
            message,
            ToolTipIcon.Warning);
    }
}
