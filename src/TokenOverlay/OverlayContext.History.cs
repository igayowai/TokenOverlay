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
                _form.SetHistoryPinned(true);
            }
            else
            {
                EndHistory(relayout: true);
            }
            return;
        }

        if (!_interaction.OnCapsuleMouseUp() || _currentTarget is null)
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
            if (_historyPinnedUntil is not null)
            {
                if (!HistoryPinned) EndHistory(relayout: true);
                return;
            }
            // A screen-capture tool taking over the foreground must not close what is being captured.
            if (ScreenCaptureTools.IsForeground()) return;
            if (!control || !_form.Visible || _manualAttachment.IsEditing
                || !Rectangle.Inflate(_form.Bounds, 6, 6).Contains(cursor))
            {
                EndHistory(relayout: true);
            }
            return;
        }

        if (control && _currentTarget is not null && !_manuallyHidden && !_manualAttachment.IsEditing
            && (_form.Visible || _form.HoverHidden)
            && _interaction.State != OverlayVisualState.Expanded
            && _form.CapsuleScreenBounds.Contains(cursor)
            // Claude: whether the mod runs here and its sessions, read now (the list may fall back to the turns).
            && (_presentation.App != HostApp.Claude || RefreshSessionRows(scan: true))
            && _presentation.HasHistoryPanel)
        {
            _historyMode = true;
            _form.CancelHoverHide();
            _form.SetShowHistory(true);
            ApplyLayout(_currentTarget);
        }
    }

    private void EndHistory(bool relayout)
    {
        if (!_historyMode) return;
        _historyMode = false;
        _historyPinnedUntil = null;
        _form.SetHistoryPinned(false);
        _form.SetShowHistory(false);
        _form.Invalidate();
        if (relayout && _currentTarget is not null && _form.Visible) ApplyLayout(_currentTarget);
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
