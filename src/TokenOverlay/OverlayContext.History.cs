namespace TokenOverlay;

internal sealed partial class OverlayContext
{
    private void HandleCapsuleClicked(object? sender, EventArgs eventArgs)
    {
        if (_historyMode)
        {
            // Click while the list is open: pin it, or release a pinned one.
            if (_historyPinnedUntil is null)
            {
                _historyPinnedUntil = DateTime.UtcNow + HistoryPinDuration;
                ListForm.SetHistoryPinned(true);
            }
            else
            {
                EndHistory(relayout: true);
            }
            return;
        }

        // A pane capsule's click only pins or releases its list; the details panel is the main capsule's.
        if (sender != _form || !_interaction.OnCapsuleMouseUp() || _currentTarget is null)
        {
            return;
        }

        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
        }
        ApplyLayout(_currentTarget);
    }

    private void PollOutsidePointer()
    {
        if (!_interaction.ShouldPollOutsideClicks)
        {
            StopOutsideClickPolling();
            return;
        }

        if (!PointerInput.TryGetCursorPosition(out var position))
        {
            return;
        }

        if (_interaction.OnPointerSample(
            PointerInput.ReadPressedButtons(),
            _form.ContainsScreenPoint(position)))
        {
            StopOutsideClickPolling();
            if (_currentTarget is not null)
            {
                ApplyLayout(_currentTarget);
            }
        }
    }

    private void UpdateOutsideClickPolling()
    {
        if (_interaction.ShouldPollOutsideClicks && !_manuallyHidden)
        {
            _outsideClickTimer.Start();
        }
        else
        {
            StopOutsideClickPolling();
        }
    }

    private void StopOutsideClickPolling() => _outsideClickTimer.Stop();

    private void PollHistoryGesture()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var control = (Control.ModifierKeys & Keys.Control) != 0;
        var cursor = Cursor.Position;
        if (_historyMode)
        {
            // About once a second while open: the session rows follow the pings and the scans.
            if (++_sessionTicks % 12 == 0 && _presentation.App == HostApp.Claude) RefreshSessionRows(scan: true);
            RefreshFreeTokens();
            if (_historyPinnedUntil is not null)
            {
                if (!HistoryPinned) EndHistory(relayout: true);
                return;
            }
            // A screen-capture tool taking over the foreground must not close what is being captured.
            if (ScreenCaptureTools.IsForeground()) return;
            var list = ListForm;
            if (!control || !list.Visible || _manualAttachment.IsEditing
                || !Rectangle.Inflate(list.Bounds, 6, 6).Contains(cursor))
            {
                EndHistory(relayout: true);
            }
            return;
        }

        if (!control || _currentTarget is not { } target || _manuallyHidden || _manualAttachment.IsEditing) return;
        if ((_form.Visible || _form.HoverHidden)
            && _interaction.State != OverlayVisualState.Expanded
            && _form.CapsuleScreenBounds.Contains(cursor))
        {
            // Claude: whether the mod runs here and its sessions, read now (the list may fall back to the turns).
            if ((_presentation.App != HostApp.Claude || RefreshSessionRows(scan: true))
                && FreeTokensRead()
                && _presentation.HasHistoryPanel)
            {
                _historyMode = true;
                _form.CancelHoverHide();
                _form.SetShowHistory(true);
                ApplyLayout(target);
            }
            return;
        }

        // A split view's other panes: the capsule under the pointer opens its own chat's list in place.
        var pane = _paneForms.FindIndex(form => (form.Visible || form.HoverHidden) && form.CapsuleScreenBounds.Contains(cursor));
        if (pane < 0 || pane >= _panePresentations.Count) return;
        RefreshSessionRows(scan: true);
        FreeTokensRead();
        if (!_panePresentations[pane].HasHistoryPanel && SessionsFor(HostApp.Claude) is null) return;
        _historyMode = true;
        _historyPane = _paneForms[pane];
        _historyPane.CancelHoverHide();
        _historyPane.SetShowHistory(true);
        UpdatePaneForms(target);
    }

    // The latest complimentary-token numbers into the list before it opens (always goes on).
    private bool FreeTokensRead()
    {
        RefreshFreeTokens();
        return true;
    }

    private void EndHistory(bool relayout)
    {
        if (!_historyMode) return;
        var list = ListForm;
        _historyMode = false;
        _historyPane = null;
        _historyPinnedUntil = null;
        list.SetHistoryPinned(false);
        list.SetShowHistory(false);
        list.Invalidate();
        if (!relayout || _currentTarget is null) return;
        if (list != _form) UpdatePaneForms(_currentTarget);
        else if (_form.Visible) ApplyLayout(_currentTarget);
    }

    private void CollapseAndHide()
    {
        EndHistory(relayout: false);
        _interaction.CollapseForHostChange();
        StopOutsideClickPolling();
        _form.Hide();
        HidePaneForms();
    }
}
