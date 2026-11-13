namespace TokenOverlay;

internal sealed partial class OverlayContext
{
    private void BeginManualEditing()
    {
        if (_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        if (!CodexWindowLocator.TryRefreshKnownCodexTarget(
            _currentTarget,
            out var refreshedTarget))
        {
            _currentTarget = null;
            UpdateManualMenuState();
            return;
        }
        _currentTarget = refreshedTarget;

        // A pinned recent-turn list would keep the panel open; editing starts from the plain capsule.
        EndHistory(relayout: false);
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = SnapshotFromSettings();
        _saveFailureNotified = false;
        var transition = _manualAttachment.BeginEdit(
            _settingsSnapshotBeforeEdit,
            CreateAttachmentTargets(_currentTarget));
        ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        _form.BeginEditMode(transition.Draft.ScalePercent);
        if (!_form.Visible)
        {
            _form.Show();
        }
        UpdateManualMenuState();
    }

    private void HandleEditPreviewChanged(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.BeginGesturePreview();
        ManualAttachmentTransition transition;
        if (eventArgs.Kind == OverlayEditGestureKind.Move)
        {
            transition = OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                CurrentCapsuleCenter(),
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: false);
            ApplyEditTransition(
                _currentTarget,
                transition,
                OverlayEditPreviewLayoutPolicy.ShouldApplyLayout(eventArgs.Kind, transition));
        }
        else
        {
            transition = _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
            ApplyEditTransition(_currentTarget, transition, applyLayout: true);
        }
        UpdateManualMenuState();
    }

    private void HandleEditGestureCompleted(
        object? sender,
        OverlayEditPreviewEventArgs eventArgs)
    {
        _ = sender;
        if (!_manualAttachment.IsEditing || _currentTarget is null)
        {
            return;
        }

        var targets = CreateAttachmentTargets(_currentTarget);
        _manualAttachment.EndGesturePreview();
        var transition = eventArgs.Kind == OverlayEditGestureKind.Move
            ? OverlayEditMoveDispatcher.Dispatch(
                _manualAttachment,
                targets,
                eventArgs,
                CurrentCapsuleCenter(),
                point => IsCursorOnKnownHost(_currentTarget!, point),
                isCompletion: true)
            : _manualAttachment.PreviewResize(
                targets,
                eventArgs.FixedTopLeft,
                eventArgs.ScalePercent,
                CurrentCollapsedDisplay());
        ApplyEditTransition(
            _currentTarget,
            transition,
            applyLayout: true);
        UpdateManualMenuState();
    }

    private void SaveManualEditing()
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }
        if (!_manualAttachment.CanSave)
        {
            NotifySaveFailure("오버레이를 Codex·Claude 창 위로 옮겨주셈.");
            return;
        }

        var original = _settingsSnapshotBeforeEdit ?? SnapshotFromSettings();
        var draft = _manualAttachment.Draft with { Enabled = true };
        ApplySnapshotToSettings(draft);
        if (!_settings.TrySave(_settingsPath))
        {
            ApplySnapshotToSettings(original);
            NotifySaveFailure("위치 설정을 저장하지 못했음.");
            return;
        }

        var committed = _manualAttachment.Commit();
        ApplySnapshotToSettings(committed.Draft);
        FinishManualEditing(restoreFocus: true, relayout: true);
    }

    private void CancelManualEditing(
        bool restoreFocus = true,
        bool relayout = true)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var cancelled = _manualAttachment.Cancel();
        ApplySnapshotToSettings(cancelled.Draft);
        FinishManualEditing(restoreFocus, relayout);
    }

    private void ResetManualPlacement()
    {
        if (_manualAttachment.IsEditing)
        {
            return;
        }

        _settings.ManualPlacementEnabled = true;
        _settings.MainAttachment = ManualAttachmentRules.DefaultMainAttachment;
        _settings.OverlayScalePercent = ManualAttachmentRules.DefaultScalePercent;
        if (!_settings.TrySave(_settingsPath))
        {
            _trayIcon.ShowBalloonTip(
                3000,
                TrayName,
                "복원한 위치를 저장하지 못했음.",
                ToolTipIcon.Warning);
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        UpdateManualMenuState();
        if (_currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }
    }

    private void ApplyEditDraftLayout(CodexWindowTarget target)
    {
        if (!_manualAttachment.IsEditing)
        {
            return;
        }

        var transition = new ManualAttachmentTransition(
            _manualAttachment.Draft,
            IsEditing: true,
            _manualAttachment.CanSave,
            RequiresPersist: false,
            ShouldCollapse: true,
            HighlightBounds: _manualAttachment.ShouldShowStaticHighlight
                ? target.HostWindow.WindowBounds
                : null,
            ResolvedCenter: ManualAttachmentCoordinator.ResolveCenter(
                _manualAttachment.Draft,
                CreateAttachmentTargets(target)));
        ApplyEditTransition(target, transition, applyLayout: true);
    }

    private void ApplyEditTransition(
        CodexWindowTarget target,
        ManualAttachmentTransition transition,
        bool applyLayout)
    {
        if (transition.HighlightBounds is IntRect highlight && !highlight.IsEmpty)
        {
            _targetHighlight.ShowTarget(highlight);
        }
        else
        {
            _targetHighlight.ClearTarget();
        }

        if (!applyLayout || transition.ResolvedCenter is not Point center)
        {
            return;
        }

        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            target.HostWindow,
            _settings.AnchorFor(target.App),
            RequestExpanded: false,
            _presentation.ExpandedRows.Count,
            _presentation.ShowContextProgress,
            center,
            ScalePercent: transition.Draft.ScalePercent));
        _form.ApplyLayout(layout);
        if (layout.State == OverlayVisualState.HiddenForSpace)
        {
            _form.Hide();
        }
        else if (!_form.Visible && !_form.HoverHidden)
        {
            _form.Show();
        }
    }

    private void FinishManualEditing(bool restoreFocus, bool relayout)
    {
        var focusTarget = _currentTarget;
        _targetHighlight.ClearTarget();
        _form.EndEditMode();
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _settingsSnapshotBeforeEdit = null;
        _saveFailureNotified = false;
        UpdateManualMenuState();

        if (relayout && _currentTarget is not null && !_manuallyHidden)
        {
            ApplyLayout(_currentTarget);
        }

        if (restoreFocus
            && focusTarget is not null
            && CodexWindowLocator.TryRefreshKnownCodexTarget(focusTarget, out var refreshed))
        {
            _currentTarget = refreshed;
            SetForegroundWindow(refreshed.HostWindow.Handle);
        }
    }

    private Point CurrentCapsuleCenter()
    {
        var layout = _form.CurrentLayout
            ?? throw new InvalidOperationException("The editing layout has not been built yet.");
        return new Point(
            _form.Left + layout.CapsuleBounds.X + (layout.CapsuleBounds.Width / 2),
            _form.Top + layout.CapsuleBounds.Y + (layout.CapsuleBounds.Height / 2));
    }

    private CollapsedDisplayMode CurrentCollapsedDisplay() =>
        _form.CurrentLayout?.CollapsedDisplay ?? CollapsedDisplayMode.TwoFields;

    private ManualPlacementSnapshot SnapshotFromSettings() => new(
        _settings.ManualPlacementEnabled,
        ManualAttachmentRules.SanitizeMain(_settings.MainAttachment),
        ManualAttachmentRules.SanitizeScale(_settings.OverlayScalePercent));

    private void ApplySnapshotToSettings(ManualPlacementSnapshot snapshot)
    {
        _settings.ManualPlacementEnabled = snapshot.Enabled;
        _settings.MainAttachment = ManualAttachmentRules.SanitizeMain(snapshot.MainAttachment);
        _settings.OverlayScalePercent = ManualAttachmentRules.SanitizeScale(snapshot.ScalePercent);
    }

    private static AttachmentTargetBounds CreateAttachmentTargets(CodexWindowTarget target) => new(
        target.HostWindow.Handle.ToInt64(),
        target.HostWindow.WindowBounds,
        target.HostWindow.WorkingArea,
        target.HostWindow.Dpi);

    private bool IsCursorOnKnownHost(CodexWindowTarget target, Point point) =>
        CodexWindowLocator.IsPointOnKnownHost(
            target,
            point,
            new HashSet<long>
            {
                _form.Handle.ToInt64(),
                _targetHighlight.Handle.ToInt64()
            });
}
