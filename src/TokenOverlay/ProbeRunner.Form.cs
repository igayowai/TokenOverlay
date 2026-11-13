namespace TokenOverlay;

internal static partial class ProbeRunner
{
    private static FormProbeResult ExecuteFormProbe(FormProbeRequest request)
    {
        var results = new List<FormProbeCaseResult>(request.Cases.Count);
        foreach (var probeCase in request.Cases)
        {
            using var form = new TokenStripForm();
            var handle = form.Handle;
            var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            var classStyle = GetClassLongPtr(handle, GclStyle).ToInt64();

            var normalCapsuleClickCount = 0;
            form.CapsuleClicked += (_, _) => normalCapsuleClickCount++;

            form.ApplyLayout(probeCase.CollapsedLayout);
            var collapsedBounds = IntRect.FromRectangle(form.Bounds);
            var normalRenderDecorations = form.RenderDecorations;
            form.SimulateCapsuleClick(ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds));

            var setBoundsCount = form.SetBoundsCoreCallCount;
            form.ApplyLayout(probeCase.ExpandedLayout);
            var expandedSetBoundsCoreDelta = form.SetBoundsCoreCallCount - setBoundsCount;
            var expandedBounds = IntRect.FromRectangle(form.Bounds);

            var capsuleCenter = ScreenCenter(
                probeCase.ExpandedLayout.WindowBounds,
                probeCase.ExpandedLayout.CapsuleBounds);
            var panelCenter = ScreenCenter(
                probeCase.ExpandedLayout.WindowBounds,
                probeCase.ExpandedLayout.PanelBounds);
            var topLeft = new Point(
                probeCase.ExpandedLayout.WindowBounds.Left,
                probeCase.ExpandedLayout.WindowBounds.Top);
            var normalMouseActivateResult = SendMessage(
                handle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
            var capsuleCenterHitTest = SendHitTest(handle, capsuleCenter);
            var panelCenterHitTest = SendHitTest(handle, panelCenter);
            var topLeftHitTest = SendHitTest(handle, topLeft);
            var expandedRegionMatchesUnion = RegionMatchesLayout(form, probeCase.ExpandedLayout);
            var normalCommandIntercepted = form.SimulateEditCommand(Keys.Enter);

            var beginEditRejectsExpanded = ThrowsInvalidOperation(() =>
                form.BeginEditMode(probeCase.ExpandedLayout.ScalePercent));

            form.ApplyLayout(probeCase.CollapsedLayout);
            var editCapsuleClickCount = 0;
            form.CapsuleClicked += (_, _) => editCapsuleClickCount++;
            form.BeginEditMode(probeCase.CollapsedLayout.ScalePercent);
            var editHandle = form.Handle;
            var editExtendedStyle = GetWindowLongPtr(editHandle, GwlExStyle).ToInt64();
            var editMouseActivateResult = SendMessage(
                editHandle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();
            form.SimulateCapsuleClick(ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds));
            var editResizeHandle100 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editIsCollapsed = form.CurrentLayout?.State == OverlayVisualState.Collapsed;
            var editRenderDecorations100 = form.RenderDecorations;

            OverlayEditPreviewEventArgs? movePreview = null;
            var editGestureCompletionCount = 0;
            form.EditPreviewChanged += (_, eventArgs) => movePreview = eventArgs;
            form.EditGestureCompleted += (_, _) => editGestureCompletionCount++;
            var moveStart = ScreenCenter(
                probeCase.CollapsedLayout.WindowBounds,
                probeCase.CollapsedLayout.CapsuleBounds);
            var moveCurrent = new Point(moveStart.X + 30, moveStart.Y + 20);
            var moveStartSize = form.Size;
            form.SimulateEditDrag(moveStart, moveCurrent);
            var movePreviewBounds = IntRect.FromRectangle(form.Bounds);
            form.SimulateEditGestureCompleted(moveCurrent);
            var capturedMovePreview = movePreview;

            var saveRequestCount = 0;
            var cancelRequestCount = 0;
            form.EditSaveRequested += (_, _) => saveRequestCount++;
            form.EditCancelRequested += (_, _) => cancelRequestCount++;
            form.SimulateEditCommand(Keys.Enter);
            form.SimulateEditCommand(Keys.Escape);

            var minimumResize = ExecuteResizeProbe(form, probeCase.CollapsedLayout, -2000);
            var maximumResize = ExecuteResizeProbe(form, probeCase.CollapsedLayout, 2000);
            var lostMoveCapture = ExecuteLostCaptureProbe(
                probeCase.CollapsedLayout,
                OverlayEditGestureKind.Move);
            var lostResizeCapture = ExecuteLostCaptureProbe(
                probeCase.CollapsedLayout,
                OverlayEditGestureKind.Resize);
            var cancelCapture = ExecuteCancelledCaptureProbe(probeCase.CollapsedLayout);
            var disposeCapture = ExecuteDisposedCaptureProbe(probeCase.CollapsedLayout);

            form.ApplyLayout(probeCase.Collapsed60Layout);
            var editResizeHandle60 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editRenderDecorations60 = form.RenderDecorations;
            form.ApplyLayout(probeCase.Collapsed130Layout);
            var editResizeHandle130 = IntRect.FromRectangle(form.EditResizeHandleBounds);
            var editRenderDecorations130 = form.RenderDecorations;

            form.EndEditMode();
            var restoredHandle = form.Handle;
            var restoredExtendedStyle = GetWindowLongPtr(restoredHandle, GwlExStyle).ToInt64();
            var restoredMouseActivateResult = SendMessage(
                restoredHandle,
                WmMouseActivate,
                IntPtr.Zero,
                IntPtr.Zero).ToInt32();

            using var highlight = new AttachmentTargetHighlightForm();
            var highlightHandle = highlight.Handle;
            var highlightExtendedStyle = GetWindowLongPtr(highlightHandle, GwlExStyle).ToInt64();
            var highlightTarget = new IntRect(300, 240, 420, 260);
            var highlightSetBoundsCount = highlight.SetBoundsCoreCallCount;
            highlight.ShowTarget(highlightTarget);
            var highlightSetBoundsCoreDelta = highlight.SetBoundsCoreCallCount - highlightSetBoundsCount;
            var highlightBounds = IntRect.FromRectangle(highlight.Bounds);
            var highlightDeviceDpi = highlight.DeviceDpi <= 0 ? 96 : highlight.DeviceDpi;
            var highlightExpectedRingThicknessPixels = Math.Max(
                1,
                (int)Math.Round(
                    2 * highlightDeviceDpi / 96d,
                    MidpointRounding.AwayFromZero));
            var highlightRing = HighlightHasRingRegion(
                highlight,
                highlightExpectedRingThicknessPixels);
            var highlightHitTest = SendHitTest(highlight.Handle, new Point(301, 241));
            highlight.ClearTarget();
            var highlightHiddenAfterClear = !highlight.Visible;

            results.Add(new FormProbeCaseResult(
                probeCase.Name,
                (extendedStyle & WsExToolWindow) != 0,
                (extendedStyle & WsExNoActivate) != 0,
                (extendedStyle & WsExTransparent) != 0,
                (classStyle & CsDropShadow) != 0,
                normalMouseActivateResult,
                capsuleCenterHitTest,
                panelCenterHitTest,
                topLeftHitTest,
                collapsedBounds,
                expandedBounds,
                expandedSetBoundsCoreDelta,
                expandedRegionMatchesUnion,
                normalCapsuleClickCount,
                normalCommandIntercepted,
                normalRenderDecorations,
                beginEditRejectsExpanded,
                (editExtendedStyle & WsExToolWindow) != 0,
                (editExtendedStyle & WsExNoActivate) != 0,
                editMouseActivateResult,
                editIsCollapsed,
                editCapsuleClickCount,
                editResizeHandle60,
                editResizeHandle100,
                editResizeHandle130,
                editRenderDecorations60,
                editRenderDecorations100,
                editRenderDecorations130,
                capturedMovePreview,
                movePreviewBounds,
                moveStartSize.Width == movePreviewBounds.Width
                    && moveStartSize.Height == movePreviewBounds.Height,
                minimumResize,
                maximumResize,
                lostMoveCapture,
                lostResizeCapture,
                cancelCapture,
                disposeCapture,
                editGestureCompletionCount,
                saveRequestCount,
                cancelRequestCount,
                (restoredExtendedStyle & WsExNoActivate) != 0,
                restoredMouseActivateResult,
                OverlayRenderMetrics.Create(96, 60),
                OverlayRenderMetrics.Create(96, 100),
                OverlayRenderMetrics.Create(96, 130),
                (highlightExtendedStyle & WsExToolWindow) != 0,
                (highlightExtendedStyle & WsExNoActivate) != 0,
                (highlightExtendedStyle & WsExTransparent) != 0,
                highlight.ShowInTaskbar,
                highlightSetBoundsCoreDelta,
                highlightBounds,
                highlightDeviceDpi,
                highlightExpectedRingThicknessPixels,
                highlightRing,
                highlightHitTest,
                highlightHiddenAfterClear,
                highlight.Region is null));
        }

        return new FormProbeResult(results);
    }

    private static OverlayEditPreviewEventArgs ExecuteResizeProbe(
        TokenStripForm form,
        OverlayLayoutResult collapsedLayout,
        int delta)
    {
        form.ApplyLayout(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        form.EditPreviewChanged += CapturePreview;
        var resizeHandle = form.EditResizeHandleBounds;
        var start = form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
        var current = new Point(start.X + delta, start.Y + delta);
        form.SimulateEditResize(start, current);
        form.EditPreviewChanged -= CapturePreview;
        form.SimulateEditGestureCompleted(current);
        return preview ?? throw new InvalidOperationException("The resize simulation produced no preview event.");

        void CapturePreview(object? sender, OverlayEditPreviewEventArgs eventArgs) => preview = eventArgs;
    }

    private static EditCaptureLossProbeResult ExecuteLostCaptureProbe(
        OverlayLayoutResult collapsedLayout,
        OverlayEditGestureKind kind)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = kind == OverlayEditGestureKind.Move
            ? ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds)
            : ResizeHandleCenter(form);
        var current = new Point(start.X + 31, start.Y + 19);
        if (kind == OverlayEditGestureKind.Move)
        {
            form.SimulateEditDrag(start, current);
        }
        else
        {
            form.SimulateEditResize(start, current);
        }

        var activeBeforeLoss = form.IsEditGestureActive;
        form.SimulateEditCaptureLost();
        var activeAfterLoss = form.IsEditGestureActive;
        var captureAfterLoss = form.Capture;
        form.SimulateEditCaptureLost();
        form.SimulateEditGestureCompleted(new Point(current.X + 5, current.Y + 5));
        var activeAfterRepeatedSignals = form.IsEditGestureActive;

        return new EditCaptureLossProbeResult(
            kind,
            preview,
            completed,
            completionCount,
            activeBeforeLoss,
            activeAfterLoss,
            activeAfterRepeatedSignals,
            captureAfterLoss,
            0);
    }

    private static EditCaptureLossProbeResult ExecuteCancelledCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        using var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        var cancelCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };
        form.EditCancelRequested += (_, _) => cancelCount++;

        var start = ScreenCenter(collapsedLayout.WindowBounds, collapsedLayout.CapsuleBounds);
        var current = new Point(start.X + 17, start.Y + 11);
        form.SimulateEditDrag(start, current);
        var activeBeforeCancel = form.IsEditGestureActive;
        form.SimulateEditCommand(Keys.Escape);
        form.EndEditMode();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Move,
            preview,
            completed,
            completionCount,
            activeBeforeCancel,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            cancelCount);
    }

    private static EditCaptureLossProbeResult ExecuteDisposedCaptureProbe(
        OverlayLayoutResult collapsedLayout)
    {
        var form = CreateEditingForm(collapsedLayout);
        OverlayEditPreviewEventArgs? preview = null;
        OverlayEditPreviewEventArgs? completed = null;
        var completionCount = 0;
        form.EditPreviewChanged += (_, eventArgs) => preview = eventArgs;
        form.EditGestureCompleted += (_, eventArgs) =>
        {
            completionCount++;
            completed = eventArgs;
        };

        var start = ResizeHandleCenter(form);
        var current = new Point(start.X + 13, start.Y + 13);
        form.SimulateEditResize(start, current);
        var activeBeforeDispose = form.IsEditGestureActive;
        form.Dispose();

        return new EditCaptureLossProbeResult(
            OverlayEditGestureKind.Resize,
            preview,
            completed,
            completionCount,
            activeBeforeDispose,
            form.IsEditGestureActive,
            form.IsEditGestureActive,
            form.Capture,
            0);
    }

    private static TokenStripForm CreateEditingForm(OverlayLayoutResult collapsedLayout)
    {
        var form = new TokenStripForm();
        _ = form.Handle;
        form.ApplyLayout(collapsedLayout);
        form.BeginEditMode(collapsedLayout.ScalePercent);
        return form;
    }

    private static Point ResizeHandleCenter(TokenStripForm form)
    {
        var resizeHandle = form.EditResizeHandleBounds;
        return form.PointToScreen(new Point(
            resizeHandle.Left + Math.Max(0, resizeHandle.Width / 2),
            resizeHandle.Top + Math.Max(0, resizeHandle.Height / 2)));
    }

    private static bool HighlightHasRingRegion(
        AttachmentTargetHighlightForm form,
        int expectedThicknessPixels)
    {
        if (form.Region is null || form.ClientSize.Width < 8 || form.ClientSize.Height < 8)
        {
            return false;
        }

        var outer = new Rectangle(Point.Empty, form.ClientSize);
        using var expected = new Region(outer);
        if (form.ClientSize.Width > expectedThicknessPixels * 2
            && form.ClientSize.Height > expectedThicknessPixels * 2)
        {
            expected.Exclude(Rectangle.Inflate(
                outer,
                -expectedThicknessPixels,
                -expectedThicknessPixels));
        }

        using var graphics = form.CreateGraphics();
        return form.Region.Equals(expected, graphics);
    }
}
