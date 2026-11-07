namespace TokenOverlay;

// "내 기기 관리": which devices in the share folder are the user's own. Devices join by themselves;
// here they can be named, left out, merged (same computer, new ID after a reinstall), given to another person
// sharing the folder, or taken off the list, and the share folder can be picked by hand when two devices use
// different Google accounts.
internal sealed class DeviceManagerForm : Form
{
    private readonly DriveShareLink _link;
    private readonly Action<string?> _setFolder;
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Top, Padding = new Padding(4, 6, 4, 0), AutoEllipsis = true };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, CheckBoxes = true, MultiSelect = true,
        HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable
    };
    private readonly Button _rename = new() { Text = "이름 바꾸기", AutoSize = true };
    private readonly Button _merge = new() { Text = "합치기", AutoSize = true };
    private readonly Button _owner = new() { Text = "주인 정하기…", AutoSize = true };
    private readonly Button _defaultOwner = new() { Text = "기본 주인 이름…", AutoSize = true };
    private readonly Button _remove = new() { Text = "목록에서 지우기", AutoSize = true };
    private readonly Button _folder = new() { Text = "공유 폴더 바꾸기…", AutoSize = true };
    private readonly Button _automatic = new() { Text = "자동(구글 드라이브)으로", AutoSize = true };
    private readonly Button _close = new() { Text = "닫기", AutoSize = true, DialogResult = DialogResult.Cancel };
    private IReadOnlyList<DeviceRow> _rows = [];
    private bool _filling;
    // The list reports its check boxes once more as its window is made (unchecked, then checked): those are not the
    // user's, so changes count only once the window is up (they had briefly left devices out, 2026-10-25).
    private bool _shown;

    public DeviceManagerForm(DriveShareLink link, Action<string?> setFolder)
    {
        _link = link; _setFolder = setFolder;
        Text = "내 기기 관리";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false; MaximizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(720, 380);
        MinimumSize = new Size(520, 300);
        CancelButton = _close;

        _list.Columns.Add("이름", 150);
        _list.Columns.Add("주인", 100);
        _list.Columns.Add("상태", 150);
        _list.Columns.Add("마지막으로 보임", 140);
        _list.Columns.Add("기기 ID", 110);
        var hint = new Label
        {
            Dock = DockStyle.Bottom, Padding = new Padding(4, 2, 4, 0), ForeColor = SystemColors.GrayText,
            Text = "체크 = 셈에 넣음. 주인이 같은 기기끼리 '나', 주인이 다른 기기는 그 사람으로 셈.\n합치기: 같은 컴퓨터가 Windows 재설치 등으로 두 번 보일 때 두 줄을 골라 누름."
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(2), WrapContents = true };
        buttons.Controls.AddRange([_rename, _owner, _defaultOwner, _merge, _remove, _folder, _automatic, _close]);
        // Two lines each, in whatever font size the screen uses.
        _status.Height = Font.Height * 2 + 12;
        hint.Height = Font.Height * 2 + 8;
        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);
        Controls.Add(_status);

        _list.ItemChecked += (_, e) => OnChecked(e.Item);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += (_, _) => Rename();
        _rename.Click += (_, _) => Rename();
        _merge.Click += (_, _) => MergeOrSplit();
        _owner.Click += (_, _) => SetOwner();
        _defaultOwner.Click += (_, _) => SetDefaultOwner();
        _remove.Click += (_, _) => Remove();
        _folder.Click += (_, _) => PickFolder();
        _automatic.Click += (_, _) => { _setFolder(null); Fill(); };
        _close.Click += (_, _) => Close();
        Activated += (_, _) => Fill();
        Shown += (_, _) => _shown = true;
        Fill();
    }

    private DriveShare? Share => _link.Current();

    internal void Fill()
    {
        var share = Share;
        _status.Text = (share is null ? _link.Status : share.Status)
            + "\n공유 폴더: " + (share?.BaseFolder ?? _link.FolderOverride ?? "구글 드라이브를 찾는 중")
            + (_link.FolderOverride is null ? " (자동)" : " (직접 지정)");
        _rows = share?.ListDevices() ?? [];
        var names = _rows.ToDictionary(row => row.Id, row => row.Name, StringComparer.OrdinalIgnoreCase);
        var selected = _list.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToHashSet();
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            var state = row.IsThis ? "이 기기" : row.Excluded ? "셈에서 뺌" : row.Mine ? "" : "다른 사람";
            if (row.MergedInto is { } into) state = $"{names.GetValueOrDefault(into) ?? into}에 합쳐짐";
            var seen = row.LastSeenUtc is DateTime at ? Ago(DateTime.UtcNow - at) : "기록 없음";
            var item = new ListViewItem([row.Name, row.Owner, state, seen, row.Id]) { Tag = row.Id, Checked = !row.Excluded };
            if (row.Excluded || row.MergedInto is not null) item.ForeColor = SystemColors.GrayText;
            item.Selected = selected.Contains(row.Id);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _filling = false;
        _automatic.Visible = _link.FolderOverride is not null;
        _defaultOwner.Visible = _rows.Any(row => !row.Mine) || share?.DefaultOwner is not null;
        UpdateButtons();
    }

    internal static string Ago(TimeSpan span) =>
        span.TotalMinutes < 2 ? "방금" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}분 전"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours}시간 전" : $"{(int)span.TotalDays}일 전";

    private DeviceRow[] Selected() => _list.SelectedItems.Cast<ListViewItem>()
        .Select(item => _rows.First(row => row.Id == (string)item.Tag!)).ToArray();

    private void UpdateButtons()
    {
        var selected = Selected();
        var share = Share is not null;
        _rename.Enabled = share && selected.Length == 1;
        _owner.Enabled = share && selected.Length > 0;
        _merge.Text = selected is [{ MergedInto: not null }] ? "합치기 풀기" : "합치기";
        _merge.Enabled = share && (selected.Length == 2 || selected is [{ MergedInto: not null }]);
        _remove.Enabled = share && selected.Length > 0 && selected.All(row => !row.IsThis);
    }

    private void OnChecked(ListViewItem item)
    {
        if (_filling || !_shown || Share is not { } share) return;
        var id = (string)item.Tag!;
        if (id == DeviceIdentity.Id || _rows.FirstOrDefault(row => row.Id == id)?.IsThis == true)
        {
            // This device is always the user's own.
            if (!item.Checked) BeginInvoke(() => { _filling = true; item.Checked = true; _filling = false; });
            return;
        }
        share.SetExcluded(id, !item.Checked);
        DiagnosticLog.Info("share", item.Checked ? "device counted as mine" : "device left out");
        BeginInvoke(Fill);
    }

    private void Rename()
    {
        if (Share is not { } share || Selected() is not [var row]) return;
        if (Prompt("이름 바꾸기", "이 기기를 부를 이름 (비우면 컴퓨터 이름):", row.Name) is not { } name) return;
        share.Rename(row.Id, name);
        Fill();
    }

    // Whose the selected devices are. The first time someone else is named, the devices left without an owner get a
    // name too, for that person's screen.
    private void SetOwner()
    {
        if (Share is not { } share || Selected() is not { Length: > 0 } selected) return;
        var current = selected.Length == 1 && DriveShare.ShownName(share.ListDevices().First(row => row.Id == selected[0].Id).Owner) is { } owner
            && !owner.Equals(share.DefaultOwner ?? "공유 폴더 주인", StringComparison.OrdinalIgnoreCase) ? owner : "";
        if (Prompt("주인 정하기", "이 기기를 쓰는 사람 (비우면 기본 주인):", current) is not { } name) return;
        foreach (var row in selected) share.SetOwner(row.Id, name);
        DiagnosticLog.Info("share", $"owner set for {selected.Length} device(s)");
        if (!string.IsNullOrWhiteSpace(name) && share.DefaultOwner is null) SetDefaultOwner();
        Fill();
    }

    private void SetDefaultOwner()
    {
        if (Share is not { } share) return;
        if (Prompt("기본 주인 이름", "주인을 따로 정하지 않은 기기의 주인 (다른 사람 화면에 이 이름으로 보임):", share.DefaultOwner ?? "") is not { } name) return;
        share.SetDefaultOwner(name);
        Fill();
    }

    private void MergeOrSplit()
    {
        if (Share is not { } share) return;
        var selected = Selected();
        if (selected is [{ MergedInto: not null } merged])
        {
            share.Unmerge(merged.Id);
        }
        else if (selected.Length == 2)
        {
            // The one seen most recently stays; the older one is folded into it.
            var ordered = selected.OrderByDescending(row => row.IsThis).ThenByDescending(row => row.LastSeenUtc ?? DateTime.MinValue).ToArray();
            var (keep, fold) = (ordered[0], ordered[1]);
            if (MessageBox.Show(this, $"'{fold.Name}'을(를) '{keep.Name}'에 합칠까요?\n같은 컴퓨터일 때만 합치면 됨.", "합치기",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            share.Merge(fold.Id, keep.Id);
        }
        Fill();
    }

    private void Remove()
    {
        if (Share is not { } share || Selected() is not { Length: > 0 } selected || selected.Any(row => row.IsThis)) return;
        if (MessageBox.Show(this, $"{string.Join(", ", selected.Select(row => row.Name))}을(를) 목록에서 지울까요?\n"
                + "그 기기의 공유 파일도 지움. 아직 쓰는 기기면 다시 나타남.", "목록에서 지우기",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var row in selected) share.Remove(row.Id);
        DiagnosticLog.Info("share", $"{selected.Length} device(s) taken off the list");
        Fill();
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "같이 볼 수 있는 폴더 (예: 두 구글 계정이 함께 쓰는 공유 폴더, 또는 공유받은 TokenOverlay 폴더)",
            UseDescriptionForTitle = true,
            InitialDirectory = Share?.BaseFolder is { } current ? Path.GetDirectoryName(current) ?? "" : ""
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        _setFolder(dialog.SelectedPath);
        Fill();
    }

    // A one-line text question; null when cancelled.
    internal static string? Prompt(string title, string question, string initial)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
            Font = SystemFonts.MessageBoxFont ?? Control.DefaultFont, ClientSize = new Size(360, 110)
        };
        var label = new Label { Text = question, Left = 12, Top = 12, Width = 336, AutoSize = false, Height = 20 };
        var box = new TextBox { Text = initial, Left = 12, Top = 36, Width = 336 };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Left = 192, Top = 72, Width = 75 };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Left = 273, Top = 72, Width = 75 };
        form.Controls.AddRange([label, box, ok, cancel]);
        form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
}
