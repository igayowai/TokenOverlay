namespace TokenOverlay;

internal sealed record CapsuleMeasurements(Size Size, int Primary, int Middle, int Secondary, int Gap, int Padding)
{
    public int Icon { get; init; }
    public int ValueGap { get; init; }
    public int Dot { get; init; }
    // Room left after the icon so a count in the lower-left note fits under the token count; and, from the content's
    // left edge, where both counts end (0 = the note has no count).
    public int Shift { get; init; }
    public int CountRight { get; init; }
    // Two-line capsule: height of the main row text and of the smaller line under it (0 = single line).
    public int MainHeight { get; init; }
    public int SubHeight { get; init; }
    // Claude's own lines under the main row (1 or 2, each SubHeight tall, LineGap apart).
    public int SubLines { get; init; }
    // The worker's rows under Claude's lines (0 = none).
    public int ExtraHeight { get; init; }
    // Space added between the stacked lines (0 = touching).
    public int LineGap { get; init; }
    // Space above the third line: the line gap plus room for the divider that sets it apart.
    public int ExtraGap { get; init; }
}

// Measure and paint with identical pixel fonts and GDI flags, independent of the current monitor DC.
internal static class AutoCapsule
{
    internal const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
    // The cache bar is sized for its longest text so the capsule does not change width as the minutes count down.
    internal const string CacheBarSample = "캐시 60분 남음";
    internal static int CacheBarWidth(Font label, int height) => Width(CacheBarSample, label) + height;
    // Extra space between the capsule's stacked lines, as a share of a line's height.
    internal static int LineGapPercent = 15;
    // Smaller label text ("소모", "TPS", "모델") sits on the bold number's baseline instead of being centred on its own.
    internal static bool BaselineAlign = true;
    // How far down label text moves so its baseline meets the value's, when both are centred in the same line box.
    // Measured on the pixels the way the capsule draws ("H" has no descender or overshoot), so no rounding of font
    // metrics can leave the label a pixel off.
    internal static int BaselineShift(Font label, Font value)
    {
        if (!BaselineAlign) return 0;
        var key = (label.Name, label.Size, value.Name, value.Size);
        if (_shift is { } known && known.Key == key) return known.Shift;
        var box = TextRenderer.MeasureText("가Ag", value, Size.Empty, Flags).Height;
        int Bottom(Font font)
        {
            using var bitmap = new Bitmap(box * 2, box);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                TextRenderer.DrawText(g, "H", font, new Rectangle(0, 0, box * 2, box), Color.Black, Color.White, Flags | TextFormatFlags.VerticalCenter);
            }
            for (var y = box - 1; y >= 0; y--)
                for (var x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y).R < 128) return y;
            return 0;
        }
        var shift = Bottom(value) - Bottom(label);
        _shift = (key, shift);
        return shift;
    }
    private static ((string, float, string, float) Key, int Shift)? _shift;
    // Room for the divider above the third line (another worker's line, set apart from Claude's own two).
    internal static int DividerSpace(int textHeight) => Math.Max(3, textHeight / 4);
    private static (string, string, string, string, string, string?, uint, int, HostApp, bool, bool, string?)? _lastKey;
    private static CapsuleMeasurements? _last;

    public static Font LabelFont(uint dpi, int scale) => PixelFont("Segoe UI", 10, dpi, scale);
    public static Font ValueFont(uint dpi, int scale) => PixelFont("Segoe UI Semibold", 12, dpi, scale);
    // Percentage inside the context ring.
    public static Font RingFont(uint dpi, int scale) => PixelFont("Segoe UI Semibold", (float)(8 * RingLines / 2), dpi, scale);
    // Ring height in capsule lines; the percentage grows with it.
    internal const double RingLines = 2.8;
    // The ring is as tall as the capsule's two lines; the module's disc adds a margin around it.
    internal static (int Disc, int Margin) RingSize(uint dpi, int scale)
    {
        using var value = ValueFont(dpi, scale);
        var line = TextRenderer.MeasureText("가Ag", value, Size.Empty, Flags).Height;
        var margin = Math.Max(2, line / 10);
        return ((int)Math.Round(RingLines * line) + 2 * margin, margin);
    }
    // The widest share a meter shows inside its bar: the bar is never narrower than it.
    internal const string MeterNumberSample = "100";
    // Meter bars have Task Manager's tray icon's proportions, 7 wide to 8 tall.
    internal const double MeterAspect = 7 / 8.0;
    // The meters inside the row (from its top left), CPU, memory and page file: bars side by side with a margin between,
    // each as tall as the ring less a margin above and below, the first far enough right to clear the row's rounded
    // corner. `room`: the row's width left of the ring and its lines; where 7 to 8 would not leave room for all three
    // (96 DPI, 2026-11-07), the bars narrow to fit, never below the widest number.
    internal static Rectangle[] MeterBars(int disc, int margin, int radius, uint dpi, int scale, int room)
    {
        using var font = MeterFont(dpi, scale);
        var number = TextRenderer.MeasureText(MeterNumberSample, font, Size.Empty, Flags).Width;
        var top = 2 * margin;
        var height = disc - 4 * margin;
        var clear = top >= radius ? 0 : (int)Math.Ceiling(radius - Math.Sqrt(radius * radius - (radius - top) * (radius - top)));
        var left = Math.Max(top, clear + margin);
        var fits = (room - left - 2 * margin - 2 * margin) / 3;
        var width = Math.Max(number + 2 * margin, Math.Min(fits, (int)Math.Round(height * MeterAspect)));
        return Enumerable.Range(0, 3).Select(i => new Rectangle(left + i * (width + margin), top, width, height)).ToArray();
    }
    // The share inside a meter's bar.
    public static Font MeterFont(uint dpi, int scale) => PixelFont("Segoe UI Semibold", 11, dpi, scale);
    // The width the ring's lines take in the ring module: the widest line with a margin each side, or nothing.
    internal static int RingNoteWidth(uint dpi, int scale, string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return 0;
        using var font = LabelFont(dpi, scale);
        return note.Split('\n').Max(text => TextRenderer.MeasureText(text, font, Size.Empty, Flags).Width) + 4 * RingSize(dpi, scale).Margin;
    }
    // The worker's rows under its name: a provider at the left ("" when none) beside a rate or cache line at the
    // right ("" when none), as many rows as the longer list.
    internal static (string Left, string Right)[] WorkerRows(OverlayPresentation p)
    {
        var right = new[] { p.WorkerSubLine, p.WorkerThirdLine, p.WorkerCacheLine }.Where(l => !string.IsNullOrEmpty(l)).ToArray();
        var left = string.IsNullOrEmpty(p.WorkerProvider) ? [] : p.WorkerProvider.Split('\n');
        return Enumerable.Range(0, Math.Max(left.Length, right.Length))
            .Select(i => (left.ElementAtOrDefault(i) ?? "", right.ElementAtOrDefault(i) ?? "")).ToArray();
    }

    // "모델 71.50 TPS" → label, value, unit: drawn like the metrics above (label font + bold value font).
    // Split from the end, so a label of several words ("DeepSeek V4.1 Flash 247 TPS") keeps "TPS" last, in its column.
    internal static (string Label, string Value, string Unit) SplitSubLine(string line)
    {
        var parts = line.Split(' ');
        return parts.Length switch
        {
            1 => (parts[0], "", ""),
            2 => (parts[0], parts[1], ""),
            _ => (string.Join(' ', parts[..^2]), parts[^2], parts[^1])
        };
    }

    // "캐시 만료 109.0k" → ("캐시 만료", "109.0k"): a note that ends in a token count, drawn in the token column.
    internal static (string Label, string Count)? SplitNoteCount(string? note)
    {
        var space = note?.LastIndexOf(' ') ?? -1;
        if (space <= 0) return null;
        var count = note![(space + 1)..];
        return System.Text.RegularExpressions.Regex.IsMatch(count, @"^\d+(\.\d+)?[kM]?$") ? (note[..space], count) : null;
    }

    // "캐시히트 99.1%" → ("캐시히트", "99.1%"): a note that ends in a share, drawn at the lower left like "소모 4.72M".
    internal static (string Label, string Value)? SplitNotePercent(string? note)
    {
        var space = note?.LastIndexOf(' ') ?? -1;
        if (space <= 0) return null;
        var share = note![(space + 1)..];
        return System.Text.RegularExpressions.Regex.IsMatch(share, @"^\d+(\.\d+)?%$") ? (note[..space], share) : null;
    }

    internal static int SubLineWidth(string line, Font label, Font value, int gap)
    {
        var (prefix, number, unit) = SplitSubLine(line);
        return Width(prefix, label) + (number.Length > 0 ? gap + Width(number, value) : 0)
            + (unit.Length > 0 ? gap + Width(unit, label) : 0) + 2;
    }
    private static Font PixelFont(string family, float points, uint dpi, int scale) =>
        new(family, points * (dpi == 0 ? 96 : dpi) / 72f * scale / 100f, FontStyle.Regular, GraphicsUnit.Pixel);
    internal static int Width(string text, Font font) => TextRenderer.MeasureText(text, font, Size.Empty, Flags).Width;

    // "⚡ TPS" → ("⚡", "TPS") when the unit trails the value; otherwise the whole label leads.
    internal static (string Prefix, string Unit) SplitLabel(OverlayMetric metric)
    {
        if (!metric.LabelAfterValue) return (metric.CompactLabel, "");
        var space = metric.CompactLabel.LastIndexOf(' ');
        return space < 0 ? ("", metric.CompactLabel) : (metric.CompactLabel[..space], metric.CompactLabel[(space + 1)..]);
    }

    // Space between the icon, the token count, the seconds and the speed (two thirds of the earlier 2 x MetricGap).
    internal static int ItemGap(OverlayRenderMetrics m) => Math.Max(5, m.MetricGap * 4 / 3);
    // Between the three numbers themselves: narrower, so "1분 2초" (which has its own space) still reads as one item.
    internal static int ValueGap(OverlayRenderMetrics m) => Math.Max(4, m.MetricGap);

    internal static int IconSize(Font valueFont) =>
        TextRenderer.MeasureText("가Ag", valueFont, Size.Empty, Flags).Height * 9 / 10;

    public static CapsuleMeasurements Measure(OverlayPresentation p, uint dpi, int scale)
    {
        var key = (p.Primary.CompactLabel, p.Primary.Value, p.CompactMiddle?.Value ?? "",
            p.Secondary.CompactLabel, p.Secondary.Value, p.StatusText, dpi, scale,
            p.App, p.Running, p.Secondary.LabelAfterValue, p.SubLine + "\u0001" + p.SubLine2 + "\u0001" + p.SubLineLeft + (p.SubLineLeftBar is null ? "" : "\u0001bar") + "\u0001" + p.WorkerLine + "\u0001" + p.WorkerSubLine + "\u0001" + p.WorkerThirdLine + "\u0001" + p.WorkerCacheLine + "\u0001" + p.WorkerProvider + "\u0001" + LineGapPercent);
        if (_lastKey == key && _last is not null) return _last;
        using var label = LabelFont(dpi, scale);
        using var value = ValueFont(dpi, scale);
        var m = OverlayRenderMetrics.Create(dpi, scale);
        int Metric(OverlayMetric metric)
        {
            var (prefix, unit) = SplitLabel(metric);
            var total = Width(metric.Value, value) + 2;
            if (prefix.Length > 0) total += Width(prefix, label) + m.CompactMetricGap;
            if (unit.Length > 0) total += Width(unit, label) + m.CompactMetricGap;
            return total;
        }
        var primary = Metric(p.Primary);
        var middle = Width(p.CompactMiddle?.Value ?? "", value) + 2;
        var secondary = Metric(p.Secondary);
        var gap = ItemGap(m);
        var textHeight = TextRenderer.MeasureText("가Ag", value, Size.Empty, Flags).Height;
        var icon = p.App == HostApp.None ? 0 : IconSize(value);
        var dot = p.Running ? Math.Max(4, textHeight / 3) : 0;
        var lead = (icon > 0 ? icon + gap : 0) + (dot > 0 ? dot + m.CompactMetricGap : 0);
        var noteCount = string.IsNullOrEmpty(p.SubLine) || p.SubLineLeftBar is not null ? null : SplitNoteCount(p.SubLineLeft);
        // The note's label and count must fit left of where the token count ends; if not, the top row moves right.
        var shift = noteCount is var (noteLabel, count)
            ? Math.Max(0, Width(noteLabel, label) + m.CompactMetricGap + Width(count, value) + 2 - lead - primary) : 0;
        var width = primary + middle + secondary + 2 * ValueGap(m) + 2 * m.HorizontalPadding + lead + shift;
        if (!string.IsNullOrWhiteSpace(p.StatusText))
            width = Width(p.StatusText, label) + 2 * m.HorizontalPadding + 2 + (icon > 0 ? icon + gap : 0);
        var height = Math.Max(OverlayLayoutCalculator.GetCollapsedSize(dpi, scale, CollapsedDisplayMode.TwoFields).Height,
            TextRenderer.MeasureText("가Ag", value, Size.Empty, Flags).Height + 2 * m.MetricGap);
        var subHeight = 0;
        var subLines = 0;
        var lineGap = (int)Math.Round(textHeight * LineGapPercent / 100.0);
        if (!string.IsNullOrEmpty(p.SubLine) && string.IsNullOrWhiteSpace(p.StatusText))
        {
            subLines = string.IsNullOrEmpty(p.SubLine2) ? 1 : 2;
            subHeight = textHeight;
            // The cache note sits at the lower left, on the lowest rate line.
            var share = noteCount is null && p.SubLineLeftBar is null ? SplitNotePercent(p.SubLineLeft) : null;
            var left = string.IsNullOrEmpty(p.SubLineLeft) ? 0
                : share is var (shareLabel, shareValue) ? Width(shareLabel, label) + m.CompactMetricGap + Width(shareValue, value) + 2 + gap
                : (noteCount is not null ? lead + shift + primary
                    : p.SubLineLeftBar is null ? Width(p.SubLineLeft, label) : CacheBarWidth(label, subHeight)) + gap;
            width = Math.Max(width, (subLines == 1 ? left : 0) + SubLineWidth(p.SubLine, label, value, m.CompactMetricGap) + 2 * m.HorizontalPadding);
            height = Math.Max(height, textHeight + subHeight + m.MetricGap + m.MetricGap / 2) + lineGap;
            if (subLines == 2)
            {
                width = Math.Max(width, left + SubLineWidth(p.SubLine2!, label, value, m.CompactMetricGap) + 2 * m.HorizontalPadding);
                height += subHeight + lineGap;
            }
        }
        var extraHeight = 0;
        if (subHeight > 0 && !string.IsNullOrEmpty(p.WorkerLine))
        {
            // The worker's name at the left with its rate at the right, then its 모델, 생성 and cache hit lines at the
            // right and its providers at the left, one per row, as many rows as the longer of the two.
            var rows = WorkerRows(p);
            extraHeight = (1 + rows.Length) * subHeight + rows.Length * lineGap;
            width = Math.Max(width, SubLineWidth(p.WorkerLine, label, value, m.CompactMetricGap) + gap + 2 * m.HorizontalPadding);
            // A row is drawn leaving a part gap (and a pixel per part) before where it begins.
            foreach (var (left, right) in rows)
                width = Math.Max(width, (left.Length > 0 ? Width(left, label) + gap : 0)
                    + (right.Length > 0 ? SubLineWidth(right, label, value, m.CompactMetricGap) + (left.Length > 0 ? m.CompactMetricGap + 2 : 0) : 0)
                    + 2 * m.HorizontalPadding);
            height += extraHeight + lineGap + DividerSpace(textHeight);
        }
        _lastKey = key;
        return _last = new(new Size(width, height), primary, middle, secondary, gap, m.HorizontalPadding)
            { ValueGap = ValueGap(m), Icon = icon, Dot = dot, Shift = shift, CountRight = noteCount is null ? 0 : lead + shift + primary, MainHeight = textHeight, SubHeight = subHeight, SubLines = subLines, ExtraHeight = extraHeight, LineGap = subHeight > 0 ? lineGap : 0, ExtraGap = extraHeight > 0 ? lineGap + DividerSpace(textHeight) : 0 };
    }
}
