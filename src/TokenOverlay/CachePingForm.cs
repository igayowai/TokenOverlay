namespace TokenOverlay;

// "Claude 캐시 세션": the large uncompacted Claude sessions whose cache is still alive, soonest lapse first, each with
// a 핑 button. A ping is done by the session's own mod (tokenoverlay-cache); a session without it can only be told.
// Opened from the cache notice or the tray menu; it follows the watch's scans while open.
internal sealed class CachePingForm : Form
{
    private readonly CacheWatch _watch;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action? _scan;
    private readonly TableLayoutPanel _rows = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 7, Padding = new Padding(6, 4, 6, 4),
        GrowStyle = TableLayoutPanelGrowStyle.AddRows
    };
    private readonly Button _close = new() { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly CachePings _pings;
    private string _shown = "";
    private bool _scanning;

    public CachePingForm(CacheWatch watch, Func<DateTimeOffset>? now = null, bool live = true, CachePings? pings = null)
    {
        _watch = watch;
        _pings = pings ?? new CachePings();
        _now = now ?? (() => DateTimeOffset.Now);
        if (live) _scan = watch.ScanNow;
        Text = "Claude 캐시 세션";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(960, 400);
        MinimumSize = new Size(520, 220);
        CancelButton = _close;

        // Name, time left and size, state, then 압축, the 유지 switch and 핑, kept to the right edge by an empty column.
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _rows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var hint = new Label
        {
            Dock = DockStyle.Top, Padding = new Padding(8, 8, 8, 4), ForeColor = SystemColors.GrayText,
            Text = "핑: 그 세션의 캐시를 한 번 읽어 1시간 더 살림 · 대화에는 안 남음\n유지: 켜 두면 만료 10분 전마다 확장이 알아서 핑 (켠 뒤 8시간 동안)\n그냥 두면: 만료 5분 전에 확장이 알아서 압축\n압축: 두 번 누르면 그 세션을 바로 압축 (답하는 중이면 끝난 뒤)"
        };
        hint.Height = Font.Height * 4 + 14;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(2), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_close);
        Controls.Add(_rows);
        Controls.Add(hint);
        Controls.Add(buttons);

        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => Refresh(scan: true);
        Shown += (_, _) => { if (live) _timer.Start(); Refresh(scan: true); };
        FormClosed += (_, _) => _timer.Dispose();
        Fill();
    }

    // A fresh scan off the UI thread (the session logs and the mod's files), then the rows again.
    private void Refresh(bool scan)
    {
        if (!scan || _scan is null) { Fill(); return; }
        if (_scanning) return;
        _scanning = true;
        Task.Run(_scan).ContinueWith(_ =>
        {
            try { if (!IsDisposed) BeginInvoke(() => { _scanning = false; Fill(); }); }
            catch (InvalidOperationException) { }
        }, TaskScheduler.Default);
    }

    internal void Fill()
    {
        var now = _now();
        var sessions = _watch.Sessions.Where(s => s.Shown(now)).ToArray();
        var rows = sessions.Select(s => (s, _pings.Line(s, now))).ToArray();
        // Rebuilt only when something shown changed, so a button is not swapped out under the pointer.
        var key = string.Join("\n", rows.Select(r => $"{r.s.Id}|{r.Item2.Left}|{r.Item2.State}|{r.Item2.CanPing}|{r.Item2.Keep}|{r.Item2.CanKeep}|{r.Item2.CanCompact}|{r.Item2.Arming}"));
        if (key == _shown) return;
        _shown = key;
        _rows.SuspendLayout();
        _rows.Controls.Clear();
        _rows.RowStyles.Clear();
        _rows.RowCount = 0;
        if (rows.Length == 0)
        {
            _rows.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 8, 3, 3),
                Text = $"지금 캐시가 살아 있는 큰 세션 없음 ({CacheWatch.LargeContext / 1000}K 이상, 압축 안 한 세션만 보임)"
            }, 0, 0);
            _rows.SetColumnSpan(_rows.GetControlFromPosition(0, 0)!, 7);
        }
        foreach (var (session, line) in rows)
        {
            var row = _rows.RowCount++;
            _rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label Cell(string text, Color? color = null) => new()
            {
                Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 10, 6),
                ForeColor = color ?? SystemColors.ControlText, AutoEllipsis = true
            };
            _rows.Controls.Add(Cell(session.Name), 0, row);
            _rows.Controls.Add(Cell(line.Left, line.Soon ? Color.Firebrick : null), 1, row);
            _rows.Controls.Add(Cell(line.State, SystemColors.GrayText), 2, row);
            var compact = new Button { Text = line.Arming ? "한 번 더" : "압축", AutoSize = true, Anchor = AnchorStyles.Right, Enabled = line.CanCompact };
            compact.Click += (_, _) => Compact(session.Id);
            _rows.Controls.Add(compact, 4, row);
            var keep = new CheckBox
            {
                Text = line.Keep is null ? "유지" : "유지 중", Appearance = Appearance.Button, AutoSize = true, Anchor = AnchorStyles.Right,
                Checked = line.Keep is not null, Enabled = line.CanKeep, TextAlign = ContentAlignment.MiddleCenter
            };
            var wanted = line.Keep is null;
            keep.Click += (_, _) => Keep(session.Id, wanted);
            _rows.Controls.Add(keep, 5, row);
            var ping = new Button { Text = "핑", AutoSize = true, Anchor = AnchorStyles.Right, Enabled = line.CanPing, Tag = session.Id };
            ping.Click += (_, _) => Ping(session.Id);
            _rows.Controls.Add(ping, 6, row);
        }
        // The space left over goes to an empty last row, not to the last session's.
        _rows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rows.RowCount++;
        _rows.ResumeLayout();
        // Wider rows than the window widen it (within the screen), so the buttons never sit on the state text.
        var fit = _rows.GetPreferredSize(Size.Empty).Width + SystemInformation.VerticalScrollBarWidth;
        var screen = Screen.FromControl(this).WorkingArea.Width * 9 / 10;
        if (fit > ClientSize.Width) ClientSize = new Size(Math.Min(fit, screen), ClientSize.Height);
    }

    private void Keep(string sessionId, bool on)
    {
        try { _pings.Keep(sessionId, on, _now()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "유지 설정 파일을 쓰지 못했음: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _shown = "";
        Fill();
    }

    private void Compact(string sessionId)
    {
        try { _pings.Compact(sessionId, _now()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "압축 요청 파일을 쓰지 못했음: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _shown = "";
        Fill();
    }

    private void Ping(string sessionId)
    {
        try
        {
            _pings.Ask(sessionId, _now());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "핑 요청 파일을 쓰지 못했음: " + e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        Fill();
    }
}

// The pings asked for, by session, until each session's mod answers, and what each session's row says. The window and
// the Ctrl list under the capsule share one, so a ping asked from either shows in both.
internal sealed class CachePings
{
    internal const string NoneText = "핑할 세션 없음";
    internal const string UpdateText = "새 버전 · /reload-plugins 치면 적용";
    internal const string ArmText = "한 번 더 누르면 압축";
    // A first press of 압축 waits this long for the second.
    internal static readonly TimeSpan ArmFor = TimeSpan.FromSeconds(5);
    // The request id and when, by session: pings, and compactions.
    private readonly Dictionary<string, (string Request, DateTimeOffset At)> _asked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Request, DateTimeOffset At)> _compacts = new(StringComparer.OrdinalIgnoreCase);
    private (string Id, DateTimeOffset At)? _armed;
    private readonly string? _bridgeFolder;

    internal CachePings(string? bridgeFolder = null) => _bridgeFolder = bridgeFolder;

    // Keep: the 유지 switch is on (until then); CanKeep: it can be switched (on or off) for this session.
    // CanCompact: 압축 can be asked; Arming: it was pressed once and waits for the second press.
    internal readonly record struct RowText(string Left, bool Soon, string State, bool CanPing, DateTimeOffset? Keep = null, bool CanKeep = false,
        bool CanCompact = false, bool Arming = false);

    internal RowText Line(CacheWatch.Session s, DateTimeOffset now)
    {
        // A row past its lapse is one whose compaction the mod tried and the log never got.
        var lapsed = s.LapsesAt <= now;
        var minutes = Math.Max(1, (int)Math.Ceiling((s.LapsesAt - now).TotalMinutes));
        var left = lapsed ? $"캐시 만료 · {s.Context / 1000}K" : $"{minutes}분 남음 · {s.Context / 1000}K";
        var soon = lapsed || minutes <= CacheWatch.WarnMinutes;
        var keep = CacheWatch.KeepUntil(s.Id, now, _bridgeFolder);
        if (!s.Partner)
            return new RowText(left, soon, s.Compaction == CacheWatch.Compaction.Failed ? Failed(s.Bridge!)
                : s.Bridge is null ? "확장 없음" : "확장이 멈춰 있음", false, keep, keep is not null);
        var b = s.Bridge!;
        var canKeep = keep is not null || b.Code >= CacheWatch.KeepCodeNeeded;
        var text = Text(s, b, now, keep, left, soon);
        // A session keeps the mod build it loaded until /reload-plugins is typed there (a mod cannot run it itself), so an
        // older one says so whenever there is no ping to tell of, and always while 유지 waits on it.
        var recent = _asked.ContainsKey(s.Id) || b.PingAt is { } at && now - at < TimeSpan.FromMinutes(60);
        if (b.Code < CacheWatch.ReloadHintCode && (!recent || keep is not null && b.Code < CacheWatch.KeepCodeNeeded))
            text = text with { State = UpdateText };
        var canCompact = b.Code >= CacheWatch.CompactCodeNeeded;
        if (Compacting(s.Id, b, now) is { } compacting) { text = text with { State = compacting.State }; canCompact = compacting.CanAsk; }
        else if (s.Compaction != CacheWatch.Compaction.None)
        {
            text = text with { State = s.Compaction switch
            {
                CacheWatch.Compaction.Running => "압축 중…",
                CacheWatch.Compaction.Checking => "압축 확인 중…",
                CacheWatch.Compaction.Retrying => "압축 다시 시도 중…",
                _ => Failed(b)
            } };
            canCompact &= s.Compaction == CacheWatch.Compaction.Failed;
        }
        var arming = canCompact && _armed is { } armed && armed.Id == s.Id && now - armed.At <= ArmFor;
        if (arming) text = text with { State = ArmText };
        return text with { CanPing = text.CanPing && !lapsed, Keep = keep, CanKeep = canKeep, CanCompact = canCompact, Arming = arming };
    }

    private static string Failed(CacheWatch.Bridge b) => $"{b.Attempt!.At.ToLocalTime():HH:mm} 압축 실패";

    // The 압축 asked for a session, as its mod takes it up; the compaction itself then ends the row. Forgotten after
    // 10 minutes, so a row the compaction did not end says what it said before. A mod that reports its attempts
    // (build 5 on) is followed by them from when it takes the request up.
    private (string State, bool CanAsk)? Compacting(string sessionId, CacheWatch.Bridge b, DateTimeOffset now)
    {
        if (!_compacts.TryGetValue(sessionId, out var asked)) return null;
        if (now - asked.At > TimeSpan.FromMinutes(10)) { _compacts.Remove(sessionId); return null; }
        if (b.CompactHandled == asked.Request && b.Attempt is not null) { _compacts.Remove(sessionId); return null; }
        if (b.CompactHandled != asked.Request)
        {
            if (b.Busy) return ("답이 끝나면 압축", false);
            // The mod looks every 30 seconds; much longer means its session's process is not running it.
            return now - asked.At < TimeSpan.FromMinutes(2) ? ("압축 요청함…", false) : ("확장이 답하지 않음", true);
        }
        return b.CompactOk ? ("압축 중…", false) : ("압축 못 함", true);
    }

    // 압축: a first press arms the row for a few seconds, a second one within them writes the request the session's mod
    // picks up. True when this press asked; a file error reaches the caller.
    internal bool Compact(string sessionId, DateTimeOffset now)
    {
        if (_armed is not { } armed || armed.Id != sessionId || now - armed.At > ArmFor)
        {
            _armed = (sessionId, now);
            return false;
        }
        _armed = null;
        _compacts[sessionId] = (CacheWatch.RequestCompact(sessionId, _bridgeFolder), now);
        DiagnosticLog.Info("cache", "compact asked");
        return true;
    }

    private RowText Text(CacheWatch.Session s, CacheWatch.Bridge b, DateTimeOffset now, DateTimeOffset? keep, string left, bool soon)
    {
        // While kept: what happened last (if within the hour), then when the next ping goes and until when.
        var tail = keep is { } until ? $" · 유지 ~{until.ToLocalTime():HH:mm}" : "";
        if (_asked.TryGetValue(s.Id, out var asked))
        {
            // The mod looks every 30 seconds; much longer means its session's process is not running it.
            if (b.Handled != asked.Request && now - asked.At < TimeSpan.FromMinutes(2)) return new RowText(left, soon, "핑 보내는 중…", false);
            if (b.Handled != asked.Request) return new RowText(left, soon, "확장이 답하지 않음", true);
            _asked.Remove(s.Id);
        }
        if (b.PingAt is { } at && now - at < TimeSpan.FromMinutes(60))
        {
            var when = at.ToLocalTime().ToString("HH:mm");
            var how = b.PingAuto ? "자동 핑" : "핑";
            var next = keep is not null ? $" · 다음 {s.LapsesAt.AddMinutes(-10).ToLocalTime():HH:mm}" : "";
            if (b.PingOk) return new RowText(left, soon, $"{when} {how}함{next}{tail}", true);
            return new RowText(left, soon, $"{when} {how} 못 함 · {Reason(b.PingReason)}{tail}", true);
        }
        if (keep is not null)
            return new RowText(left, soon, $"{s.LapsesAt.AddMinutes(-10).ToLocalTime():HH:mm} 자동 핑 예정{tail}", true);
        var compactAt = s.LapsesAt.AddMinutes(-5).ToLocalTime().ToString("HH:mm");
        return new RowText(left, soon, $"{compactAt} 자동 압축 예정", true);
    }

    internal static string Reason(string? reason) => reason switch
    {
        "busy" => "작업 중이었음",
        "cold" => "캐시가 이미 식었음",
        "cache-missed" => "캐시를 못 읽음",
        "nothing-to-fork" => "대화가 없음",
        _ => "응답 오류"
    };

    // Switches 유지 for the session (8 hours from now when on); a file error reaches the caller.
    internal void Keep(string sessionId, bool on, DateTimeOffset now)
    {
        CacheWatch.SetKeep(sessionId, on, now, _bridgeFolder);
        DiagnosticLog.Info("cache", on ? "keep on" : "keep off");
    }

    // Writes the request the session's mod picks up; a file error reaches the caller.
    internal void Ask(string sessionId, DateTimeOffset now)
    {
        _asked[sessionId] = (CacheWatch.RequestPing(sessionId), now);
        DiagnosticLog.Info("cache", "ping asked");
    }
}
