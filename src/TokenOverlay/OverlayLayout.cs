using System.Drawing;
using System.Text.Json.Serialization;

namespace TokenOverlay;

internal readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Left => X;
    [JsonIgnore] public int Top => Y;
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    [JsonIgnore] public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static IntRect FromRectangle(Rectangle value) =>
        new(value.X, value.Y, value.Width, value.Height);
}

internal readonly record struct WindowChromeMetrics(
    int CaptionButtonWidth,
    int CaptionButtonHeight,
    int FrameWidth,
    int FrameHeight,
    int PaddedBorderWidth);

internal sealed record CodexWindowInfo(
    IntPtr Handle,
    IntRect WindowBounds,
    IntRect ExtendedFrameBounds,
    IntRect? CaptionButtonBounds,
    IntRect WorkingArea,
    uint Dpi,
    WindowChromeMetrics ChromeMetrics);

internal enum OverlayVisualState { Collapsed, Expanded, HiddenForSpace }
internal enum CollapsedDisplayMode { TwoFields, PrimaryOnly }
internal enum ExpansionDirection { Down, Up }

internal sealed record OverlayLayoutRequest(
    CodexWindowInfo HostWindow,
    AnchorMode AnchorMode,
    bool RequestExpanded,
    int ExpandedRowCount,
    bool ShowContextProgress,
    Point? ManualCapsuleCenter = null,
    int ScalePercent = ManualAttachmentRules.DefaultScalePercent,
    OverlayPresentation? Presentation = null,
    bool CompactPanel = false);

internal sealed record OverlayLayoutResult(
    OverlayVisualState State,
    CollapsedDisplayMode CollapsedDisplay,
    ExpansionDirection ExpansionDirection,
    uint Dpi,
    IntRect WindowBounds,
    IntRect CapsuleBounds,
    IntRect PanelBounds,
    int ExpandedRowHeight,
    int ScalePercent = ManualAttachmentRules.DefaultScalePercent)
{
    // Context ring module on top of the collapsed capsule (client coordinates), one block with round top corners:
    // the ring at its right and the ring's lines left of it. All empty when there is no ring.
    public IntRect RingModuleBounds { get; init; }
    // The ring inside the module.
    public IntRect RingBounds { get; init; }
    // The ring's lines inside the module, as tall as the ring; empty when the capsule has no room for them.
    public IntRect RingNoteBounds { get; init; }
    // The meters at the module's left end: CPU, memory and page file side by side, each a bar drawn like Task Manager's
    // tray icon with its share inside. With them the module runs the capsule's full width, so the overlay is one
    // rectangle. Empty without; the page file goes first, then memory, when the row is too narrow.
    public IntRect CpuBarBounds { get; init; }
    public IntRect RamBarBounds { get; init; }
    public IntRect PageBarBounds { get; init; }
    public bool FullTopRow => !CpuBarBounds.IsEmpty;

    public bool ContainsClientPoint(Point point) =>
        CapsuleBounds.Contains(point.X, point.Y) || PanelBounds.Contains(point.X, point.Y);

    public bool ContainsScreenPoint(Point point) => ContainsClientPoint(new Point(
        point.X - WindowBounds.X,
        point.Y - WindowBounds.Y));
}

internal static class OverlayLayoutCalculator
{
    private const int CollapsedWidthDip = 360;
    private const int TitleBarCollapsedWidthDip = 240;
    private const int PrimaryOnlyWidthDip = 116;
    private const int CollapsedHeightDip = 34;
    private const int ExpandedWidthDip = 360;
    // The recent-turn list: six fixed columns plus a model-name column as wide as the longest name shown.
    internal const int HistoryFixedWidthDip = 406;
    // A list without turns is only as wide as its footer.
    // The session list is as wide as its rows (or its footer).
    private static int PanelWidth(OverlayLayoutRequest request, uint dpi, int scalePercent) => request.CompactPanel
        ? request.Presentation?.Sessions is not null ? Math.Max(TokenStripForm.SessionListWidth(request.Presentation, dpi, scalePercent),
            TokenStripForm.HistoryFooterWidth(request.Presentation, dpi, scalePercent))
        : request.Presentation?.History.Count is null or 0 ? TokenStripForm.HistoryFooterWidth(request.Presentation, dpi, scalePercent)
        : Math.Max(ScaleOverlayDip(HistoryFixedWidthDip, dpi, scalePercent) + TokenStripForm.HistoryNameWidth(request.Presentation, dpi, scalePercent),
            TokenStripForm.HistoryFooterWidth(request.Presentation, dpi, scalePercent))
        : ScaleOverlayDip(ExpandedWidthDip, dpi, scalePercent);
    private const int CapsulePanelGapDip = 6;
    private const int CaptionSafetyGapDip = 8;
    private const int TitleLeftReserveDip = 160;
    private const int PanelChromeHeightDip = 122;
    private const int CompactPanelChromeHeightDip = 54;
    private const int NormalRowHeightDip = 30;
    private const int MinimumRowHeightDip = 24;

    private const int LegacyOutsideGapDip = 10;
    private const int LegacyInsideMarginDip = 18;
    private const int LegacyHeaderOffsetDip = 56;
    private const int LegacyBottomOffsetDip = 70;

    public static OverlayLayoutResult Calculate(OverlayLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.HostWindow);

        if (request.Presentation?.CompactMiddle is not null)
        {
            // Preserve all three statistics. Reduce scale only when the measured capsule will not fit.
            for (var scale = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
                scale >= ManualAttachmentRules.MinimumScalePercent; scale--)
            {
                var result = CalculateCore(request with { ScalePercent = scale });
                if (result.State != OverlayVisualState.HiddenForSpace && result.CollapsedDisplay == CollapsedDisplayMode.TwoFields)
                    return WithRing(result, request.Presentation);
            }
            return Hidden(request.HostWindow.Dpi, DirectionFor(request.AnchorMode), ManualAttachmentRules.MinimumScalePercent);
        }
        return CalculateCore(request);
    }

    // Context ring: a module standing on the collapsed capsule above its right end ("TPS"), where the host
    // window is mostly empty. The capsule keeps its place and width; the window grows upward by what sticks out.
    private static OverlayLayoutResult WithRing(OverlayLayoutResult result, OverlayPresentation presentation)
    {
        if (!HasTopRow(presentation) || result.State != OverlayVisualState.Collapsed || result.CapsuleBounds.IsEmpty) return result;
        var metrics = OverlayRenderMetrics.Create(result.Dpi, result.ScalePercent);
        var (diameter, margin) = AutoCapsule.RingSize(result.Dpi, result.ScalePercent);
        // A small gap between the ring and the row under it.
        var rise = diameter + Math.Max(2, metrics.MetricGap / 2);
        var capsule = result.CapsuleBounds;
        var ring = presentation.ContextFill is null ? 0 : diameter;
        // The ring's lines left of the ring, when the capsule is wide enough for both.
        var note = ring == 0 ? 0 : AutoCapsule.RingNoteWidth(result.Dpi, result.ScalePercent, presentation.RingNote);
        if (note + ring > capsule.Width) note = 0;
        // The meters at the left end (CPU, memory, page file); the last goes first when the row is too narrow.
        var bars = presentation.CpuMeter
            ? AutoCapsule.MeterBars(diameter, margin, metrics.CapsuleRadius, result.Dpi, result.ScalePercent, capsule.Width - note - ring) : [];
        var shown = bars.Length;
        while (shown > 0 && bars[shown - 1].Right + 2 * margin + note + ring > capsule.Width) shown--;
        if (shown == 0 && ring == 0) return result;
        IntRect Bar(int i) => i < shown ? new(capsule.X + bars[i].X, capsule.Y + bars[i].Y, bars[i].Width, bars[i].Height) : default;
        var window = result.WindowBounds;
        return result with
        {
            WindowBounds = new IntRect(window.X, window.Y - rise, window.Width, window.Height + rise),
            CapsuleBounds = capsule with { Y = capsule.Y + rise },
            // Flush with the capsule's right edge, so the two read as one right-aligned column; with the meter the
            // module is as wide as the capsule.
            RingModuleBounds = shown > 0 ? new IntRect(capsule.X, capsule.Y, capsule.Width, diameter)
                : new IntRect(capsule.Right - diameter - note, capsule.Y, note + diameter, diameter),
            RingBounds = ring == 0 ? default : new IntRect(capsule.Right - diameter, capsule.Y, diameter, diameter),
            RingNoteBounds = note == 0 ? default : new IntRect(capsule.Right - diameter - note, capsule.Y, note, diameter),
            CpuBarBounds = Bar(0), RamBarBounds = Bar(1), PageBarBounds = Bar(2),
        };
    }

    // A row above the capsule: the context ring, or the CPU meter (alone when the ring is off or not known yet).
    private static bool HasTopRow(OverlayPresentation p) =>
        p.CompactMiddle is not null && string.IsNullOrWhiteSpace(p.StatusText) && (p.ContextFill is not null || p.CpuMeter);

    private static OverlayLayoutResult CalculateCore(OverlayLayoutRequest request)
    {

        if (request.ManualCapsuleCenter is not null)
        {
            return CalculateManual(request);
        }

        return request.AnchorMode == AnchorMode.TitleBarTopRight
            ? CalculateTitleBar(request)
            : CalculateLegacy(BelowRing(request));
    }

    // At the top right the ring would stand above the window's top edge: the window's top is taken as lower by the
    // ring's height, so the ring sits where the capsule would and the capsule under it, open or not.
    private static OverlayLayoutRequest BelowRing(OverlayLayoutRequest request)
    {
        if (request.AnchorMode != AnchorMode.InsideTopRight || request.Presentation is not { } p || !HasTopRow(p)) return request;
        var host = request.HostWindow;
        var scale = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var rise = AutoCapsule.RingSize(host.Dpi, scale).Disc + Math.Max(2, OverlayRenderMetrics.Create(host.Dpi, scale).MetricGap / 2);
        IntRect Lower(IntRect r) => r.IsEmpty ? r : new(r.X, r.Y + rise, r.Width, Math.Max(1, r.Height - rise));
        return request with { HostWindow = host with { WindowBounds = Lower(host.WindowBounds), ExtendedFrameBounds = Lower(host.ExtendedFrameBounds) } };
    }

    public static Size GetCollapsedSize(
        uint dpi,
        int scalePercent,
        CollapsedDisplayMode display) =>
        new(
            ScaleOverlayDip(
                display == CollapsedDisplayMode.TwoFields
                    ? CollapsedWidthDip
                    : PrimaryOnlyWidthDip,
                dpi,
                scalePercent),
            ScaleOverlayDip(CollapsedHeightDip, dpi, scalePercent));

    private static Size GetTitleBarCollapsedSize(
        uint dpi,
        int scalePercent,
        CollapsedDisplayMode display) =>
        display == CollapsedDisplayMode.TwoFields
            ? new Size(
                ScaleOverlayDip(TitleBarCollapsedWidthDip, dpi, scalePercent),
                ScaleOverlayDip(CollapsedHeightDip, dpi, scalePercent))
            : GetCollapsedSize(dpi, scalePercent, display);

    private static Size ContentSize(OverlayLayoutRequest request, uint dpi, int scale, CollapsedDisplayMode display, bool titleBar = false) =>
        // A capsule with numbers, or a waiting message ("확인 중…"), is as wide as what it shows, not a fixed 360.
        display == CollapsedDisplayMode.TwoFields && (request.Presentation?.CompactMiddle is not null
            || !string.IsNullOrWhiteSpace(request.Presentation?.StatusText))
            ? AutoCapsule.Measure(request.Presentation!, dpi, scale).Size
            : titleBar ? GetTitleBarCollapsedSize(dpi, scale, display) : GetCollapsedSize(dpi, scale, display);

    private static OverlayLayoutResult CalculateManual(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var workingArea = host.WorkingArea;
        if (host.Dpi == 0 || workingArea.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var display = CollapsedDisplayMode.TwoFields;
        var collapsedSize = ContentSize(request, host.Dpi, scalePercent, display);
        if (collapsedSize.Width > workingArea.Width)
        {
            display = CollapsedDisplayMode.PrimaryOnly;
            collapsedSize = ContentSize(request, host.Dpi, scalePercent, display);
        }
        if (collapsedSize.Width > workingArea.Width || collapsedSize.Height > workingArea.Height)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var center = request.ManualCapsuleCenter!.Value;
        var capsuleScreen = new IntRect(
            Clamp(
                center.X - (collapsedSize.Width / 2),
                workingArea.Left,
                workingArea.Right - collapsedSize.Width),
            Clamp(
                center.Y - (collapsedSize.Height / 2),
                workingArea.Top,
                workingArea.Bottom - collapsedSize.Height),
            collapsedSize.Width,
            collapsedSize.Height);
        var collapsed = Collapsed(
            host.Dpi,
            display,
            ExpansionDirection.Down,
            capsuleScreen,
            scalePercent);
        if (!request.RequestExpanded)
        {
            return collapsed;
        }

        var panelWidth = Math.Max(PanelWidth(request, host.Dpi, scalePercent), capsuleScreen.Width);
        if (panelWidth > workingArea.Width)
        {
            return collapsed;
        }

        var maximumCapsuleRight = workingArea.Right;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        var captionSafetyGap = ScaleSystemDip(CaptionSafetyGapDip, host.Dpi);
        if (!visibleHost.IsEmpty &&
            TryGetCaptionRegion(
                host,
                visibleHost,
                captionSafetyGap,
                out var captionTop,
                out var captionBottom,
                out var captionSafeRight) &&
            capsuleScreen.Top < captionBottom &&
            capsuleScreen.Bottom > captionTop)
        {
            maximumCapsuleRight = Math.Min(maximumCapsuleRight, captionSafeRight);
        }

        var minimumCapsuleX = workingArea.Left + panelWidth - capsuleScreen.Width;
        var maximumCapsuleX = maximumCapsuleRight - capsuleScreen.Width;
        if (maximumCapsuleX < minimumCapsuleX)
        {
            return collapsed;
        }

        capsuleScreen = capsuleScreen with
        {
            X = Clamp(
                capsuleScreen.X,
                minimumCapsuleX,
                maximumCapsuleX)
        };
        var panelLeft = capsuleScreen.Right - panelWidth;
        var panelGap = ScaleOverlayDip(CapsulePanelGapDip, host.Dpi, scalePercent);
        var availableBelow = workingArea.Bottom - capsuleScreen.Bottom - panelGap;
        if (TryGetPanelSize(
            request,
            host.Dpi,
            scalePercent,
            availableBelow,
            out var panelHeight,
            out var rowHeight))
        {
            var panelTop = capsuleScreen.Bottom + panelGap;
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                display,
                ExpansionDirection.Down,
                host.Dpi,
                new IntRect(
                    panelLeft,
                    capsuleScreen.Top,
                    panelWidth,
                    panelTop + panelHeight - capsuleScreen.Top),
                new IntRect(
                    capsuleScreen.Left - panelLeft,
                    0,
                    capsuleScreen.Width,
                    capsuleScreen.Height),
                new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }

        var availableAbove = capsuleScreen.Top - panelGap - workingArea.Top;
        if (TryGetPanelSize(
            request,
            host.Dpi,
            scalePercent,
            availableAbove,
            out panelHeight,
            out rowHeight))
        {
            var panelTop = capsuleScreen.Top - panelGap - panelHeight;
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                display,
                ExpansionDirection.Up,
                host.Dpi,
                new IntRect(
                    panelLeft,
                    panelTop,
                    panelWidth,
                    capsuleScreen.Bottom - panelTop),
                new IntRect(
                    capsuleScreen.Left - panelLeft,
                    panelHeight + panelGap,
                    capsuleScreen.Width,
                    capsuleScreen.Height),
                new IntRect(0, 0, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }

        return collapsed;
    }

    private static OverlayLayoutResult CalculateTitleBar(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        if (host.Dpi == 0)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var workingArea = host.WorkingArea;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        if (visibleHost.IsEmpty)
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var safetyGap = ScaleSystemDip(CaptionSafetyGapDip, host.Dpi);
        if (!TryGetCaptionRegion(host, visibleHost, safetyGap, out var captionTop, out var captionBottom, out var safeRight))
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        safeRight = Math.Min(safeRight, Math.Min(visibleHost.Right, workingArea.Right));
        var titleLeft = Math.Max(visibleHost.Left, workingArea.Left) + ScaleSystemDip(TitleLeftReserveDip, host.Dpi);
        var availableWidth = safeRight - titleLeft;
        var captionHeight = captionBottom - captionTop;
        if (!TryGetLargestTitleBarFit(
            request, host.Dpi,
            scalePercent,
            availableWidth,
            captionHeight,
            out var effectiveScale,
            out var collapsedDisplay,
            out var capsuleSize))
        {
            return Hidden(host.Dpi, ExpansionDirection.Down, scalePercent);
        }

        var capsuleScreen = new IntRect(
            safeRight - capsuleSize.Width,
            captionTop + ((captionHeight - capsuleSize.Height) / 2),
            capsuleSize.Width,
            capsuleSize.Height);
        var collapsed = Collapsed(
            host.Dpi,
            collapsedDisplay,
            ExpansionDirection.Down,
            capsuleScreen,
            effectiveScale);
        if (!request.RequestExpanded)
        {
            return collapsed;
        }

        var panelWidth = Math.Max(
            PanelWidth(request, host.Dpi, effectiveScale),
            capsuleSize.Width);
        var panelLeft = capsuleScreen.Right - panelWidth;
        if (panelLeft < workingArea.Left || capsuleScreen.Right > workingArea.Right)
        {
            return collapsed;
        }

        var panelTop = capsuleScreen.Bottom + ScaleOverlayDip(
            CapsulePanelGapDip,
            host.Dpi,
            effectiveScale);
        var availablePanelHeight = workingArea.Bottom - panelTop;
        if (!TryGetPanelSize(
            request,
            host.Dpi,
            effectiveScale,
            availablePanelHeight,
            out var panelHeight,
            out var rowHeight))
        {
            return collapsed;
        }

        var formBounds = new IntRect(
            panelLeft,
            capsuleScreen.Top,
            panelWidth,
            panelTop + panelHeight - capsuleScreen.Top);
        return new OverlayLayoutResult(
            OverlayVisualState.Expanded,
            collapsedDisplay,
            ExpansionDirection.Down,
            host.Dpi,
            formBounds,
            new IntRect(
                capsuleScreen.Left - panelLeft,
                0,
                capsuleSize.Width,
                capsuleSize.Height),
            new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
            rowHeight,
            effectiveScale);
    }

    private static bool TryGetLargestTitleBarFit(
        OverlayLayoutRequest request, uint dpi,
        int requestedScale,
        int availableWidth,
        int captionHeight,
        out int effectiveScale,
        out CollapsedDisplayMode display,
        out Size capsuleSize)
    {
        requestedScale = ManualAttachmentRules.SanitizeScale(requestedScale);
        for (var candidate = requestedScale;
             candidate >= ManualAttachmentRules.MinimumScalePercent;
             candidate--)
        {
            var twoFields = ContentSize(request, dpi, candidate, CollapsedDisplayMode.TwoFields, true);
            if (twoFields.Width <= availableWidth && twoFields.Height <= captionHeight)
            {
                effectiveScale = candidate;
                display = CollapsedDisplayMode.TwoFields;
                capsuleSize = twoFields;
                return true;
            }

            var primary = GetCollapsedSize(dpi, candidate, CollapsedDisplayMode.PrimaryOnly);
            if (primary.Width <= availableWidth && primary.Height <= captionHeight)
            {
                effectiveScale = candidate;
                display = CollapsedDisplayMode.PrimaryOnly;
                capsuleSize = primary;
                return true;
            }
        }

        effectiveScale = requestedScale;
        display = CollapsedDisplayMode.TwoFields;
        capsuleSize = Size.Empty;
        return false;
    }

    private static OverlayLayoutResult CalculateLegacy(OverlayLayoutRequest request)
    {
        var host = request.HostWindow;
        var dpi = host.Dpi == 0 ? 96u : host.Dpi;
        var scalePercent = ManualAttachmentRules.SanitizeScale(request.ScalePercent);
        var workingArea = host.WorkingArea;
        var visibleHost = Intersect(PreferredHostBounds(host), workingArea);
        if (visibleHost.IsEmpty || workingArea.IsEmpty)
        {
            return Hidden(host.Dpi, DirectionFor(request.AnchorMode), scalePercent);
        }

        var outsideGap = ScaleSystemDip(LegacyOutsideGapDip, dpi);
        var insideMargin = ScaleSystemDip(LegacyInsideMarginDip, dpi);
        var fullCapsuleSize = ContentSize(request, dpi, scalePercent, CollapsedDisplayMode.TwoFields);
        var primaryCapsuleSize = GetCollapsedSize(dpi, scalePercent, CollapsedDisplayMode.PrimaryOnly);
        var capsuleHeight = fullCapsuleSize.Height;
        var fullCapsuleWidth = fullCapsuleSize.Width;
        var primaryCapsuleWidth = primaryCapsuleSize.Width;
        var availableWidth = Math.Max(0, workingArea.Width - (2 * outsideGap));
        var collapsedDisplay = availableWidth >= fullCapsuleWidth
            ? CollapsedDisplayMode.TwoFields
            : CollapsedDisplayMode.PrimaryOnly;
        var capsuleWidth = collapsedDisplay == CollapsedDisplayMode.TwoFields
            ? fullCapsuleWidth
            : primaryCapsuleWidth;
        if (availableWidth < primaryCapsuleWidth || workingArea.Height < capsuleHeight)
        {
            return Hidden(host.Dpi, DirectionFor(request.AnchorMode), scalePercent);
        }

        var placement = request.AnchorMode;
        var chooseAutomaticDirection = placement == AnchorMode.Auto;
        var outsideRight = false;
        var outsideLeft = false;
        if (placement == AnchorMode.Auto)
        {
            var requiredWidth = request.RequestExpanded
                ? PanelWidth(request, dpi, scalePercent)
                : capsuleWidth;
            if (workingArea.Right - visibleHost.Right >= requiredWidth + outsideGap)
            {
                outsideRight = true;
            }
            else if (visibleHost.Left - workingArea.Left >= requiredWidth + outsideGap)
            {
                outsideLeft = true;
            }
            else
            {
                placement = AnchorMode.InsideTopRight;
            }
        }

        int capsuleX;
        int capsuleY;
        if (outsideRight)
        {
            capsuleX = visibleHost.Right + outsideGap;
            capsuleY = Math.Max(
                workingArea.Top + outsideGap,
                visibleHost.Bottom - capsuleHeight - ScaleSystemDip(LegacyBottomOffsetDip, dpi));
        }
        else if (outsideLeft)
        {
            capsuleX = visibleHost.Left - capsuleWidth - outsideGap;
            capsuleY = Math.Max(
                workingArea.Top + outsideGap,
                visibleHost.Bottom - capsuleHeight - ScaleSystemDip(LegacyBottomOffsetDip, dpi));
        }
        else if (placement == AnchorMode.InsideBottomRight)
        {
            capsuleX = visibleHost.Right - capsuleWidth - insideMargin;
            capsuleY = visibleHost.Bottom - capsuleHeight - insideMargin;
        }
        else
        {
            capsuleX = visibleHost.Right - capsuleWidth - insideMargin;
            capsuleY = visibleHost.Top + ScaleSystemDip(LegacyHeaderOffsetDip, dpi);
        }

        capsuleX = Clamp(capsuleX, workingArea.Left + outsideGap, workingArea.Right - capsuleWidth - outsideGap);
        capsuleY = Clamp(capsuleY, workingArea.Top + outsideGap, workingArea.Bottom - capsuleHeight - outsideGap);
        var capsuleScreen = new IntRect(capsuleX, capsuleY, capsuleWidth, capsuleHeight);
        var direction = chooseAutomaticDirection
            ? ChooseDirection(request, capsuleScreen, workingArea, dpi, scalePercent)
            : placement == AnchorMode.InsideBottomRight
                ? ExpansionDirection.Up
                : ExpansionDirection.Down;
        var collapsed = Collapsed(
            host.Dpi,
            collapsedDisplay,
            direction,
            capsuleScreen,
            scalePercent);
        if (!request.RequestExpanded)
        {
            return collapsed;
        }

        var panelWidth = Math.Max(PanelWidth(request, dpi, scalePercent), capsuleScreen.Width);
        var panelLeft = capsuleScreen.Right - panelWidth;
        if (panelLeft < workingArea.Left || capsuleScreen.Right > workingArea.Right)
        {
            return collapsed;
        }

        var panelGap = ScaleOverlayDip(CapsulePanelGapDip, dpi, scalePercent);
        var availablePanelHeight = direction == ExpansionDirection.Down
            ? workingArea.Bottom - capsuleScreen.Bottom - panelGap
            : capsuleScreen.Top - panelGap - workingArea.Top;
        if (!TryGetPanelSize(
            request,
            dpi,
            scalePercent,
            availablePanelHeight,
            out var panelHeight,
            out var rowHeight))
        {
            return collapsed;
        }

        if (direction == ExpansionDirection.Down)
        {
            var panelTop = capsuleScreen.Bottom + panelGap;
            var formBounds = new IntRect(
                panelLeft,
                capsuleScreen.Top,
                panelWidth,
                panelTop + panelHeight - capsuleScreen.Top);
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsedDisplay,
                direction,
                host.Dpi,
                formBounds,
                new IntRect(capsuleScreen.Left - panelLeft, 0, capsuleWidth, capsuleHeight),
                new IntRect(0, panelTop - capsuleScreen.Top, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }
        else
        {
            var panelTop = capsuleScreen.Top - panelGap - panelHeight;
            var formBounds = new IntRect(
                panelLeft,
                panelTop,
                panelWidth,
                capsuleScreen.Bottom - panelTop);
            return new OverlayLayoutResult(
                OverlayVisualState.Expanded,
                collapsedDisplay,
                direction,
                host.Dpi,
                formBounds,
                new IntRect(capsuleScreen.Left - panelLeft, panelHeight + panelGap, capsuleWidth, capsuleHeight),
                new IntRect(0, 0, panelWidth, panelHeight),
                rowHeight,
                scalePercent);
        }
    }

    private static bool TryGetCaptionRegion(
        CodexWindowInfo host,
        IntRect visibleHost,
        int safetyGap,
        out int captionTop,
        out int captionBottom,
        out int safeRight)
    {
        if (host.CaptionButtonBounds is { } caption &&
            !caption.IsEmpty &&
            caption.Left >= visibleHost.Left &&
            caption.Left < visibleHost.Right &&
            caption.Bottom > visibleHost.Top &&
            caption.Top < visibleHost.Bottom)
        {
            captionTop = Math.Max(caption.Top, visibleHost.Top);
            captionBottom = Math.Min(caption.Bottom, visibleHost.Bottom);
            safeRight = caption.Left - safetyGap;
            return captionBottom > captionTop;
        }

        var metrics = host.ChromeMetrics;
        if (metrics.CaptionButtonWidth <= 0 ||
            metrics.CaptionButtonHeight <= 0 ||
            metrics.FrameWidth < 0 ||
            metrics.FrameHeight < 0 ||
            metrics.PaddedBorderWidth < 0)
        {
            captionTop = 0;
            captionBottom = 0;
            safeRight = 0;
            return false;
        }

        var reservedWidth =
            (3 * metrics.CaptionButtonWidth) +
            (2 * metrics.FrameWidth) +
            (2 * metrics.PaddedBorderWidth);
        var rawCaptionTop = host.WindowBounds.Top + metrics.FrameHeight + metrics.PaddedBorderWidth;
        var rawCaptionBottom = rawCaptionTop + metrics.CaptionButtonHeight;
        captionTop = Math.Max(visibleHost.Top, rawCaptionTop);
        captionBottom = Math.Min(visibleHost.Bottom, rawCaptionBottom);
        safeRight = host.WindowBounds.Right - reservedWidth - safetyGap;
        return captionBottom > captionTop && safeRight > visibleHost.Left;
    }

    private static bool TryGetPanelSize(
        OverlayLayoutRequest request,
        uint dpi,
        int scalePercent,
        int availableHeight,
        out int panelHeight,
        out int rowHeight)
    {
        var rowCount = Math.Max(0, request.ExpandedRowCount);
        var chromeHeight = ScaleOverlayDip(request.CompactPanel ? CompactPanelChromeHeightDip : PanelChromeHeightDip, dpi, scalePercent);
        var normalRowHeight = ScaleOverlayDip(NormalRowHeightDip, dpi, scalePercent);
        var minimumRowHeight = ScaleOverlayDip(MinimumRowHeightDip, dpi, scalePercent);
        rowHeight = normalRowHeight;
        panelHeight = chromeHeight + (rowCount * rowHeight);
        if (panelHeight <= availableHeight)
        {
            return true;
        }

        if (rowCount == 0)
        {
            rowHeight = 0;
            panelHeight = 0;
            return false;
        }

        rowHeight = (availableHeight - chromeHeight) / rowCount;
        if (rowHeight < minimumRowHeight)
        {
            rowHeight = 0;
            panelHeight = 0;
            return false;
        }

        rowHeight = Math.Min(rowHeight, normalRowHeight);
        panelHeight = chromeHeight + (rowCount * rowHeight);
        return panelHeight <= availableHeight;
    }

    private static ExpansionDirection ChooseDirection(
        OverlayLayoutRequest request,
        IntRect capsule,
        IntRect workingArea,
        uint dpi,
        int scalePercent)
    {
        var gap = ScaleOverlayDip(CapsulePanelGapDip, dpi, scalePercent);
        var below = workingArea.Bottom - capsule.Bottom - gap;
        var above = capsule.Top - gap - workingArea.Top;
        var rowCount = Math.Max(0, request.ExpandedRowCount);
        var normalPanelHeight =
            ScaleOverlayDip(PanelChromeHeightDip, dpi, scalePercent) +
            (rowCount * ScaleOverlayDip(NormalRowHeightDip, dpi, scalePercent));
        if (below >= normalPanelHeight)
        {
            return ExpansionDirection.Down;
        }
        if (above >= normalPanelHeight)
        {
            return ExpansionDirection.Up;
        }

        var minimumPanelHeight =
            ScaleOverlayDip(PanelChromeHeightDip, dpi, scalePercent) +
            (rowCount * ScaleOverlayDip(MinimumRowHeightDip, dpi, scalePercent));
        var belowFitsMinimum = below >= minimumPanelHeight;
        var aboveFitsMinimum = above >= minimumPanelHeight;
        if (belowFitsMinimum != aboveFitsMinimum)
        {
            return belowFitsMinimum
                ? ExpansionDirection.Down
                : ExpansionDirection.Up;
        }

        return below >= above
            ? ExpansionDirection.Down
            : ExpansionDirection.Up;
    }

    private static ExpansionDirection DirectionFor(AnchorMode anchorMode) =>
        anchorMode == AnchorMode.InsideBottomRight
            ? ExpansionDirection.Up
            : ExpansionDirection.Down;

    private static OverlayLayoutResult Collapsed(
        uint dpi,
        CollapsedDisplayMode display,
        ExpansionDirection direction,
        IntRect capsuleScreen,
        int scalePercent) =>
        new(
            OverlayVisualState.Collapsed,
            display,
            direction,
            dpi,
            capsuleScreen,
            new IntRect(0, 0, capsuleScreen.Width, capsuleScreen.Height),
            default,
            0,
            ManualAttachmentRules.SanitizeScale(scalePercent));

    private static OverlayLayoutResult Hidden(
        uint dpi,
        ExpansionDirection direction,
        int scalePercent) =>
        new(
            OverlayVisualState.HiddenForSpace,
            CollapsedDisplayMode.TwoFields,
            direction,
            dpi,
            default,
            default,
            default,
            0,
            ManualAttachmentRules.SanitizeScale(scalePercent));

    private static IntRect PreferredHostBounds(CodexWindowInfo host) =>
        host.ExtendedFrameBounds.IsEmpty ? host.WindowBounds : host.ExtendedFrameBounds;

    private static IntRect Intersect(IntRect first, IntRect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right <= left || bottom <= top
            ? default
            : new IntRect(left, top, right - left, bottom - top);
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private static int ScaleSystemDip(double dip, uint dpi) =>
        (int)Math.Round(dip * dpi / 96d, MidpointRounding.AwayFromZero);

    private static int ScaleOverlayDip(double dip, uint dpi, int scalePercent) =>
        (int)Math.Round(
            dip * dpi / 96d * ManualAttachmentRules.SanitizeScale(scalePercent) / 100d,
            MidpointRounding.AwayFromZero);
}

internal sealed class LayoutProbeRequest
{
    public IReadOnlyList<LayoutProbeCaseRequest> Cases { get; init; } = [];
}

internal sealed class LayoutProbeCaseRequest
{
    public string Name { get; init; } = string.Empty;
    public required LayoutProbeWindowInfo HostWindow { get; init; }
    public AnchorMode AnchorMode { get; init; }
    public bool RequestExpanded { get; init; }
    public int ExpandedRowCount { get; init; }
    public bool ShowContextProgress { get; init; }
    public LayoutProbePoint? ManualCapsuleCenter { get; init; }
    public int ScalePercent { get; init; } = ManualAttachmentRules.DefaultScalePercent;
    public IReadOnlyList<LayoutProbePoint> ClientPoints { get; init; } = [];
    public IReadOnlyList<LayoutProbePoint> ScreenPoints { get; init; } = [];

    public OverlayLayoutRequest ToModel() => new(
        HostWindow.ToModel(),
        AnchorMode,
        RequestExpanded,
        ExpandedRowCount,
        ShowContextProgress,
        ManualCapsuleCenter?.ToPoint(),
        ScalePercent);
}

internal sealed record LayoutProbePoint(int X, int Y)
{
    public Point ToPoint() => new(X, Y);
}

internal sealed class LayoutProbeWindowInfo
{
    public long Handle { get; init; }
    public IntRect WindowBounds { get; init; }
    public IntRect ExtendedFrameBounds { get; init; }
    public IntRect? CaptionButtonBounds { get; init; }
    public IntRect WorkingArea { get; init; }
    public uint Dpi { get; init; }
    public WindowChromeMetrics ChromeMetrics { get; init; }

    public CodexWindowInfo ToModel() => new(
        new IntPtr(Handle),
        WindowBounds,
        ExtendedFrameBounds,
        CaptionButtonBounds,
        WorkingArea,
        Dpi,
        ChromeMetrics);
}

internal sealed record LayoutProbeCaseResult(
    string Name,
    long Handle,
    OverlayLayoutResult Layout,
    IReadOnlyList<bool> ContainsClientPoints,
    IReadOnlyList<bool> ContainsScreenPoints);

internal sealed record LayoutProbeResult(IReadOnlyList<LayoutProbeCaseResult> Cases);

internal static class LayoutProbe
{
    public static LayoutProbeResult Execute(LayoutProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cases = request.Cases.Select(item =>
        {
            var model = item.ToModel();
            var layout = OverlayLayoutCalculator.Calculate(model);
            return new LayoutProbeCaseResult(
                item.Name,
                model.HostWindow.Handle.ToInt64(),
                layout,
                item.ClientPoints.Select(point => layout.ContainsClientPoint(point.ToPoint())).ToArray(),
                item.ScreenPoints.Select(point => layout.ContainsScreenPoint(point.ToPoint())).ToArray());
        }).ToArray();
        return new LayoutProbeResult(cases);
    }
}
