using System.Drawing.Drawing2D;

namespace TokenOverlay;

internal enum OverlayEditGestureKind
{
    Move,
    Resize
}

internal sealed record OverlayEditPreviewEventArgs(
    OverlayEditGestureKind Kind,
    Point CursorScreen,
    Point FixedTopLeft,
    int ScalePercent);

internal readonly record struct OverlayRenderMetrics(
    double LabelFontPoints,
    double CompactValueFontPoints,
    double PanelHeaderFontPoints,
    double HighlightedValueFontPoints,
    int CapsuleRadius,
    int PanelRadius,
    int HorizontalPadding,
    int MetricGap,
    int DividerHeight,
    int PanelPadding,
    int HeaderHeight,
    int HighlightTopGap,
    int HighlightHeight,
    int ProgressTrackHeight,
    int ProgressVerticalGap,
    int CompactMetricGap,
    int EditHandleSize,
    int StrokeWidth)
{
    public static OverlayRenderMetrics Create(uint dpi, int scalePercent)
    {
        var effectiveDpi = dpi == 0 ? 96u : dpi;
        var sanitizedScale = ManualAttachmentRules.SanitizeScale(scalePercent);
        var userFactor = sanitizedScale / 100d;
        var pixelFactor = effectiveDpi / 96d * userFactor;
        int Scale(int dip) => (int)Math.Round(
            dip * pixelFactor,
            MidpointRounding.AwayFromZero);
        double ScaleFont(double points) => Math.Round(
            points * userFactor,
            2,
            MidpointRounding.AwayFromZero);

        return new OverlayRenderMetrics(
            ScaleFont(10d),
            ScaleFont(12d),
            ScaleFont(13d),
            ScaleFont(15d),
            Scale(10),
            Scale(14),
            Scale(10),
            Scale(8),
            Scale(14),
            Scale(14),
            Scale(22),
            Scale(6),
            Scale(44),
            Math.Max(1, Scale(4)),
            Scale(10),
            Scale(4),
            Math.Max(1, Scale(12)),
            Math.Max(1, Scale(1)));
    }
}

internal readonly record struct OverlayRenderDecorationState(
    bool ShowBorder,
    bool ShowDragHint,
    bool ShowResizeHandle,
    string DragHintText,
    IntRect DragHintBounds,
    double DragHintFontPoints);

internal sealed partial class TokenStripForm : Form
{
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int CsDropShadow = 0x00020000;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding
        | TextFormatFlags.SingleLine
        | TextFormatFlags.EndEllipsis
        | TextFormatFlags.VerticalCenter;

    private OverlayPresentation _presentation;
    // While the pointer is over the capsule it steps aside so whatever it covers can be seen and clicked.
    // Hold Ctrl to keep it (for the click that opens the details).
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 80 };
    private Rectangle _hoverBounds;
    public bool HoverHidden { get; private set; }
    // Ctrl + pointer over the capsule: the panel lists the recent turns instead of the details.
    public bool ShowHistory { get; private set; }

    public void SetShowHistory(bool value)
    {
        if (ShowHistory == value) return;
        ShowHistory = value;
        Invalidate();
    }

    // Pinned: the list stays open without Ctrl (so it can be screenshotted) until it is clicked again.
    public bool HistoryPinned { get; private set; }

    public void SetHistoryPinned(bool value)
    {
        if (HistoryPinned == value) return;
        HistoryPinned = value;
        Invalidate();
    }

    // Width in pixels of the list's model-name column: the longest name shown (or its header).
    internal static int HistoryNameWidth(OverlayPresentation? presentation, uint dpi, int scalePercent)
    {
        if (presentation is null || !presentation.HasTable) return 0;
        var metrics = OverlayRenderMetrics.Create(dpi, scalePercent);
        using var font = new Font("Segoe UI", (float)metrics.LabelFontPoints * (dpi == 0 ? 96 : dpi) / 72f,
            FontStyle.Regular, GraphicsUnit.Pixel);
        return presentation.Tables.SelectMany(table => table.Rows.Select(row => row.ModelName).Append(table.Header[6]))
            .Max(text => AutoCapsule.Width(text, font)) + 4;
    }

    // The list's six fixed columns (when | tokens | time | whole-turn rate | model-request rate | cache) as parts of 95
    // of HistoryFixedWidthDip; the two parts left sit before the model name.
    internal static readonly int[] HistoryColumnParts = [11, 14, 21, 18, 14, 15];

    // Pixels each fixed column grows past its share so its widest text fits with the gap on both sides (user,
    // 2026-11-11: "2940만" and "2시간 11분" were cut off in the Codex list). All zero while everything fits, so a list
    // that fit before keeps its width and its lines. Measured with the normal row height's gap, the widest a row gets.
    internal static int[] HistoryColumnGrowth(OverlayPresentation? presentation, uint dpi, int scalePercent)
    {
        var growth = new int[HistoryColumnParts.Length];
        if (presentation is null || !presentation.HasTable) return growth;
        var metrics = OverlayRenderMetrics.Create(dpi, scalePercent);
        // The panel's padding comes off both sides before the columns share what is left, so a column's share is
        // measured against that, the narrowest the drawing gives them.
        var whole = OverlayLayoutCalculator.ScaleOverlayDip(OverlayLayoutCalculator.HistoryFixedWidthDip, dpi, scalePercent)
            - (2 * metrics.PanelPadding);
        var gap = Math.Max(4, OverlayLayoutCalculator.ScaleOverlayDip(OverlayLayoutCalculator.NormalRowHeightDip, dpi, scalePercent) / 6);
        var perPoint = (dpi == 0 ? 96 : dpi) / 72f;
        using var label = new Font("Segoe UI", (float)metrics.LabelFontPoints * perPoint, FontStyle.Regular, GraphicsUnit.Pixel);
        using var value = new Font("Segoe UI Semibold", (float)metrics.CompactValueFontPoints * perPoint, FontStyle.Regular, GraphicsUnit.Pixel);
        var slot = AutoCapsule.Width(" 00초", value);
        // Every table shown shares the same column lines, so each column fits the widest of all of them.
        var tables = presentation.Tables;
        var rows = tables.SelectMany(table => table.Rows).ToArray();
        int Head(int column) => tables.Max(table => AutoCapsule.Width(table.Header[column], label));
        int Numbers(Func<HistoryRow, string> pick, int column) =>
            Math.Max(rows.Max(row => AutoCapsule.Width(pick(row), value)), Head(column)) + (2 * gap) + 2;
        int Time(string text) => text.LastIndexOf(' ') is var split and >= 0
            ? AutoCapsule.Width(text[..split], value) + (2 * gap) + slot + 2
            : AutoCapsule.Width(text, value) + (2 * gap) + 2;
        int[] need =
        [
            Math.Max(rows.Max(row => AutoCapsule.Width(row.When, label)), Head(0)) + gap + 2,
            Numbers(row => row.Tokens, 1),
            Math.Max(rows.Max(row => Time(row.Seconds)), Head(2) + (2 * gap) + 2),
            Numbers(row => row.Rate, 3),
            Numbers(row => row.Model, 4),
            Numbers(row => row.Cache, 5),
        ];
        for (var i = 0; i < growth.Length; i++)
            growth[i] = Math.Max(0, need[i] - (whole * HistoryColumnParts[i] / 95));
        return growth;
    }

    // The fixed columns' width in pixels: HistoryFixedWidthDip plus whatever the columns grew by.
    internal static int HistoryFixedWidth(OverlayPresentation? presentation, uint dpi, int scalePercent) =>
        OverlayLayoutCalculator.ScaleOverlayDip(OverlayLayoutCalculator.HistoryFixedWidthDip, dpi, scalePercent)
        + HistoryColumnGrowth(presentation, dpi, scalePercent).Sum();

    // Width in pixels the list panel needs for its footer line (text plus the panel's own padding), 0 without one.
    internal static int HistoryFooterWidth(OverlayPresentation? presentation, uint dpi, int scalePercent)
    {
        if (presentation is null || presentation.HistoryFooterLines is not { Count: > 0 } footer) return 0;
        var metrics = OverlayRenderMetrics.Create(dpi, scalePercent);
        using var font = new Font("Segoe UI", (float)metrics.LabelFontPoints * (dpi == 0 ? 96 : dpi) / 72f,
            FontStyle.Regular, GraphicsUnit.Pixel);
        using var bold = new Font(font, FontStyle.Bold);
        return footer.Max(line => WeeklySplit.ParseLine(line) is { } bar ? WeeklyHeadWidth(bar, font, bold)
                : WeeklySplit.ParseReset(line) is not null ? AutoCapsule.Width(line, font) + 2 + WeeklyGap(font) + AutoCapsule.Width("경과 ", font) + 2
                    + AutoCapsule.Width("100%", bold) + 2
                : WeeklySplit.IsLogin(line) ? LoginLineWidth(line, font)
                : AutoCapsule.Width(line, font))
            + 4 + (2 * metrics.PanelPadding);
    }

    // The week's login line: what happened in the label font, then "눌러서 다시 로그인" larger, bold and red.
    internal static Font LoginActionFont(Font font) => new(font.FontFamily, font.Size * 1.15f, FontStyle.Bold, font.Unit);
    // A Go window's state when its limit is reached; such a row is drawn on red.
    internal const string BlockedState = "막힘";
    internal static readonly Color LoginRedDark = Color.FromArgb(255, 92, 92);
    internal static readonly Color LoginRedLight = Color.FromArgb(206, 32, 41);
    internal static int LoginLineWidth(string line, Font font)
    {
        using var action = LoginActionFont(font);
        return AutoCapsule.Width(line[..^WeeklySplit.LoginAction.Length], font) + 2 + AutoCapsule.Width(WeeklySplit.LoginAction, action) + 2;
    }


    // The session list's columns: name, time left and size, the mod's state, then the 압축 button, the 유지 switch and
    // the 핑 button at the right edge.
    internal readonly record struct SessionColumns(int Name, int Left, int State, int CompactButton, int KeepButton, int Button, int Gap)
    {
        public int Width => Name + Left + State + CompactButton + KeepButton + Button + (3 * Gap) + (2 * (Gap / 2));
    }

    internal enum SessionButton { Compact, Keep, Ping }

    internal static SessionColumns MeasureSessions(IReadOnlyList<SessionRow> rows, Font label, Font value) => new(
        rows.Select(r => AutoCapsule.Width(r.Name, value)).DefaultIfEmpty(0).Max() + 2,
        rows.Select(r => AutoCapsule.Width(r.Left, value)).DefaultIfEmpty(0).Max() + 2,
        rows.Select(r => AutoCapsule.Width(r.State, label)).DefaultIfEmpty(0).Max() + 2,
        // Each button is its word with a little room each side (user, 2026-11-12: "버튼 크기 줄여 좌우 여백을 줄여";
        // was 3/4 of the font's height a side).
        AutoCapsule.Width("압축", value) + (value.Height * 3 / 4),
        AutoCapsule.Width("유지", value) + (value.Height * 3 / 4), AutoCapsule.Width("핑", value) + (value.Height * 3 / 4),
        Math.Max(8, value.Height * 4 / 5));

    private static (Font Label, Font Value) SessionFonts(uint dpi, int scalePercent)
    {
        var metrics = OverlayRenderMetrics.Create(dpi, scalePercent);
        var pixelsPerPoint = (dpi == 0 ? 96 : dpi) / 72f;
        return (new Font("Segoe UI", (float)metrics.LabelFontPoints * pixelsPerPoint, FontStyle.Regular, GraphicsUnit.Pixel),
            new Font("Segoe UI Semibold", (float)metrics.CompactValueFontPoints * pixelsPerPoint, FontStyle.Regular, GraphicsUnit.Pixel));
    }

    // Width in pixels the session list needs (its rows or the line saying there are none, plus the panel's padding),
    // 0 when the list shows the recent turns.
    internal static int SessionListWidth(OverlayPresentation? presentation, uint dpi, int scalePercent)
    {
        if (presentation?.Sessions is not { } rows) return 0;
        var (label, value) = SessionFonts(dpi, scalePercent);
        using (label) using (value)
        {
            var inner = rows.Count == 0 ? AutoCapsule.Width(CachePings.NoneText, label) + 4 : MeasureSessions(rows, label, value).Width;
            return inner + (2 * OverlayRenderMetrics.Create(dpi, scalePercent).PanelPadding);
        }
    }

    // Where each session's 압축 button, 유지 switch and 핑 button are in the open list, in client pixels: drawn there and
    // clicked there.
    internal IReadOnlyList<(Rectangle Bounds, SessionRow Row, SessionButton Kind)> SessionButtons()
    {
        if (!ShowHistory || CurrentLayout is not { } layout || _presentation.Sessions is not { Count: > 0 } rows || layout.PanelBounds.IsEmpty) return [];
        var metrics = OverlayRenderMetrics.Create(layout.Dpi, layout.ScalePercent);
        var (label, value) = SessionFonts(layout.Dpi, layout.ScalePercent);
        using (label) using (value)
        {
            var columns = MeasureSessions(rows, label, value);
            var bounds = layout.PanelBounds.ToRectangle();
            var right = bounds.Right - metrics.PanelPadding;
            var top = bounds.Bottom - metrics.PanelPadding - (layout.ExpandedRowHeight * (_presentation.HistoryPanelRows - _presentation.SessionsTopRow));
            var inset = Math.Max(2, layout.ExpandedRowHeight / 7);
            var height = layout.ExpandedRowHeight - (2 * inset);
            return rows.SelectMany((row, index) =>
            {
                var y = top + (index * layout.ExpandedRowHeight) + inset;
                var keepRight = right - columns.Button - (columns.Gap / 2);
                var compactRight = keepRight - columns.KeepButton - (columns.Gap / 2);
                return new[]
                {
                    (new Rectangle(compactRight - columns.CompactButton, y, columns.CompactButton, height), row, SessionButton.Compact),
                    (new Rectangle(keepRight - columns.KeepButton, y, columns.KeepButton, height), row, SessionButton.Keep),
                    (new Rectangle(right - columns.Button, y, columns.Button, height), row, SessionButton.Ping)
                };
            }).ToArray();
        }
    }

    // The weekly bar's upper row: "이번주 17(토)~" at the left, then each part's dot and share, then "계정 38%".
    internal static int WeeklyDot(Font font) => Math.Max(6, font.Height * 9 / 20);
    internal static int WeeklyGap(Font font) => Math.Max(6, font.Height * 3 / 5);
    internal static int WeeklyHeadWidth(WeeklySplit.Bar bar, Font font, Font bold) =>
        AutoCapsule.Width("이번주 " + bar.Since, font) + 2
        + (bar.Who is { } who ? AutoCapsule.Width(who + " · ", bold) + 2 : 0)
        + bar.Parts.Sum(part => WeeklyGap(font) + WeeklyDot(font) + WeeklyDot(font) / 2 + AutoCapsule.Width($"{part.Name} {part.Percent}%", font) + 2)
        + WeeklyGap(font) + AutoCapsule.Width("계정 ", font) + 2 + AutoCapsule.Width($"{bar.Account}%", bold) + 2;

    public Rectangle CapsuleScreenBounds => CurrentLayout is null ? Rectangle.Empty : new Rectangle(
        Left + CurrentLayout.CapsuleBounds.X, Top + CurrentLayout.CapsuleBounds.Y,
        CurrentLayout.CapsuleBounds.Width, CurrentLayout.CapsuleBounds.Height);

    public void CancelHoverHide()
    {
        _hoverTimer.Stop();
        HoverHidden = false;
    }
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Dark);
    private OverlayEditGestureKind? _editGesture;
    private Point _gestureStartCursorScreen;
    private Rectangle _gestureStartBounds;
    private Point _fixedTopLeft;
    private int _gestureStartScalePercent = ManualAttachmentRules.DefaultScalePercent;
    private OverlayEditPreviewEventArgs? _lastEditPreview;

    public TokenStripForm()
    {
        Text = "TokenOverlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = _palette.Background;
        ForeColor = _palette.Value;
        Opacity = 0.97;
        DoubleBuffered = true;

        _presentation = OverlayPresentationBuilder.CreateWaiting(
            string.Empty,
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        ApplyLayout(new OverlayLayoutResult(
            OverlayVisualState.Collapsed,
            CollapsedDisplayMode.TwoFields,
            ExpansionDirection.Down,
            96,
            new IntRect(0, 0, 196, 34),
            new IntRect(0, 0, 196, 34),
            default,
            0));
    }

    public event EventHandler? CapsuleClicked;
    // A session's 핑 button or 유지 switch in the open list was clicked: the session id.
    public event EventHandler<string>? PingClicked;
    public event EventHandler<string>? KeepClicked;
    public event EventHandler<string>? CompactClicked;
    // The week's "… · 눌러서 다시 로그인" line was clicked.
    public event EventHandler? LoginClicked;
    // Where that line was last drawn in the open panel, in client pixels; empty when it is not shown.
    private Rectangle _loginLine;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditPreviewChanged;
    public event EventHandler<OverlayEditPreviewEventArgs>? EditGestureCompleted;
    public event EventHandler? EditSaveRequested;
    public event EventHandler? EditCancelRequested;

    public OverlayLayoutResult? CurrentLayout { get; private set; }
    public bool IsEditMode { get; private set; }
    internal OverlayPresentation CurrentPresentation => _presentation;
    internal OverlayThemePalette CurrentThemePalette => _palette;

    internal int SetBoundsCoreCallCount { get; private set; }
    internal bool IsEditGestureActive => _editGesture is not null;
    internal OverlayRenderDecorationState RenderDecorations
    {
        get
        {
            if (!IsEditMode
                || CurrentLayout is null
                || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return new OverlayRenderDecorationState(
                    false,
                    false,
                    false,
                    string.Empty,
                    default,
                    0d);
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var content = Rectangle.Inflate(
                capsule,
                -metrics.HorizontalPadding,
                0);
            var hintRight = Math.Max(
                content.Left,
                content.Right - metrics.EditHandleSize - metrics.MetricGap);
            return new OverlayRenderDecorationState(
                true,
                true,
                true,
                "끌어서 위치 조절",
                new IntRect(
                    content.Left,
                    content.Top,
                    hintRight - content.Left,
                    content.Height),
                metrics.LabelFontPoints);
        }
    }

    internal Rectangle EditResizeHandleBounds
    {
        get
        {
            if (CurrentLayout is null || CurrentLayout.CapsuleBounds.IsEmpty)
            {
                return Rectangle.Empty;
            }

            var metrics = OverlayRenderMetrics.Create(
                CurrentLayout.Dpi,
                CurrentLayout.ScalePercent);
            var capsule = CurrentLayout.CapsuleBounds.ToRectangle();
            var size = Math.Min(
                metrics.EditHandleSize,
                Math.Min(capsule.Width, capsule.Height));
            return new Rectangle(
                capsule.Right - size,
                capsule.Bottom - size,
                size,
                size);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr windowHandle);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr windowHandle, IntPtr region);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool PtInRegion(IntPtr region, int x, int y);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    // The shape the window itself holds (what Windows shows), for checks; true when the window has no shape.
    internal bool WindowShapeContains(int x, int y)
    {
        var region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(Handle, region) == 0 || PtInRegion(region, x, y); }
        finally { DeleteObject(region); }
    }

    protected override bool ShowWithoutActivation => !IsEditMode;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            if (!IsEditMode)
            {
                parameters.ExStyle |= WsExNoActivate;
            }
            parameters.ClassStyle |= CsDropShadow;
            return parameters;
        }
    }

    public void SetPresentation(OverlayPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        Invalidate();
    }

    public void ApplyTheme(OverlayThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (_palette == palette)
        {
            return;
        }

        _palette = palette;
        BackColor = palette.Background;
        ForeColor = palette.Value;
        Invalidate();
    }

    public void ApplyLayout(OverlayLayoutResult layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        // Another tool may minimize every window (e.g. screen automation); come back without taking focus.
        if (IsHandleCreated && IsIconic(Handle)) ShowWindow(Handle, 4);
        CurrentLayout = layout;
        SetBounds(
            layout.WindowBounds.X,
            layout.WindowBounds.Y,
            layout.WindowBounds.Width,
            layout.WindowBounds.Height,
            BoundsSpecified.All);

        var metrics = OverlayRenderMetrics.Create(layout.Dpi, layout.ScalePercent);
        using var combined = new GraphicsPath();
        if (!layout.CapsuleBounds.IsEmpty)
        {
            // With the CPU meter the module above runs the capsule's full width: one rounded rectangle for both.
            using var capsule = CreateRoundedRectanglePath(
                layout.FullTopRow ? CapsuleWithRow(layout) : layout.CapsuleBounds.ToRectangle(),
                metrics.CapsuleRadius);
            combined.AddPath(capsule, connect: false);
        }
        if (!layout.PanelBounds.IsEmpty)
        {
            using var panel = CreateRoundedRectanglePath(
                layout.PanelBounds.ToRectangle(),
                metrics.PanelRadius);
            combined.AddPath(panel, connect: false);
        }

        var shape = new Region(combined);
        if (!layout.RingModuleBounds.IsEmpty && !layout.FullTopRow)
        {
            // A union, not another figure in the same path: overlapping figures would cancel out where they meet.
            var block = layout.RingModuleBounds;
            using var module = RingModulePath(RectangleF.FromLTRB(block.X - 1, block.Y - 1, block.Right, block.Bottom),
                layout.CapsuleBounds.Y + metrics.CapsuleRadius, metrics.CapsuleRadius);
            shape.Union(module);
        }
        // Windows takes the shape when Region is assigned; changing the Region object afterwards does not reach the window.
        Region?.Dispose();
        Region = shape;
        Invalidate();
    }

    // The capsule and the full-width row on it, as one rectangle.
    internal static Rectangle CapsuleWithRow(OverlayLayoutResult layout) => Rectangle.FromLTRB(layout.CapsuleBounds.Left,
        layout.TopLineBounds.IsEmpty ? layout.RingModuleBounds.Top : layout.TopLineBounds.Top, layout.CapsuleBounds.Right, layout.CapsuleBounds.Bottom);

    // The CPU line's text (null: nothing known yet). Redrawn only when it changes.
    private string? _topLine;
    internal string? TopLine => _topLine;
    // The program file whose icon replaces the line's first word (CpuTop.Logo), and that icon at the size last drawn.
    private string? _topLogo;
    private (string Path, int Size, Bitmap? Image)? _topLogoImage;
    public void SetTopLine(string? text, string? logo = null)
    {
        if (text == _topLine && logo == _topLogo) return;
        _topLine = text;
        _topLogo = logo;
        if (CurrentLayout is { TopLineBounds.IsEmpty: false } l) Invalidate(l.TopLineBounds.ToRectangle());
    }

    // The meters' readings (0 to 1, null before the first one or when Windows gives none): CPU, memory, and the
    // graphics card's fan speed and temperature. Only a meter whose whole percent (or temperature) changes is redrawn.
    private double? _cpuFill, _ramFill;
    private GpuReading? _gpu;
    internal double? CpuFill => _cpuFill;
    internal double? RamFill => _ramFill;
    internal GpuReading? Gpu => _gpu;

    public void SetMeters(double? cpu, double? ram, GpuReading? gpu)
    {
        var layout = CurrentLayout is { FullTopRow: true } l ? l : null;
        void Set(ref double? field, double? fill, IntRect bar)
        {
            var changed = CpuPercent(fill) != CpuPercent(field);
            field = fill;
            if (changed && !bar.IsEmpty) Invalidate(bar.ToRectangle());
        }
        Set(ref _cpuFill, cpu, layout?.CpuBarBounds ?? default);
        Set(ref _ramFill, ram, layout?.RamBarBounds ?? default);
        var gpuChanged = GpuReading.Label(gpu) != GpuReading.Label(_gpu) || CpuPercent(gpu?.Fill) != CpuPercent(_gpu?.Fill);
        _gpu = gpu;
        if (gpuChanged && layout is { } shown && !shown.PageBarBounds.IsEmpty) Invalidate(shown.PageBarBounds.ToRectangle());
    }

    internal static int? CpuPercent(double? fill) => fill is double f ? (int)Math.Round(Math.Clamp(f, 0, 1) * 100) : null;

    public void BeginEditMode(int scalePercent)
    {
        if (IsEditMode)
        {
            return;
        }
        if (CurrentLayout?.State != OverlayVisualState.Collapsed
            || !CurrentLayout.PanelBounds.IsEmpty)
        {
            throw new InvalidOperationException("Edit mode can only start from the collapsed layout.");
        }

        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(scalePercent);
        IsEditMode = true;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        Activate();
        Focus();
        Invalidate();
    }

    public void EndEditMode()
    {
        if (!IsEditMode)
        {
            return;
        }

        CancelEditGesture();
        IsEditMode = false;
        if (IsHandleCreated)
        {
            RecreateHandle();
        }
        Invalidate();
    }

    internal void SimulateCapsuleClick(Point screenPoint) =>
        HandleMouseUp(MouseButtons.Left, PointToClient(screenPoint), screenPoint);

    internal void SimulateEditDrag(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateEditResize(Point startScreen, Point currentScreen)
    {
        HandleMouseDown(MouseButtons.Left, PointToClient(startScreen), startScreen);
        HandleMouseMove(PointToClient(currentScreen), currentScreen);
    }

    internal void SimulateClick(Point clientPoint) => HandleMouseUp(MouseButtons.Left, clientPoint, PointToScreen(clientPoint));

    internal void SimulateEditGestureCompleted(Point currentScreen) =>
        HandleMouseUp(MouseButtons.Left, PointToClient(currentScreen), currentScreen);

    internal void SimulateEditCaptureLost()
    {
        Capture = false;
        if (_editGesture is not null)
        {
            OnMouseCaptureChanged(EventArgs.Empty);
        }
    }

    internal bool SimulateEditCommand(Keys keyData) => HandleEditCommand(keyData);

    public bool ContainsScreenPoint(Point screenPoint) =>
        CurrentLayout?.ContainsScreenPoint(screenPoint) == true;

    protected override void SetBoundsCore(
        int x,
        int y,
        int width,
        int height,
        BoundsSpecified specified)
    {
        SetBoundsCoreCallCount++;
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmMouseActivate && !IsEditMode)
        {
            message.Result = (IntPtr)MaNoActivate;
            return;
        }

        if (message.Msg == WmNcHitTest && CurrentLayout is not null)
        {
            var packed = message.LParam.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            var clientPoint = PointToClient(screenPoint);
            if (!CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y)
                && !CurrentLayout.PanelBounds.Contains(clientPoint.X, clientPoint.Y))
            {
                message.Result = (IntPtr)HtTransparent;
                return;
            }
        }

        base.WndProc(ref message);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        base.OnMouseDown(eventArgs);
        HandleMouseDown(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseMove(MouseEventArgs eventArgs)
    {
        base.OnMouseMove(eventArgs);
        HideForHover();
        HandleMouseMove(eventArgs.Location, PointToScreen(eventArgs.Location));
    }

    private void HideForHover()
    {
        if (HoverHidden || IsEditMode || _editGesture is not null
            || CurrentLayout is null || CurrentLayout.State == OverlayVisualState.Expanded
            || (ModifierKeys & Keys.Control) != 0 || MouseButtons != MouseButtons.None)
        {
            return;
        }

        HoverHidden = true;
        _hoverBounds = Rectangle.Inflate(Bounds, 6, 6);
        _hoverTimer.Tick -= HandleHoverTick;
        _hoverTimer.Tick += HandleHoverTick;
        _hoverTimer.Start();
        Hide();
    }

    private void HandleHoverTick(object? sender, EventArgs eventArgs)
    {
        if (_hoverBounds.Contains(Cursor.Position) && (ModifierKeys & Keys.Control) == 0) return;
        // The owner shows the form again on its next layout pass, if it should still be on screen.
        _hoverTimer.Stop();
        HoverHidden = false;
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        base.OnMouseUp(eventArgs);
        HandleMouseUp(
            eventArgs.Button,
            eventArgs.Location,
            PointToScreen(eventArgs.Location));
    }

    protected override void OnMouseCaptureChanged(EventArgs eventArgs)
    {
        base.OnMouseCaptureChanged(eventArgs);
        if (Capture || _editGesture is null)
        {
            return;
        }

        if (_lastEditPreview is not null)
        {
            CompleteEditGesture(_lastEditPreview, releaseCapture: false);
        }
        else
        {
            CancelEditGesture(releaseCapture: false);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (IsEditMode && HandleEditCommand(keyData))
        {
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private void HandleMouseDown(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (!IsEditMode
            || button != MouseButtons.Left
            || CurrentLayout is null
            || !CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y))
        {
            return;
        }

        _editGesture = EditResizeHandleBounds.Contains(clientPoint)
            ? OverlayEditGestureKind.Resize
            : OverlayEditGestureKind.Move;
        _gestureStartCursorScreen = cursorScreen;
        _gestureStartBounds = Bounds;
        _fixedTopLeft = Location;
        _gestureStartScalePercent = ManualAttachmentRules.SanitizeScale(
            CurrentLayout.ScalePercent);
        _lastEditPreview = null;
        Capture = true;
    }

    private void HandleMouseMove(Point clientPoint, Point cursorScreen)
    {
        _ = clientPoint;
        if (!IsEditMode || _editGesture is null)
        {
            return;
        }

        var deltaX = cursorScreen.X - _gestureStartCursorScreen.X;
        var deltaY = cursorScreen.Y - _gestureStartCursorScreen.Y;
        var scalePercent = _gestureStartScalePercent;
        if (_editGesture == OverlayEditGestureKind.Move)
        {
            Location = new Point(
                _gestureStartBounds.X + deltaX,
                _gestureStartBounds.Y + deltaY);
        }
        else
        {
            scalePercent = ManualAttachmentCalculator.CalculateScale(
                _gestureStartBounds.Size,
                _gestureStartScalePercent,
                deltaX,
                deltaY);
        }

        _lastEditPreview = new OverlayEditPreviewEventArgs(
            _editGesture.Value,
            cursorScreen,
            _fixedTopLeft,
            scalePercent);
        EditPreviewChanged?.Invoke(this, _lastEditPreview);
    }

    private void HandleMouseUp(MouseButtons button, Point clientPoint, Point cursorScreen)
    {
        if (button != MouseButtons.Left)
        {
            return;
        }

        if (IsEditMode)
        {
            if (_editGesture is null)
            {
                return;
            }

            HandleMouseMove(clientPoint, cursorScreen);
            var completed = _lastEditPreview ?? new OverlayEditPreviewEventArgs(
                _editGesture.Value,
                cursorScreen,
                _fixedTopLeft,
                _gestureStartScalePercent);
            CompleteEditGesture(completed, releaseCapture: true);
            return;
        }

        if (SessionButtons().FirstOrDefault(button => button.Bounds.Contains(clientPoint)) is { Row: { } pressed } hit)
        {
            if (hit.Kind == SessionButton.Keep && pressed.CanKeep) KeepClicked?.Invoke(this, pressed.Id);
            else if (hit.Kind == SessionButton.Ping && pressed.CanPing) PingClicked?.Invoke(this, pressed.Id);
            else if (hit.Kind == SessionButton.Compact && pressed.CanCompact) CompactClicked?.Invoke(this, pressed.Id);
            return;
        }

        if (ShowHistory && _loginLine.Contains(clientPoint))
        {
            LoginClicked?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (CurrentLayout is not null
            && CurrentLayout.CapsuleBounds.Contains(clientPoint.X, clientPoint.Y))
        {
            CapsuleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CompleteEditGesture(
        OverlayEditPreviewEventArgs completed,
        bool releaseCapture)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
        EditGestureCompleted?.Invoke(this, completed);
    }

    private void CancelEditGesture(bool releaseCapture = true)
    {
        _editGesture = null;
        _lastEditPreview = null;
        if (releaseCapture && Capture)
        {
            Capture = false;
        }
    }

    private bool HandleEditCommand(Keys keyData)
    {
        if (!IsEditMode)
        {
            return false;
        }

        switch (keyData & Keys.KeyCode)
        {
            case Keys.Enter:
                EditSaveRequested?.Invoke(this, EventArgs.Empty);
                return true;
            case Keys.Escape:
                EditCancelRequested?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelEditGesture();
            _hoverTimer.Dispose();
            _topLogoImage?.Image?.Dispose();
            Region?.Dispose();
            Region = null;
        }
        base.Dispose(disposing);
    }
}
