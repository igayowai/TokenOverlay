using System.Drawing.Drawing2D;

namespace TokenOverlay;

internal sealed partial class TokenStripForm
{
    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (CurrentLayout is null)
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var metrics = OverlayRenderMetrics.Create(
            CurrentLayout.Dpi,
            CurrentLayout.ScalePercent);
        var decorations = RenderDecorations;
        // Sized in pixels from the window's current DPI: point sizes follow the DPI the process started with,
        // which is wrong after the display scale changes (the capsule fonts are sized the same way).
        var pixelsPerPoint = (CurrentLayout.Dpi == 0 ? 96 : CurrentLayout.Dpi) / 72f;
        using var labelFont = new Font(
            "Segoe UI",
            (float)(decorations.ShowDragHint
                ? decorations.DragHintFontPoints
                : metrics.LabelFontPoints) * pixelsPerPoint,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var compactValueFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.CompactValueFontPoints * pixelsPerPoint,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var panelHeaderFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.PanelHeaderFontPoints * pixelsPerPoint,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var highlightedValueFont = new Font(
            "Segoe UI Semibold",
            (float)metrics.HighlightedValueFontPoints * pixelsPerPoint,
            FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var backgroundBrush = new SolidBrush(_palette.Background);
        using var borderPen = new Pen(_palette.Border, metrics.StrokeWidth);
        using var dividerPen = new Pen(_palette.Divider, metrics.StrokeWidth);
        using var progressTrackBrush = new SolidBrush(_palette.ProgressTrack);

        DrawCapsule(
            eventArgs.Graphics,
            labelFont,
            compactValueFont,
            backgroundBrush,
            borderPen,
            dividerPen,
            metrics,
            decorations);
        if (!CurrentLayout.PanelBounds.IsEmpty)
        {
            DrawPanel(
                eventArgs.Graphics,
                labelFont,
                compactValueFont,
                panelHeaderFont,
                highlightedValueFont,
                backgroundBrush,
                borderPen,
                dividerPen,
                progressTrackBrush,
                metrics,
                decorations);
        }
    }

    private void DrawCapsule(
        Graphics graphics,
        Font labelFont,
        Font valueFont,
        Brush backgroundBrush,
        Pen borderPen,
        Pen dividerPen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        var layout = CurrentLayout!;
        var bounds = layout.CapsuleBounds.ToRectangle();
        // With the CPU meter the module and the capsule are one rounded rectangle.
        var full = layout.FullTopRow;
        using var path = CreateRoundedRectanglePath(full ? CapsuleWithRow(layout) : bounds, metrics.CapsuleRadius);
        // The ring module: one block (round top, straight sides into the capsule) holding the ring and its lines.
        var block = layout.RingModuleBounds.ToRectangle();
        var hasModule = !layout.RingModuleBounds.IsEmpty;
        using var module = !hasModule || full ? null : RingModulePath(block, bounds.Top + metrics.CapsuleRadius, metrics.CapsuleRadius);
        if (module is not null)
        {
            // The module first, so the capsule's border runs over its foot and is then covered again: one piece
            // with the capsule.
            graphics.FillPath(backgroundBrush, module);
            if (decorations.ShowBorder)
            {
                using var outline = RingModulePath(RectangleF.Inflate(block, -0.5f, -0.5f), bounds.Top, metrics.CapsuleRadius, closed: false,
                    rightBottom: bounds.Top + metrics.CapsuleRadius);
                graphics.DrawPath(borderPen, outline);
            }
        }
        graphics.FillPath(backgroundBrush, path);
        if (decorations.ShowBorder)
        {
            graphics.DrawPath(borderPen, path);
        }
        if (hasModule)
        {
            if (module is not null)
                using (var foot = RingModulePath(RectangleF.FromLTRB(block.Left + 1, block.Top, block.Right - 1, block.Bottom), bounds.Top + metrics.CapsuleRadius, metrics.CapsuleRadius))
                    graphics.FillPath(backgroundBrush, foot);
            var inset = AutoCapsule.RingSize(layout.Dpi, layout.ScalePercent).Margin;
            if (!layout.RingBounds.IsEmpty && _presentation.ContextFill is double fill)
                DrawContextRing(graphics, fill, Rectangle.Inflate(layout.RingBounds.ToRectangle(), -inset, -inset), layout);
            if (!layout.RingNoteBounds.IsEmpty && _presentation.RingNote is { } note) DrawRingNote(graphics, note, layout.RingNoteBounds.ToRectangle(), layout);
            if (full)
            {
                DrawMeter(graphics, layout.CpuBarBounds, _cpuFill, CpuBlue, layout);
                DrawMeter(graphics, layout.RamBarBounds, _ramFill, RamPurple, layout);
                DrawMeter(graphics, layout.PageBarBounds, _gpu?.Fill, PageGreen, layout, GpuReading.Label(_gpu));
                if (!layout.TopLineBounds.IsEmpty) DrawTopLine(graphics, layout, metrics);
            }
            // A line where the module meets the capsule: the ring (with its lines) is its own module, not part of the
            // capsule's rates under it.
            using var joint = new Pen(Color.FromArgb(110, _palette.Label), Math.Max(1f, metrics.StrokeWidth));
            graphics.DrawLine(joint, block.Left + metrics.CapsuleRadius, bounds.Top, block.Right - metrics.CapsuleRadius, bounds.Top);
        }

        if (decorations.ShowDragHint)
        {
            TextRenderer.DrawText(
                graphics,
                decorations.DragHintText,
                labelFont,
                decorations.DragHintBounds.ToRectangle(),
                _palette.Label,
                TextFlags | TextFormatFlags.HorizontalCenter);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        var padding = metrics.HorizontalPadding;
        var content = Rectangle.Inflate(bounds, -padding, 0);
        if (!string.IsNullOrWhiteSpace(_presentation.StatusText))
        {
            if (_presentation.App != HostApp.None)
            {
                using var iconFont = AutoCapsule.ValueFont(layout.Dpi, layout.ScalePercent);
                var size = Math.Min(AutoCapsule.IconSize(iconFont), content.Height);
                if (AppIconCache.Get(_presentation.App, size) is { } icon)
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(icon, new Rectangle(
                        content.Left, content.Top + (content.Height - size) / 2, size, size));
                }
                var used = size + AutoCapsule.ItemGap(metrics);
                content = new Rectangle(content.Left + used, content.Top, content.Width - used, content.Height);
            }
            TextRenderer.DrawText(
                graphics,
                _presentation.StatusText,
                labelFont,
                content,
                _palette.Label,
                TextFlags | TextFormatFlags.HorizontalCenter);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        if (layout.CollapsedDisplay == CollapsedDisplayMode.PrimaryOnly)
        {
            DrawCompactMetric(graphics, _presentation.Primary, content, labelFont, valueFont, metrics);
            DrawEditHandle(graphics, dividerPen, metrics, decorations);
            return;
        }

        var gap = metrics.MetricGap;
        if (_presentation.CompactMiddle is { } middle)
        {
            var measured = AutoCapsule.Measure(_presentation, layout.Dpi, layout.ScalePercent);
            using var measuredLabel = AutoCapsule.LabelFont(layout.Dpi, layout.ScalePercent);
            using var measuredValue = AutoCapsule.ValueFont(layout.Dpi, layout.ScalePercent);
            if (measured.SubHeight > 0)
            {
                // Main row on top, the smaller model-request line right-aligned under the rate.
                var gaps = measured.LineGap + measured.ExtraGap;
                var claudeLines = measured.SubLines * measured.SubHeight + (measured.SubLines - 1) * measured.LineGap;
                var top = bounds.Top + (bounds.Height - measured.MainHeight - claudeLines - measured.ExtraHeight - gaps) / 2;
                var subTop = top + measured.MainHeight + measured.LineGap;
                var drop = AutoCapsule.BaselineShift(measuredLabel, measuredValue);
                // Label, bold value and unit, drawn from the right edge leftward; returns where the text begins.
                int DrawRightLine(string line, int lineTop, int lineHeight, bool labelAtLeft = false, Color? valueColor = null)
                {
                    var (label, value, unit) = AutoCapsule.SplitSubLine(line);
                    var right = content.Right;
                    void Part(string text, Font font, Color color)
                    {
                        if (text.Length == 0) return;
                        var partWidth = AutoCapsule.Width(text, font);
                        TextRenderer.DrawText(graphics, text, font,
                            new Rectangle(right - partWidth - 1, lineTop + (font == measuredLabel ? drop : 0), partWidth + 1, lineHeight), color,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter);
                        right -= partWidth + 1 + metrics.CompactMetricGap;
                    }
                    // A rate not known yet ("생성 —"): the dash in the numbers' column, the unit's place left empty, so
                    // it lines up with the rates above it (the user's ask, 2026-11-10).
                    if (unit.Length == 0 && value == "—") right -= AutoCapsule.Width("TPS", measuredLabel) + 1 + metrics.CompactMetricGap;
                    Part(unit, measuredLabel, _palette.Label);
                    Part(value, measuredValue, valueColor ?? _palette.Value);
                    if (labelAtLeft)
                        TextRenderer.DrawText(graphics, label, measuredLabel,
                            new Rectangle(content.Left, lineTop + drop, Math.Max(0, right - content.Left), lineHeight), _palette.Label,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    else Part(label, measuredLabel, _palette.Label);
                    return right;
                }
                var subRight = DrawRightLine(_presentation.SubLine!, subTop, measured.SubHeight);
                var (lowTop, lowRight) = (subTop, subRight);
                if (measured.SubLines == 2)
                {
                    lowTop = subTop + measured.SubHeight + measured.LineGap;
                    lowRight = DrawRightLine(_presentation.SubLine2!, lowTop, measured.SubHeight);
                }
                if (measured.ExtraHeight > 0)
                {
                    // A divider across the capsule under Claude's lines; the outside worker's rows sit below it,
                    // its rate coloured like Claude's above it.
                    var claudeBottom = subTop + claudeLines;
                    var dividerY = claudeBottom + measured.ExtraGap / 2;
                    graphics.DrawLine(dividerPen, content.Left, dividerY, content.Right, dividerY);
                    var workerTop = claudeBottom + measured.ExtraGap;
                    DrawRightLine(_presentation.WorkerLine!, workerTop, measured.SubHeight, labelAtLeft: true,
                        valueColor: ValueColorFor(_presentation.Secondary));
                    // Each row: a provider at the left, clipped before the rate drawn at the right of it.
                    foreach (var (provider, rate) in AutoCapsule.WorkerRows(_presentation))
                    {
                        workerTop += measured.SubHeight + measured.LineGap;
                        var rowLeft = rate.Length > 0 ? DrawRightLine(rate, workerTop, measured.SubHeight) - measured.Gap : content.Right;
                        if (provider.Length > 0)
                            TextRenderer.DrawText(graphics, provider, measuredLabel,
                                new Rectangle(content.Left, workerTop + drop, Math.Max(0, rowLeft - content.Left), measured.SubHeight), _palette.Label,
                                AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    }
                }
                // The cache note at the lower left, on the lowest rate line (생성).
                if (_presentation.SubLineLeft is { Length: > 0 } cacheNote)
                {
                    var noteColor = _presentation.SubLineLeftAlert ? _palette.Accent : _palette.Value;
                    if (measured.CountRight > 0 && AutoCapsule.SplitNoteCount(cacheNote) is var (noteLabel, count))
                    {
                        // "캐시 만료 109.0k": the count ends where the token count above it ends, in the same font.
                        var countRight = content.Left + measured.CountRight;
                        var countWidth = AutoCapsule.Width(count, measuredValue) + 2;
                        TextRenderer.DrawText(graphics, count, measuredValue,
                            new Rectangle(countRight - countWidth, lowTop, countWidth, measured.SubHeight), noteColor,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                        var labelRight = countRight - countWidth - metrics.CompactMetricGap;
                        TextRenderer.DrawText(graphics, noteLabel, measuredLabel,
                            new Rectangle(content.Left, lowTop + drop, Math.Max(0, labelRight - content.Left), measured.SubHeight), noteColor,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                    }
                    else if (_presentation.SubLineLeftBar is null && AutoCapsule.SplitNotePercent(cacheNote) is var (shareLabel, share))
                    {
                        // "캐시히트 99.1%": label and bold value like "소모 4.72M" above it.
                        var shareLabelWidth = AutoCapsule.Width(shareLabel, measuredLabel);
                        TextRenderer.DrawText(graphics, shareLabel, measuredLabel,
                            new Rectangle(content.Left, lowTop + drop, shareLabelWidth + 1, measured.SubHeight), _palette.Label,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter);
                        var shareLeft = content.Left + shareLabelWidth + metrics.CompactMetricGap;
                        TextRenderer.DrawText(graphics, share, measuredValue,
                            new Rectangle(shareLeft, lowTop, Math.Max(0, lowRight - shareLeft), measured.SubHeight), noteColor,
                            AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    }
                    else if (_presentation.SubLineLeftBar is { } bar)
                        DrawCacheBar(graphics, cacheNote, bar, measuredLabel, new Rectangle(content.Left, lowTop + 1,
                            Math.Min(AutoCapsule.CacheBarWidth(measuredLabel, measured.SubHeight), Math.Max(0, lowRight - content.Left)),
                            Math.Max(1, measured.SubHeight - 2)));
                    else
                        TextRenderer.DrawText(graphics, cacheNote, measuredLabel,
                            new Rectangle(content.Left, lowTop, Math.Max(0, lowRight - content.Left), measured.SubHeight),
                            noteColor, AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
                content = new Rectangle(content.Left, top, content.Width, measured.MainHeight);
            }
            var x = content.Left;
            if (measured.Icon > 0)
            {
                if (AppIconCache.Get(_presentation.App, measured.Icon) is { } icon)
                {
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(icon, new Rectangle(
                        x, content.Top + (content.Height - measured.Icon) / 2, measured.Icon, measured.Icon));
                }
                x += measured.Icon + measured.Gap;
            }
            x += measured.Shift;
            if (measured.Dot > 0)
            {
                // A turn is running; the numbers are the previous completed turn.
                using var dotBrush = new SolidBrush(_palette.Accent);
                graphics.FillEllipse(dotBrush, x, content.Top + (content.Height - measured.Dot) / 2, measured.Dot, measured.Dot);
                x += measured.Dot + metrics.CompactMetricGap;
            }
            var labelDrop = AutoCapsule.BaselineShift(measuredLabel, measuredValue);
            void DrawMeasuredMetric(OverlayMetric metric, int width)
            {
                var (prefix, unit) = AutoCapsule.SplitLabel(metric);
                int left = x, right = x + width;
                if (prefix.Length > 0)
                {
                    var prefixWidth = AutoCapsule.Width(prefix, measuredLabel);
                    TextRenderer.DrawText(graphics, prefix, measuredLabel,
                        new Rectangle(left, content.Top + labelDrop, prefixWidth + 1, content.Height), _palette.Label,
                        AutoCapsule.Flags | TextFormatFlags.VerticalCenter);
                    left += prefixWidth + metrics.CompactMetricGap;
                }
                if (unit.Length > 0)
                {
                    var unitWidth = AutoCapsule.Width(unit, measuredLabel);
                    TextRenderer.DrawText(graphics, unit, measuredLabel,
                        new Rectangle(right - unitWidth - 1, content.Top + labelDrop, unitWidth + 1, content.Height), _palette.Label,
                        AutoCapsule.Flags | TextFormatFlags.VerticalCenter);
                    right -= unitWidth + 1 + metrics.CompactMetricGap;
                }
                TextRenderer.DrawText(graphics, metric.Value, measuredValue,
                    new Rectangle(left, content.Top, right - left, content.Height),
                    ValueColorFor(metric), AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                x += width + measured.ValueGap;
            }
            DrawMeasuredMetric(_presentation.Primary, measured.Primary);
            TextRenderer.DrawText(graphics, middle.Value, measuredValue,
                new Rectangle(x, content.Top, measured.Middle, content.Height), _palette.Value,
                AutoCapsule.Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            x += measured.Middle + measured.ValueGap;
            // The rate ends at the right edge, where the 모델 and 생성 lines end, even when they are wider than this row and
            // widened the capsule ("무조건 TPS 글자에 3개 나란히 정렬", the user, 2026-11-12).
            x = Math.Max(x, content.Right - measured.Secondary);
            DrawMeasuredMetric(_presentation.Secondary, measured.Secondary);
            return;
        }
        var dividerX = content.Left + (content.Width / 2);
        var dividerHeight = Math.Min(metrics.DividerHeight, content.Height);
        var dividerTop = content.Top + ((content.Height - dividerHeight) / 2);
        graphics.DrawLine(dividerPen, dividerX, dividerTop, dividerX, dividerTop + dividerHeight);

        var primaryBounds = Rectangle.FromLTRB(content.Left, content.Top, dividerX - gap, content.Bottom);
        var secondaryBounds = Rectangle.FromLTRB(dividerX + gap, content.Top, content.Right, content.Bottom);
        DrawCompactMetric(graphics, _presentation.Primary, primaryBounds, labelFont, valueFont, metrics);
        DrawCompactMetric(graphics, _presentation.Secondary, secondaryBounds, labelFont, valueFont, metrics);
        DrawEditHandle(graphics, dividerPen, metrics, decorations);
    }

    private void DrawEditHandle(
        Graphics graphics,
        Pen pen,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        if (!decorations.ShowResizeHandle || EditResizeHandleBounds.IsEmpty)
        {
            return;
        }

        var handle = EditResizeHandleBounds;
        var inset = metrics.StrokeWidth;
        var middle = Math.Max(inset, handle.Width / 2);
        graphics.DrawLine(
            pen,
            handle.Right - middle,
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - middle);
        graphics.DrawLine(
            pen,
            handle.Right - Math.Max(inset, handle.Width / 3),
            handle.Bottom - inset,
            handle.Right - inset,
            handle.Bottom - Math.Max(inset, handle.Height / 3));
    }

    // The Claude sessions whose cache is alive, one row each: name, time left and size (in the accent colour when it is
    // close), what the mod will do or did, and the 핑 button at the right edge (greyed where the session cannot be pinged).
    private void DrawSessions(Graphics graphics, IReadOnlyList<SessionRow> sessions, Font labelFont, Font valueFont, Pen dividerPen,
        Rectangle content, int top, int rowHeight)
    {
        void Cell(string text, Font font, Color color, int left, int width, int y) =>
            TextRenderer.DrawText(graphics, text, font, new Rectangle(left, y, width, rowHeight), color, TextFlags);
        if (sessions.Count == 0)
        {
            Cell(CachePings.NoneText, labelFont, _palette.Label, content.Left, content.Width, top);
            return;
        }
        var columns = MeasureSessions(sessions, labelFont, valueFont);
        var buttons = SessionButtons();
        for (var index = 0; index < sessions.Count; index++)
        {
            var row = sessions[index];
            var y = top + (index * rowHeight);
            if (index > 0) graphics.DrawLine(dividerPen, content.Left, y, content.Right, y);
            var x = content.Left;
            Cell(row.Name, valueFont, _palette.Value, x, columns.Name, y);
            x += columns.Name + columns.Gap;
            Cell(row.Left, valueFont, row.Soon ? _palette.Accent : _palette.Value, x, columns.Left, y);
            x += columns.Left + columns.Gap;
            Cell(row.State, labelFont, _palette.Label, x, columns.State, y);
            // 유지 is filled while on, 압축 while it waits for its second press; a button that cannot be pressed is drawn in
            // the divider colour.
            foreach (var (button, _, kind) in buttons.Where(b => b.Row == row))
            {
                var enabled = kind switch { SessionButton.Compact => row.CanCompact, SessionButton.Keep => row.CanKeep, _ => row.CanPing };
                var filled = kind == SessionButton.Keep && row.Keeping || kind == SessionButton.Compact && row.Arming;
                var color = enabled ? _palette.Accent : _palette.Divider;
                using var path = CreateRoundedRectanglePath(button, button.Height / 2);
                if (filled) { using var fill = new SolidBrush(color); graphics.FillPath(fill, path); }
                using var outline = new Pen(color, Math.Max(1f, rowHeight / 22f));
                graphics.DrawPath(outline, path);
                TextRenderer.DrawText(graphics, kind switch { SessionButton.Compact => "압축", SessionButton.Keep => "유지", _ => "핑" }, valueFont, button,
                    filled ? _palette.Background : enabled ? _palette.Accent : _palette.Label,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    private void DrawPanel(
        Graphics graphics,
        Font labelFont,
        Font valueFont,
        Font headerFont,
        Font highlightedValueFont,
        Brush backgroundBrush,
        Pen borderPen,
        Pen dividerPen,
        Brush progressTrackBrush,
        OverlayRenderMetrics metrics,
        OverlayRenderDecorationState decorations)
    {
        var layout = CurrentLayout!;
        var bounds = layout.PanelBounds.ToRectangle();
        using var path = CreateRoundedRectanglePath(bounds, metrics.PanelRadius);
        graphics.FillPath(backgroundBrush, path);
        if (decorations.ShowBorder)
        {
            graphics.DrawPath(borderPen, path);
        }

        var padding = metrics.PanelPadding;
        var content = Rectangle.Inflate(bounds, -padding, -padding);
        var headerHeight = metrics.HeaderHeight;
        _loginLine = Rectangle.Empty;
        if (ShowHistory && _presentation.HasHistoryPanel)
        {
            // No chat known yet: only the footer (the weekly line), under its own header.
            var sessions = _presentation.Sessions;
            var title = _presentation.History.Count > 0 ? _presentation.HistoryTitle
                : _presentation.GoTables is [var goTable, ..] ? goTable.Title
                : sessions is not null ? "Claude 캐시 세션" : "주간 사용량";
            TextRenderer.DrawText(graphics, HistoryPinned ? title + " · 고정됨" : title, headerFont,
                new Rectangle(content.Left, content.Top, content.Width, headerHeight), _palette.Value, TextFlags);
            var history = _presentation.History;
            var historyTop = bounds.Bottom - padding - (layout.ExpandedRowHeight * _presentation.HistoryPanelRows);
            // Columns: when | tokens | whole-turn seconds | whole-turn rate | model-request rate | cache read | model name
            // The fixed columns share what the name column leaves; their edges are parts of 95.
            // A column whose text would not fit its share grows by what it lacks, and the lines after it move right with it.
            var growth = HistoryColumnGrowth(_presentation, layout.Dpi, layout.ScalePercent);
            var fixedWidth = content.Width - HistoryNameWidth(_presentation, layout.Dpi, layout.ScalePercent) - growth.Sum();
            int Edge(int part) => content.Left + (fixedWidth * part / 95);
            void Cell(string text, Font font, Color color, int left, int right, int y, bool alignRight) =>
                TextRenderer.DrawText(graphics, text, font, new Rectangle(left, y, right - left, layout.ExpandedRowHeight), color,
                    alignRight ? TextFlags | TextFormatFlags.Right : TextFlags);
            // A table (user, 2026-11-11: "표 형식으로 칸 나눠놔야 구분감이 사는듯"): a vertical line between each two
            // columns, from the column header down to the last turn. Numbers keep a small gap from the line on their right;
            // the column header is centred in its cell (user, 2026-11-11, picked over headers right and a centred mix).
            var lines = new int[HistoryColumnParts.Length];
            for (int i = 0, part = 0, grown = 0; i < lines.Length; i++)
            {
                part += HistoryColumnParts[i];
                grown += growth[i];
                lines[i] = Edge(part) + grown;
            }
            var gap = Math.Max(4, layout.ExpandedRowHeight / 6);
            void Number(string text, Font font, Color color, int left, int right, int y, bool header) =>
                TextRenderer.DrawText(graphics, text, font, header
                        ? new Rectangle(left, y, right - left, layout.ExpandedRowHeight)
                        : new Rectangle(left + gap, y, right - left - (2 * gap), layout.ExpandedRowHeight), color,
                    TextFlags | (header ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Right));
            void Row(int index, string when, string tokens, string seconds, string rate, string model, string cache, string name, Font font, bool header)
            {
                var y = historyTop + (index * layout.ExpandedRowHeight);
                if (!header) graphics.DrawLine(dividerPen, content.Left, y, content.Right, y);
                Cell(when, labelFont, _palette.Label, Edge(0), lines[0] - gap, y, false);
                Number(tokens, font, header ? _palette.Label : _palette.Value, lines[0], lines[1], y, header);
                var secondsColor = header ? _palette.Label : _palette.Value;
                var split = header ? -1 : seconds.LastIndexOf(' ');
                if (split < 0) Number(seconds, font, secondsColor, lines[1], lines[2], y, header);
                else
                {
                    // "11분 38초" over "3분 1초": the seconds get a two-digit slot so 분 and 초 each line up.
                    var slot = AutoCapsule.Width(" 00초", font);
                    Number(seconds[(split + 1)..], font, secondsColor, lines[2] - slot - (2 * gap), lines[2], y, false);
                    Number(seconds[..split], font, secondsColor, lines[1], lines[2] - slot, y, false);
                }
                Number(rate, font, header ? _palette.Label : _palette.Accent, lines[2], lines[3], y, header);
                Number(model, font, header ? _palette.Label : _palette.Value, lines[3], lines[4], y, header);
                Number(cache, font, header ? _palette.Label : _palette.Value, lines[4], lines[5], y, header);
                // The name closes the row, left-aligned after the last line.
                Cell(name, labelFont, _palette.Label, lines[5] + gap, content.Right, y, false);
            }
            // One table: its column header, its rows, and the column lines from the header down to its last row.
            void Table(int top, IReadOnlyList<string> head, IReadOnlyList<HistoryRow> rows)
            {
                Row(top, head[0], head[1], head[2], head[3], head[4], head[5], head[6], labelFont, true);
                for (var index = 0; index < rows.Count; index++)
                {
                    var item = rows[index];
                    // A window whose limit is reached: the whole row on red (user, 2026-11-14: "막힘있으면 그 줄은
                    // 빨간배경으로 바뀌게 해놔").
                    if (item.Cache == BlockedState)
                    {
                        var light = _palette.Background.GetBrightness() > 0.5f;
                        using var red = new SolidBrush(light ? Color.FromArgb(70, LoginRedLight) : Color.FromArgb(120, 200, 40, 40));
                        graphics.FillRectangle(red, content.Left, historyTop + ((top + index + 1) * layout.ExpandedRowHeight),
                            content.Width, layout.ExpandedRowHeight);
                    }
                    Row(top + index + 1, item.When, item.Tokens, item.Seconds, item.Rate, item.Model, item.Cache, item.ModelName, valueFont, false);
                }
                foreach (var x in lines)
                    graphics.DrawLine(dividerPen, x, historyTop + (top * layout.ExpandedRowHeight),
                        x, historyTop + ((top + rows.Count + 1) * layout.ExpandedRowHeight));
            }
            // The line between sections, across the whole panel: thicker and stronger than the table's lines (user,
            // 2026-11-12: "섹션별 구분선좀 두껍고 진하게").
            using var sectionPen = new Pen(Color.FromArgb(230, _palette.Label), Math.Max(2f, metrics.StrokeWidth * 2.5f));
            void SectionLine(int y) => graphics.DrawLine(sectionPen, bounds.Left + 1, y, bounds.Right - 2, y);
            // A section under what is above it: a line across the panel and its title.
            void Section(int at, string text)
            {
                var y = historyTop + (at * layout.ExpandedRowHeight);
                SectionLine(y);
                TextRenderer.DrawText(graphics, text, headerFont,
                    new Rectangle(content.Left, y, content.Width, layout.ExpandedRowHeight), _palette.Value, TextFlags);
            }
            // OpenCode Go on top under the panel's title, then OpenAI under a title row of its own.
            // OpenAI on top under the panel's title, then OpenCode Go under a title row of its own when OpenAI is above it.
            if (history.Count > 0) Table(0, _presentation.HistoryHeader, history);
            // Each OpenCode Go account's table, the second account on top; each one under another under its own title.
            for (var index = 0; index < _presentation.GoTables.Count; index++)
            {
                var go = _presentation.GoTables[index];
                if (_presentation.GoTitle(index)) Section(_presentation.GoTop(index) - 1, go.Title);
                Table(_presentation.GoTop(index), go.Header ?? _presentation.HistoryHeader, go.Rows);
            }
            // Under the tables the sessions are their own section.
            if (_presentation.SessionTitle) Section(_presentation.TableRows, "Claude 캐시 세션");
            if (sessions is not null)
                DrawSessions(graphics, sessions, labelFont, valueFont, dividerPen, content,
                    historyTop + (_presentation.SessionsTopRow * layout.ExpandedRowHeight), layout.ExpandedRowHeight);
            var footerLines = _presentation.HistoryFooterLines;
            var row = _presentation.SessionsTopRow + _presentation.SessionRows;
            var usage = (Bar: Rectangle.Empty, Label: Rectangle.Empty);
            for (var index = 0; index < footerLines.Count; index++)
            {
                var y = historyTop + (row++ * layout.ExpandedRowHeight);
                // The footer under the tables is a section of its own; under the Claude sessions it keeps the thin line
                // (user, 2026-11-12: "클로드 캐시 세션쪽은 구분선 기존처럼").
                if (index == 0 && sessions is not null) graphics.DrawLine(dividerPen, content.Left, y, content.Right, y);
                else if (index == 0 && _presentation.HasTable) SectionLine(y);
                if (WeeklySplit.ParseLine(footerLines[index]) is { } week)
                {
                    // The week (usage and reset) is its own section: a line across the whole panel above it.
                    // Under the Claude sessions every line stays as it was (user, 2026-11-12: "클로드 캐시 세션 이라는
                    // 제목 밑에있는 모든 구분선은 기존처럼").
                    if (index > 0 && sessions is not null)
                    {
                        using var section = new Pen(Color.FromArgb(110, _palette.Label), Math.Max(1f, metrics.StrokeWidth));
                        graphics.DrawLine(section, bounds.Left + 1, y, bounds.Right - 2, y);
                    }
                    else if (index > 0) SectionLine(y);
                    usage = DrawWeeklyBar(graphics, week, labelFont, content.Left, content.Right, y, layout.ExpandedRowHeight,
                        footerLines.Any(WeeklySplit.IsStale),
                        // Claude's API credit only on Claude's week (user, 2026-11-12: "클로드에서 뜰때만 보여줘").
                        _presentation.App == HostApp.Claude ? _presentation.CreditLeft : null);
                    row++;
                    continue;
                }
                if (WeeklySplit.ParseReset(footerLines[index]) is { } reset)
                {
                    // The week's time line right under the usage bar, its text on the row after.
                    DrawWeekTime(graphics, reset, content.Left, content.Right, y, layout.ExpandedRowHeight, usage);
                    DrawResetLine(graphics, reset, labelFont, content.Left, content.Right, historyTop + (row++ * layout.ExpandedRowHeight),
                        layout.ExpandedRowHeight);
                    continue;
                }
                if (WeeklySplit.IsLogin(footerLines[index]))
                {
                    // What happened in grey, then the action in large bold red; the whole line opens the sign-in.
                    var said = footerLines[index][..^WeeklySplit.LoginAction.Length];
                    Cell(said, labelFont, _palette.Label, content.Left, content.Right, y, false);
                    using var action = LoginActionFont(labelFont);
                    var actionLeft = content.Left + AutoCapsule.Width(said, labelFont) + 2;
                    Cell(WeeklySplit.LoginAction, action, _palette.Background.GetBrightness() > 0.5f ? LoginRedLight : LoginRedDark,
                        actionLeft, content.Right, y, false);
                    _loginLine = new Rectangle(content.Left, y, Math.Min(content.Width, LoginLineWidth(footerLines[index], labelFont)),
                        layout.ExpandedRowHeight);
                    continue;
                }
                Cell(footerLines[index], labelFont, WeeklySplit.IsStale(footerLines[index]) ? _palette.Label : _palette.Value,
                    content.Left, content.Right, y, false);
            }
            return;
        }
        TextRenderer.DrawText(
            graphics,
            "완료 턴 통계",
            headerFont,
            new Rectangle(content.Left, content.Top, content.Width, headerHeight),
            _palette.Value,
            TextFlags);

        var highlightTop = content.Top + headerHeight + metrics.HighlightTopGap;
        var highlightHeight = metrics.HighlightHeight;
        var highlightGap = metrics.MetricGap;
        var highlightWidth = Math.Max(0, (content.Width - highlightGap) / 2);
        DrawHighlightedMetric(
            graphics,
            _presentation.Primary,
            new Rectangle(content.Left, highlightTop, highlightWidth, highlightHeight),
            labelFont,
            highlightedValueFont);
        DrawHighlightedMetric(
            graphics,
            _presentation.Secondary,
            new Rectangle(content.Left + highlightWidth + highlightGap, highlightTop, highlightWidth, highlightHeight),
            labelFont,
            highlightedValueFont);

        var rowsHeight = layout.ExpandedRowHeight > 0
            ? layout.ExpandedRowHeight * _presentation.ExpandedRows.Count
            : 0;
        var rowsTop = Math.Max(highlightTop + highlightHeight, bounds.Bottom - padding - rowsHeight);
        if (_presentation.ShowContextProgress)
        {
            var trackHeight = metrics.ProgressTrackHeight;
            var trackTop = Math.Min(
                rowsTop - trackHeight - metrics.ProgressVerticalGap,
                highlightTop + highlightHeight + metrics.ProgressVerticalGap);
            var trackBounds = new Rectangle(content.Left, trackTop, content.Width, trackHeight);
            graphics.FillRectangle(progressTrackBrush, trackBounds);
            var progressWidth = (int)Math.Round(
                trackBounds.Width * Math.Clamp(_presentation.ContextPercent, 0, 100) / 100d,
                MidpointRounding.AwayFromZero);
            if (progressWidth > 0)
            {
                var fillBounds = new Rectangle(trackBounds.X, trackBounds.Y, progressWidth, trackBounds.Height);
                using var fillBrush = new LinearGradientBrush(
                    fillBounds,
                    _palette.ProgressStart,
                    _palette.ProgressEnd,
                    LinearGradientMode.Horizontal);
                graphics.FillRectangle(fillBrush, fillBounds);
            }
        }

        for (var index = 0; index < _presentation.ExpandedRows.Count; index++)
        {
            var row = _presentation.ExpandedRows[index];
            var rowBounds = new Rectangle(
                content.Left,
                rowsTop + (index * layout.ExpandedRowHeight),
                content.Width,
                layout.ExpandedRowHeight);
            graphics.DrawLine(dividerPen, rowBounds.Left, rowBounds.Top, rowBounds.Right, rowBounds.Top);
            DrawExpandedRow(graphics, row, rowBounds, labelFont, valueFont);
        }
    }

    // Capsule cache bar: a track, the coloured part for the time left, and the text centred on both,
    // white over the colour and the theme's value colour over the empty track.
    // The weekly line as a bar: the shares and the account's total on the row, the bar across the panel on the next
    // (its right end is the weekly limit). This PC's share in the accent colour, the user's other devices lighter,
    // other people in amber, elsewhere grey, the rest left as track.
    private static readonly Color PersonColor = Color.FromArgb(214, 140, 48);

    // Stale: the percent is an old one (the usage API keeps failing), so the bar and its dots are one grey.
    // credit: the Anthropic Console's dollars left, "API: $187.38" in front of the devices like "계정 97%" (user,
    // 2026-11-12: "PC 옆으로 붙여서 ... API: (금액) 이렇게"), left out when it would run into "이번주 ...".
    private (Rectangle Bar, Rectangle Label) DrawWeeklyBar(Graphics graphics, WeeklySplit.Bar week, Font font, int left, int right, int y, int rowHeight,
        bool stale = false, string? credit = null)
    {
        void Text(string text, Font textFont, Color color, int textLeft, int textRight) =>
            TextRenderer.DrawText(graphics, text, textFont, new Rectangle(textLeft, y, Math.Max(0, textRight - textLeft), rowHeight), color, TextFlags);
        int Width(string text, Font textFont) => AutoCapsule.Width(text, textFont) + 2;
        // By place, as the names are the user's own: this PC first, other devices next (each one lighter than the one
        // before it, two shades taking turns), other people (amber, a second one lighter) after them, elsewhere last.
        var firstPerson = week.Parts.Count - 1 - week.People;
        Color ColorOf(int index) => stale ? _palette.Label
            : index == week.Parts.Count - 1 ? Color.FromArgb(150, _palette.Label)
            : index >= firstPerson ? Color.FromArgb((index - firstPerson) % 2 == 0 ? 255 : 150, PersonColor)
            : index == 0 ? _palette.Accent : Color.FromArgb((index - 1) % 2 == 0 ? 160 : 80, _palette.Accent);
        // Another account's week leads with its name, in bold, so it is not taken for the user's own.
        var head = left;
        if (week.Who is { } who)
        {
            using var named = new Font(font, FontStyle.Bold);
            var label = who + " · ";
            Text(label, named, _palette.Accent, head, right);
            head += Width(label, named);
        }
        Text("이번주 " + week.Since, font, _palette.Value, head, right);
        head += Width("이번주 " + week.Since, font);
        using var bold = new Font(font, FontStyle.Bold);
        var x = right;
        var total = $"{week.Account}%";
        Text(total, bold, _palette.Value, x - Width(total, bold), x);
        x -= Width(total, bold);
        Text("계정 ", font, _palette.Label, x - Width("계정 ", font), x);
        x -= Width("계정 ", font);
        var dot = WeeklyDot(font);
        for (var index = week.Parts.Count - 1; index >= 0; index--)
        {
            var (name, percent) = week.Parts[index];
            x -= WeeklyGap(font);
            var share = $"{name} {percent}%";
            Text(share, font, _palette.Value, x - Width(share, font), x);
            x -= Width(share, font) + dot / 2;
            using var dotBrush = new SolidBrush(ColorOf(index));
            graphics.FillEllipse(dotBrush, x - dot, y + (rowHeight - dot) / 2, dot, dot);
            x -= dot;
        }
        if (credit is not null)
        {
            var amount = Width(credit, bold);
            var label = Width("API: ", font);
            var end = x - WeeklyGap(font);
            if (end - amount - label >= head + WeeklyGap(font))
            {
                Text(credit, bold, _palette.Value, end - amount, end);
                Text("API: ", font, _palette.Label, end - amount - label, end - amount);
            }
        }

        // Thick enough for the account's share written in it.
        var barHeight = Math.Max(6, rowHeight * 7 / 10);
        var room = (rowHeight - barHeight) / 2;
        var bar = new Rectangle(left, y + rowHeight + room - Math.Min(rowHeight / 6, room), right - left, barHeight);
        var corner = new Size(barHeight, barHeight);
        using (var track = new SolidBrush(_palette.ProgressTrack)) graphics.FillRoundedRectangle(track, bar, corner);
        var state = graphics.Save();
        using (var clip = new System.Drawing.Drawing2D.GraphicsPath())
        {
            clip.AddRoundedRectangle(bar, corner);
            graphics.SetClip(clip);
            float from = bar.Left;
            for (var index = 0; index < week.Parts.Count; index++)
            {
                var width = bar.Width * Math.Clamp(week.Parts[index].Percent, 0, 100) / 100f;
                using var fill = new SolidBrush(ColorOf(index));
                graphics.FillRectangle(fill, from, bar.Top, width, bar.Height);
                from += width;
            }
        }
        graphics.Restore(state);
        // The account's share written in the bar: at the end of the used part when it fits there, else just after it.
        var used = left + (int)(bar.Width * Math.Clamp(week.Account, 0, 100) / 100f);
        return (bar, DrawUsageShare(graphics, $"{week.Account}", font, bar, used));
    }

    // The share in gauge-like figures (Bahnschrift, part of Windows), in the bar's opposite colour inside the used part,
    // in the text colour on the track after it. Returns where it was written.
    private Rectangle DrawUsageShare(Graphics graphics, string number, Font font, Rectangle bar, int used)
    {
        using var figures = new Font("Bahnschrift SemiBold", font.Size * 1.15f, FontStyle.Regular, font.Unit);
        var text = number + "%";
        var width = AutoCapsule.Width(text, figures) + 2;
        var inside = used - bar.Left >= width + bar.Height / 2;
        var x = inside ? used - width - bar.Height / 4 : used + bar.Height / 4;
        TextRenderer.DrawText(graphics, text, figures, new Rectangle(x, bar.Top, width, bar.Height),
            inside ? _palette.Background : _palette.Value, TextFlags | TextFormatFlags.VerticalCenter);
        return new Rectangle(x, bar.Top, width, bar.Height);
    }

    // The reset line's text: when and how long is left at the left, "경과 34%" at the right like "계정 10%" above.
    private void DrawResetLine(Graphics graphics, WeeklySplit.Reset reset, Font font, int left, int right, int y, int rowHeight)
    {
        void Text(string text, Font textFont, Color color, int textLeft, int textRight) =>
            TextRenderer.DrawText(graphics, text, textFont, new Rectangle(textLeft, y, Math.Max(0, textRight - textLeft), rowHeight), color, TextFlags);
        int Width(string text, Font textFont) => AutoCapsule.Width(text, textFont) + 2;
        using var bold = new Font(font, FontStyle.Bold);
        var share = $"{Math.Round(reset.Elapsed * 100):0}%";
        var x = right - Width(share, bold);
        Text(share, bold, _palette.Value, x, right);
        Text("경과 ", font, _palette.Label, x - Width("경과 ", font), x);
        Text($"{reset.When} · {reset.Left}{reset.Extra}", font, _palette.Value, left, x - Width("경과 ", font));
    }

    // The week's time as a line of its own, unlike the usage bar above it: a thin track with a tick at each day, the
    // part gone thicker, and a clock where the week stands now, with a dotted guide from it up through the usage bar.
    private void DrawWeekTime(Graphics graphics, WeeklySplit.Reset reset, int left, int right, int y, int rowHeight, (Rectangle Bar, Rectangle Label) usage)
    {
        var height = Math.Max(6, rowHeight * 2 / 5);
        var mid = y + (rowHeight - height) / 2 - rowHeight / 6 + height / 2f;
        var past = Color.FromArgb(150, _palette.Label);
        var now = left + (int)Math.Round((right - left) * reset.Elapsed);
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var thin = new Pen(_palette.ProgressTrack, Math.Max(2f, height / 3f));
        using var thick = new Pen(past, Math.Max(2f, height / 3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var tick = new Pen(past, 1f);
        graphics.DrawLine(thin, left, mid, right, mid);
        for (var day = 0; day <= 7; day++)
        {
            var x = left + (right - left) * day / 7f;
            graphics.DrawLine(tick, x, mid - height * 0.6f, x, mid + height * 0.6f);
        }
        graphics.DrawLine(thick, left, mid, now, mid);
        if (!usage.Bar.IsEmpty)
        {
            // The guide breaks around the share written in the bar.
            var clip = graphics.Save();
            graphics.ExcludeClip(Rectangle.Inflate(usage.Label, 3, 0));
            using var guide = new Pen(_palette.Value, 1f) { DashStyle = DashStyle.Dot };
            graphics.DrawLine(guide, now, usage.Bar.Top - 2, now, mid);
            graphics.Restore(clip);
        }
        DrawClock(graphics, new PointF(now, mid), rowHeight * 0.78f);
        graphics.SmoothingMode = smoothing;
    }

    // A clock with its hands at 10:10: outlined on the light theme, filled with the hands cut out on the dark one.
    private void DrawClock(Graphics graphics, PointF centre, float size)
    {
        var dark = _palette == OverlayThemePalette.For(OverlayThemeKind.Dark);
        var line = Math.Max(1.5f, size / 12f);
        var r = size / 2f;
        RectangleF Disc(float radius) => new(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius);
        PointF At(double degrees, float length) => new(centre.X + length * (float)Math.Sin(degrees * Math.PI / 180),
            centre.Y - length * (float)Math.Cos(degrees * Math.PI / 180));
        using (var face = new SolidBrush(dark ? _palette.Value : _palette.Background)) graphics.FillEllipse(face, Disc(r));
        if (!dark)
        {
            using var rim = new Pen(_palette.Value, line);
            graphics.DrawEllipse(rim, Disc(r - line / 2));
        }
        var hands = dark ? _palette.Background : _palette.Value;
        using var hour = new Pen(hands, line * 1.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var minute = new Pen(hands, line * 0.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(hour, centre, At(10 * 30 + 5, r * 0.45f));
        graphics.DrawLine(minute, centre, At(60, r * 0.68f));
        using var pin = new SolidBrush(hands);
        graphics.FillEllipse(pin, Disc(line * 0.8f));
    }

    // The ring's lines in their part of the module: centred both ways.
    private void DrawRingNote(Graphics graphics, string note, Rectangle box, OverlayLayoutResult layout)
    {
        using var noteFont = AutoCapsule.LabelFont(layout.Dpi, layout.ScalePercent);
        var lines = note.Split('\n');
        var lineHeight = TextRenderer.MeasureText("가Ag", noteFont, Size.Empty, AutoCapsule.Flags).Height;
        var top = box.Top + (box.Height - lines.Length * lineHeight) / 2;
        for (var i = 0; i < lines.Length; i++)
            TextRenderer.DrawText(graphics, lines[i], noteFont, new Rectangle(box.Left, top + i * lineHeight, box.Width, lineHeight),
                i == 0 ? _palette.Label : _palette.Value, AutoCapsule.Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    // Plain block behind the ring: rounded top corners like the capsule's, straight sides down to the given y.
    // The right side may run further down, over the capsule's rounded corner, to make one straight edge.
    internal static GraphicsPath RingModulePath(RectangleF ring, float bottom, float radius, bool closed = true, float? rightBottom = null)
    {
        var r = Math.Max(1f, Math.Min(radius, ring.Width / 2));
        var path = new GraphicsPath();
        path.AddLine(ring.Left, bottom, ring.Left, ring.Top + r);
        path.AddArc(ring.Left, ring.Top, 2 * r, 2 * r, 180, 90);
        path.AddArc(ring.Right - 2 * r, ring.Top, 2 * r, 2 * r, 270, 90);
        path.AddLine(ring.Right, ring.Top + r, ring.Right, rightBottom ?? bottom);
        if (closed) path.CloseFigure();
        return path;
    }

    // A meter, drawn like Task Manager's tray icon: a box with a shaded edge (one part of 16 of its height) above and at
    // the left and a lit one below and at the right, its inside darkened and filled from the bottom in the meter's
    // colour (CPU in Task Manager's blue, memory purple, page file green). The fill moves in whole percents; the row
    // where it ends is drawn partly covered, so each percent shows even when the box is less than 100 pixels tall. The
    // share sits in the middle of the bar, a number without "%", white over a dark shadow so it reads on any fill.
    internal static readonly Color CpuBlue = Color.FromArgb(108, 179, 226);
    internal static readonly Color RamPurple = Color.FromArgb(178, 120, 218);
    internal static readonly Color PageGreen = Color.FromArgb(112, 190, 96);

    // `foot`: the number at the bar's foot (the whole percent when not given; the graphics card's temperature for the
    // third bar, 2026-11-10). `top`: a second number at its top, or none. A number wider than the bar is drawn in a smaller type to fit.
    private static void DrawMeter(Graphics graphics, IntRect bar, double? fill, Color color, OverlayLayoutResult layout,
        string? foot = null, string? top = null)
    {
        if (bar.IsEmpty) return;
        var box = bar.ToRectangle();
        DrawMeterBar(graphics, box, fill, color);
        var text = foot ?? (CpuPercent(fill) is int percent ? $"{percent}" : "—");
        // The number at the bar's foot, centred across it, inside the edges.
        var edge = Math.Max(1, (int)Math.Round(box.Height / 16.0));
        var inside = Rectangle.FromLTRB(box.Left + edge, box.Top + edge, box.Right - edge, box.Bottom - edge - Math.Max(1, edge / 2));
        // Chosen over six other ways (outlines, bolder, theme-coloured, a dark plate) on 2026-11-07.
        using var font = AutoCapsule.MeterFont(layout.Dpi, layout.ScalePercent);
        var shadow = Math.Max(1, box.Height / 40);
        void Draw(string value, TextFormatFlags place)
        {
            var flags = AutoCapsule.Flags | TextFormatFlags.HorizontalCenter | place;
            var width = TextRenderer.MeasureText(graphics, value, font, Size.Empty, AutoCapsule.Flags).Width;
            using var fitted = width > inside.Width && inside.Width > 0
                ? new Font(font.FontFamily, font.Size * inside.Width / width, font.Style, font.Unit) : null;
            var type = fitted ?? font;
            TextRenderer.DrawText(graphics, value, type, inside with { X = inside.X + shadow, Y = inside.Y + shadow }, Color.FromArgb(20, 20, 20), flags);
            TextRenderer.DrawText(graphics, value, type, inside, Color.White, flags);
        }
        Draw(text, TextFormatFlags.Bottom);
        if (!string.IsNullOrEmpty(top)) Draw(top, TextFormatFlags.Top);
    }

    // The CPU line: small text in the label colour from the meters' left edge, cut with "…" when too long, over a
    // divider like the one between the meters' row and the capsule.
    private void DrawTopLine(Graphics graphics, OverlayLayoutResult layout, OverlayRenderMetrics metrics)
    {
        var area = layout.TopLineBounds.ToRectangle();
        // A margin and a half above the text, as much under it before the divider (see AutoCapsule.TopLineHeight).
        var margin = Math.Max(2, AutoCapsule.RingSize(layout.Dpi, layout.ScalePercent).Margin);
        var left = layout.CpuBarBounds.IsEmpty ? area.Left + metrics.CapsuleRadius : layout.CpuBarBounds.Left;
        var text = Rectangle.FromLTRB(left, area.Top + margin * 3 / 2, area.Right - metrics.CapsuleRadius, area.Bottom - margin * 3 / 2);
        using var font = AutoCapsule.TopLineFont(layout.Dpi, layout.ScalePercent);
        var line = _topLine ?? "CPU 재는 중…";
        // A program shown by its logo (Python, the user's ask on 2026-11-10): its file's icon, as tall as the text, in
        // place of its name. Without the icon the name stays.
        var space = line.IndexOf(' ');
        if (_topLogo is { } logo && space > 0 && CpuTop.HasLogo(line[..space]) && TopLogo(logo, font.Height) is { } image)
        {
            graphics.DrawImageUnscaled(image, text.Left, text.Top + (text.Height - image.Height) / 2);
            text = Rectangle.FromLTRB(text.Left + image.Width + Math.Max(2, margin), text.Top, text.Right, text.Bottom);
            line = line[(space + 1)..];
        }
        TextRenderer.DrawText(graphics, line, font, text, _palette.Label,
            AutoCapsule.Flags | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var divider = new Pen(Color.FromArgb(110, _palette.Label), Math.Max(1f, metrics.StrokeWidth));
        graphics.DrawLine(divider, area.Left + metrics.CapsuleRadius, area.Bottom, area.Right - metrics.CapsuleRadius, area.Bottom);
    }

    // The logo from the program file's own icon at this size, read once per file and size; null when the file gives none.
    private Bitmap? TopLogo(string path, int size)
    {
        if (_topLogoImage is { } kept && kept.Path == path && kept.Size == size) return kept.Image;
        _topLogoImage?.Image?.Dispose();
        Bitmap? image = null;
        try
        {
            using var icon = Icon.ExtractIcon(path, 0, 256);
            if (icon is not null) { using var large = icon.ToBitmap(); image = LogoOnly(large, size); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        _topLogoImage = (path, size, image);
        return image;
    }

    // python.exe's icon puts the logo over a console window (pythonw.exe's over a plain one); the user wants the logo
    // alone (2026-11-10). Its two snakes are the icon's only blue and yellow below the window's title bar: those pixels,
    // cut to their bounds and scaled to the size. The whole icon when none are found.
    internal static Bitmap LogoOnly(Bitmap icon, int size)
    {
        var logo = new Bitmap(icon.Width, icon.Height);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = icon.Height * 3 / 10; y < icon.Height; y++)
            for (var x = 0; x < icon.Width; x++)
            {
                var c = icon.GetPixel(x, y);
                var hue = c.GetHue();
                if (c.A < 40 || c.GetSaturation() <= 0.35f || !(hue is >= 195 and <= 230 or >= 38 and <= 58)) continue;
                logo.SetPixel(x, y, c);
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        var source = right < 0 ? icon : logo;
        var bounds = right < 0 ? new Rectangle(0, 0, icon.Width, icon.Height) : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        var side = Math.Max(bounds.Width, bounds.Height);
        var scaled = new Bitmap(size, size);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var w = size * bounds.Width / side;
            var h = size * bounds.Height / side;
            g.DrawImage(source, new Rectangle((size - w) / 2, (size - h) / 2, w, h), bounds, GraphicsUnit.Pixel);
        }
        logo.Dispose();
        return scaled;
    }

    private static void DrawMeterBar(Graphics graphics, Rectangle bar, double? fill, Color color)
    {
        if (bar.Width < 4 || bar.Height < 4) return;
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.None;
        var edge = Math.Max(1, (int)Math.Round(bar.Height / 16.0));
        var inside = Rectangle.FromLTRB(bar.Left + edge, bar.Top + edge, bar.Right - edge, bar.Bottom - edge);
        using (var shade = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
        {
            graphics.FillRectangle(shade, bar.Left, bar.Top, bar.Width - edge, edge);
            graphics.FillRectangle(shade, bar.Left, bar.Top + edge, edge, bar.Height - edge);
        }
        using (var lit = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
        {
            graphics.FillRectangle(lit, bar.Right - edge, bar.Top, edge, bar.Height);
            graphics.FillRectangle(lit, bar.Left + edge, bar.Bottom - edge, bar.Width - 2 * edge, edge);
        }
        using (var empty = new SolidBrush(Color.FromArgb(70, 0, 0, 0))) graphics.FillRectangle(empty, inside);
        if (CpuPercent(fill) is int percent && percent > 0)
        {
            // Full rows, then the row where the fill ends, covered by the part of it the percent reaches.
            var height = inside.Height * percent / 100.0;
            var rows = (int)Math.Floor(height);
            using var blue = new SolidBrush(color);
            graphics.FillRectangle(blue, inside.Left, inside.Bottom - rows, inside.Width, rows);
            var part = height - rows;
            if (part > 0.01 && rows < inside.Height)
                using (var top = new SolidBrush(Color.FromArgb((int)Math.Round(255 * part), color)))
                    graphics.FillRectangle(top, inside.Left, inside.Bottom - rows - 1, inside.Width, 1);
        }
        graphics.SmoothingMode = smoothing;
    }

    // Context ring: track, an arc clockwise from the top for the share in use, the percentage inside.
    private void DrawContextRing(Graphics graphics, double fill, Rectangle box, OverlayLayoutResult layout)
    {
        var stroke = Math.Max(2f, box.Width / 9f);
        var arc = RectangleF.Inflate(box, -stroke / 2 - 0.5f, -stroke / 2 - 0.5f);
        var smoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var track = new Pen(_palette.ProgressTrack, stroke)) graphics.DrawEllipse(track, arc);
        var sweep = (float)(360 * Math.Clamp(fill, 0, 1));
        if (sweep > 0)
            using (var used = new Pen(OverlayThemePalette.CacheBarColor(ContextUsage.Level(fill)), stroke))
                graphics.DrawArc(used, arc, -90, sweep);
        graphics.SmoothingMode = smoothing;
        using var font = AutoCapsule.RingFont(layout.Dpi, layout.ScalePercent);
        TextRenderer.DrawText(graphics, $"{Math.Round(fill * 100):0}%", font, box, _palette.Value,
            AutoCapsule.Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawCacheBar(Graphics graphics, string text, CacheBar bar, Font font, Rectangle box)
    {
        if (box.Width <= 0) return;
        var corner = new Size(box.Height, box.Height);
        using (var track = new SolidBrush(_palette.ProgressTrack)) graphics.FillRoundedRectangle(track, box, corner);
        var filled = new Rectangle(box.Left, box.Top, (int)Math.Round(box.Width * bar.Fill), box.Height);
        var empty = Rectangle.FromLTRB(filled.Right, box.Top, box.Right, box.Bottom);
        const TextFormatFlags flags = AutoCapsule.Flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.PreserveGraphicsClipping;
        var saved = graphics.Save();
        if (filled.Width > 0)
        {
            graphics.IntersectClip(filled);
            using (var fill = new SolidBrush(OverlayThemePalette.CacheBarColor(bar.Level))) graphics.FillRoundedRectangle(fill, box, corner);
            TextRenderer.DrawText(graphics, text, font, box, Color.White, flags);
            graphics.Restore(saved);
            saved = graphics.Save();
        }
        if (empty.Width > 0)
        {
            graphics.IntersectClip(empty);
            TextRenderer.DrawText(graphics, text, font, box, _palette.Value, flags);
        }
        graphics.Restore(saved);
    }

    private void DrawCompactMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont,
        OverlayRenderMetrics metrics)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var labelWidth = TextRenderer.MeasureText(
            graphics,
            metric.CompactLabel,
            labelFont,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        var gap = metrics.CompactMetricGap;
        labelWidth = Math.Min(labelWidth, bounds.Width);
        var labelBounds = new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height);
        var valueBounds = Rectangle.FromLTRB(
            Math.Min(bounds.Right, labelBounds.Right + gap),
            bounds.Top,
            bounds.Right,
            bounds.Bottom);
        TextRenderer.DrawText(graphics, metric.CompactLabel, labelFont, labelBounds, _palette.Label, TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            valueBounds,
            ValueColorFor(metric),
            TextFlags | TextFormatFlags.Right);
    }

    private void DrawHighlightedMetric(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        var labelHeight = Math.Max(1, bounds.Height / 2);
        TextRenderer.DrawText(
            graphics,
            metric.ExpandedLabel,
            labelFont,
            new Rectangle(bounds.Left, bounds.Top, bounds.Width, labelHeight),
            _palette.Label,
            TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Left, bounds.Top + labelHeight, bounds.Width, bounds.Height - labelHeight),
            ValueColorFor(metric),
            TextFlags);
    }

    private void DrawExpandedRow(
        Graphics graphics,
        OverlayMetric metric,
        Rectangle bounds,
        Font labelFont,
        Font valueFont)
    {
        if (string.IsNullOrEmpty(metric.ExpandedLabel))
        {
            TextRenderer.DrawText(graphics, metric.Value, labelFont, bounds,
                _palette.Label, TextFlags | TextFormatFlags.HorizontalCenter);
            return;
        }
        var labelWidth = metric.ExpandedLabel == "채팅" ? bounds.Width / 7 : Math.Max(0, bounds.Width / 2);
        TextRenderer.DrawText(
            graphics,
            metric.ExpandedLabel,
            labelFont,
            new Rectangle(bounds.Left, bounds.Top, labelWidth, bounds.Height),
            _palette.Label,
            TextFlags);
        TextRenderer.DrawText(
            graphics,
            metric.Value,
            valueFont,
            new Rectangle(bounds.Left + labelWidth, bounds.Top, bounds.Width - labelWidth, bounds.Height),
            ValueColorFor(metric),
            TextFlags | TextFormatFlags.Right);
    }

    private Color ValueColorFor(OverlayMetric metric) =>
        metric.Field is DisplayField.Context or DisplayField.ContextPercent
            ? _palette.Accent
            : _palette.Value;

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return path;
        }

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
}
