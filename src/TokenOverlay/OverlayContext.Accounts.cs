namespace TokenOverlay;

// Which Claude and Codex account is in use on this PC (see AccountBook): read every few seconds off the UI thread, a
// change told once in a balloon, and the weeks worked out again for the account now in use.
internal sealed partial class OverlayContext
{
    private readonly AccountBook _accounts = new(AccountBook.DefaultPath);
    private readonly AccountProbe _accountProbe = new();
    private long _accountsAt;
    private int _accountsBusy;

    private void WatchAccounts()
    {
        var at = Environment.TickCount64;
        if (at - _accountsAt < 5000 || Interlocked.CompareExchange(ref _accountsBusy, 1, 0) != 0) return;
        _accountsAt = at;
        Task.Run(() =>
        {
            try { ObserveAccounts(announce: true); }
            catch (Exception e) { DiagnosticLog.Error("accounts", e); }
            finally { Interlocked.Exchange(ref _accountsBusy, 0); }
        });
    }

    private void ObserveAccounts(bool announce)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Claude Code's terminal sign-in is known with its organization first, so on a first run it is the user's own.
        var (code, org) = _accountProbe.ClaudeCode();
        var codeChanged = _accounts.NoteCode(code, org);
        // The first reading (announce false) comes before the weekly monitor exists; it reads the book when it starts.
        if (codeChanged && announce)
        {
            var other = _accounts.CodeOther();
            DiagnosticLog.Info("accounts", $"Claude Code sign-in: {(other is null ? "mine" : "other")}");
            Task.Run(() => _weekly.Refresh());
            if (Volatile.Read(ref _disposed) == 0 && _form.IsHandleCreated)
                _form.BeginInvoke(() => _trayIcon?.ShowBalloonTip(5000, "Claude Code 계정 바뀜", other is null
                    ? "터미널 Claude Code가 내 계정으로 돌아옴 · 주간 %를 다시 1분마다 읽음"
                    : $"터미널 Claude Code가 '{other.Name}'(으)로 로그인됨 · 그동안 내 주간은 앱의 15분 기록으로만 셈", ToolTipIcon.Info));
        }
        var changes = new List<(string App, AccountBook.Account From, AccountBook.Account To)>();
        if (_accountProbe.ClaudeInUse().Key is { } claude && _accounts.Observe(AccountBook.Claude, claude, now) is { } c)
            changes.Add(("Claude", c.From, c.To));
        if (_accountProbe.CodexInUse() is { } codex && _accounts.Observe(AccountBook.Codex, codex, now) is { } x)
            changes.Add(("Codex", x.From, x.To));
        if (changes.Count == 0 || !announce) return;
        foreach (var (app, from, to) in changes)
            DiagnosticLog.Info("accounts", $"{app} account changed: {(from.Mine ? "mine" : "other")} → {(to.Mine ? "mine" : "other")}");
        Task.Run(() => { _weekly.Refresh(); _codexWeekly.Refresh(); });
        if (Volatile.Read(ref _disposed) != 0 || !_form.IsHandleCreated) return;
        _form.BeginInvoke(() =>
        {
            var text = string.Join("\n", changes.Select(change => $"{change.App} 계정이 '{change.To.Name}'(으)로 바뀜"
                + (change.To.Mine ? " · 내 주간 기록으로 돌아옴" : " · 주간 기록은 따로 셈")));
            _trayIcon?.ShowBalloonTip(5000, "계정 바뀜", text + "\n이름은 트레이 → 설정 → 계정 이름에서 바꿈", ToolTipIcon.Info);
            RefreshPresentation();
        });
    }

    // Settings → 계정 이름: each app's accounts, the one in use marked; a click renames one, and the user's own can be
    // chosen.
    private ToolStripMenuItem AccountsMenu()
    {
        var menu = new ToolStripMenuItem("계정 이름")
        {
            ToolTipText = "이 PC에서 쓴 Claude·Codex 계정마다 이름을 붙임. '내 계정'이 아닌 계정을 쓰는 동안 주간 칸에 그 이름과 그 계정의 사용량이 뜨고, 내 주간 기록·기기 공유에는 들어가지 않음"
        };
        menu.DropDownItems.Add(new ToolStripMenuItem("(읽는 중)") { Enabled = false });
        menu.DropDownOpening += (_, _) =>
        {
            menu.DropDownItems.Clear();
            foreach (var (app, label) in new[] { (AccountBook.Claude, "Claude"), (AccountBook.Codex, "Codex") })
            {
                var accounts = _accounts.Accounts(app);
                if (accounts.Count == 0) continue;
                if (menu.DropDownItems.Count > 0) menu.DropDownItems.Add(new ToolStripSeparator());
                menu.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
                var current = _accounts.Current(app)?.Key;
                foreach (var account in accounts)
                {
                    var item = new ToolStripMenuItem($"{account.Name}{(account.Mine ? " (내 계정)" : "")}{(account.Key == current ? " · 지금 쓰는 중" : "")}");
                    var rename = new ToolStripMenuItem("이름 바꾸기…");
                    rename.Click += (_, _) =>
                    {
                        if (DeviceManagerForm.Prompt("계정 이름", $"{label} 계정을 부를 이름 (예: 나, 동생):", account.Name) is not { } name) return;
                        _accounts.Rename(app, account.Key, name);
                        Task.Run(() => { _weekly.Refresh(); _codexWeekly.Refresh(); });
                    };
                    var mine = new ToolStripMenuItem("내 계정으로 정하기") { Checked = account.Mine, Enabled = !account.Mine };
                    mine.Click += (_, _) =>
                    {
                        _accounts.SetMine(app, account.Key);
                        DiagnosticLog.Info("accounts", $"{app}: own account chosen");
                        Task.Run(() => { _weekly.Refresh(); _codexWeekly.Refresh(); });
                    };
                    item.DropDownItems.AddRange([rename, mine]);
                    menu.DropDownItems.Add(item);
                }
            }
            if (menu.DropDownItems.Count == 0) menu.DropDownItems.Add(new ToolStripMenuItem("아직 읽은 계정 없음") { Enabled = false });
        };
        return menu;
    }
}
