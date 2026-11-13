namespace TokenOverlay;

internal static class OverlayEditMoveDispatcher
{
    public static ManualAttachmentTransition Dispatch(
        ManualAttachmentCoordinator coordinator,
        AttachmentTargetBounds targets,
        OverlayEditPreviewEventArgs eventArgs,
        Point capsuleCenter,
        Func<Point, bool> hostSurfaceResolver,
        bool isCompletion)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(eventArgs);
        ArgumentNullException.ThrowIfNull(hostSurfaceResolver);
        if (eventArgs.Kind != OverlayEditGestureKind.Move)
        {
            throw new ArgumentOutOfRangeException(nameof(eventArgs));
        }

        var hostSurfaceHit = hostSurfaceResolver(eventArgs.CursorScreen);
        return isCompletion
            ? coordinator.CompleteMove(
                targets,
                eventArgs.CursorScreen,
                capsuleCenter,
                hostSurfaceHit)
            : coordinator.PreviewMove(
                targets,
                eventArgs.CursorScreen,
                capsuleCenter,
                hostSurfaceHit);
    }
}

internal static class OverlayEditPreviewLayoutPolicy
{
    public static bool ShouldApplyLayout(
        OverlayEditGestureKind kind,
        ManualAttachmentTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return kind != OverlayEditGestureKind.Move || !transition.CanSave;
    }
}
