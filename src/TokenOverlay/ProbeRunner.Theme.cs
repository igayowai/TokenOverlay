using Microsoft.Win32;

namespace TokenOverlay;

internal static partial class ProbeRunner
{
    private static object ExecuteThemeProbe()
    {
        var backgroundErrors = new List<string>();
        object? registryValue = 0;
        UserPreferenceChangedEventHandler? registeredHandler = null;
        UserPreferenceChangedEventHandler? capturedHandler = null;
        var subscribeCount = 0;
        var unsubscribeCount = 0;
        var sourceChangedCount = 0;
        var sourceChangedThread = -1;

        var source = new WindowsOverlayThemeSource(
            () => registryValue,
            handler =>
            {
                subscribeCount++;
                registeredHandler = handler;
                capturedHandler = handler;
            },
            handler =>
            {
                unsubscribeCount++;
                if (ReferenceEquals(registeredHandler, handler))
                {
                    registeredHandler = null;
                }
            });
        source.Changed += (_, _) =>
        {
            sourceChangedCount++;
            sourceChangedThread = Environment.CurrentManagedThreadId;
        };
        var sourceInitialDark = source.Current == OverlayThemeKind.Dark
            && subscribeCount == 1;
        registryValue = 1;
        var sourceChangeTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sourceChangeCompleted = PumpUntil(() => sourceChangeTask.IsCompleted);
        var sourceChangedOnce = sourceChangeCompleted
            && source.Current == OverlayThemeKind.Light
            && sourceChangedCount == 1
            && sourceChangedThread != Environment.CurrentManagedThreadId;

        registryValue = 2;
        var sameSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var sameSourceCompleted = PumpUntil(() => sameSourceTask.IsCompleted);
        var sourceSameKindIgnored = sameSourceCompleted && sourceChangedCount == 1;

        source.Dispose();
        source.Dispose();
        registryValue = 0;
        var postDisposeSourceTask = RunCaptured(
            () => capturedHandler?.Invoke(
                source,
                new UserPreferenceChangedEventArgs(UserPreferenceCategory.General)),
            backgroundErrors);
        var postDisposeSourceCompleted = PumpUntil(() => postDisposeSourceTask.IsCompleted);
        var sourcePostDisposeIgnored = postDisposeSourceCompleted
            && sourceChangedCount == 1
            && source.Current == OverlayThemeKind.Light;

        using var dispatcher = new Control();
        _ = dispatcher.Handle;
        var uiThreadId = Environment.CurrentManagedThreadId;
        var applied = new List<(Color Background, int ThreadId)>();
        var fake = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var binding = new OverlayThemeBinding(
            dispatcher,
            fake,
            palette => applied.Add((palette.Background, Environment.CurrentManagedThreadId)));
        var bindingInitialDark = applied.Count == 1
            && applied[0].Background == OverlayThemePalette.For(OverlayThemeKind.Dark).Background
            && applied[0].ThreadId == uiThreadId;

        var lightTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var lightTaskCompleted = PumpUntil(() => lightTask.IsCompleted);
        var lightApplied = PumpUntil(() => applied.Count == 2);
        var bindingBackgroundLightOnUiThread = lightTaskCompleted
            && lightApplied
            && applied[1].Background == OverlayThemePalette.For(OverlayThemeKind.Light).Background
            && applied[1].ThreadId == uiThreadId;

        var sameBindingTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var sameBindingTaskCompleted = PumpUntil(() => sameBindingTask.IsCompleted);
        Application.DoEvents();
        var bindingSameKindIgnored = sameBindingTaskCompleted && applied.Count == 2;

        var queuedTask = RunCaptured(
            () => fake.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var queuedTaskCompleted = WaitWithoutPumping(() => queuedTask.IsCompleted);
        binding.Dispose();
        binding.Dispose();
        Application.DoEvents();
        var bindingQueuedCallbackCancelledOnDispose = queuedTaskCompleted && applied.Count == 2;

        using var themedForm = new TokenStripForm();
        using var themedHighlight = new AttachmentTargetHighlightForm();
        _ = themedForm.Handle;
        _ = themedHighlight.Handle;
        var themedPresentation = OverlayPresentationBuilder.CreateWaiting(
            "theme-probe",
            DisplayField.Total,
            DisplayField.ContextPercent,
            DisplayField.Total | DisplayField.ContextPercent);
        themedForm.SetPresentation(themedPresentation);
        themedForm.BeginEditMode(ManualAttachmentRules.DefaultScalePercent);
        var themedBounds = themedForm.Bounds;
        var themedLayout = themedForm.CurrentLayout;
        var themedPresentationReference = themedForm.CurrentPresentation;
        var themedEditState = themedForm.IsEditMode;
        var themedVisibility = themedForm.Visible;
        var themedHighlightBounds = themedHighlight.Bounds;
        var themedHighlightVisibility = themedHighlight.Visible;
        var formApplyCount = 0;
        var formApplyThreads = new List<int>();
        var formSource = new ProbeOverlayThemeSource(OverlayThemeKind.Dark);
        var formBinding = new OverlayThemeBinding(
            themedHighlight,
            formSource,
            palette =>
            {
                formApplyCount++;
                formApplyThreads.Add(Environment.CurrentManagedThreadId);
                themedForm.ApplyTheme(palette);
                themedHighlight.ApplyTheme(palette);
            });
        var darkPalette = OverlayThemePalette.For(OverlayThemeKind.Dark);
        var lightPalette = OverlayThemePalette.For(OverlayThemeKind.Light);
        var formsInitialDark = formApplyCount == 1
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && themedForm.BackColor == darkPalette.Background
            && themedForm.ForeColor == darkPalette.Value
            && themedHighlight.BackColor == Color.Fuchsia
            && themedHighlight.TransparencyKey == Color.Fuchsia;

        var formLightTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formLightTaskCompleted = PumpUntil(() => formLightTask.IsCompleted);
        var formLightApplied = PumpUntil(() => formApplyCount == 2);
        var formsBackgroundLightOnUiThread = formLightTaskCompleted
            && formLightApplied
            && themedForm.CurrentThemePalette == lightPalette
            && themedHighlight.CurrentThemePalette == lightPalette
            && themedForm.BackColor == lightPalette.Background
            && themedForm.ForeColor == lightPalette.Value
            && formApplyThreads.All(threadId => threadId == uiThreadId);
        var formsStateUnchanged = themedForm.Bounds == themedBounds
            && ReferenceEquals(themedForm.CurrentLayout, themedLayout)
            && ReferenceEquals(themedForm.CurrentPresentation, themedPresentationReference)
            && themedForm.IsEditMode == themedEditState
            && themedForm.Visible == themedVisibility
            && themedHighlight.Bounds == themedHighlightBounds
            && themedHighlight.Visible == themedHighlightVisibility;

        var formSameTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Light, forceEvent: true),
            backgroundErrors);
        var formSameTaskCompleted = PumpUntil(() => formSameTask.IsCompleted);
        Application.DoEvents();
        var formsSameKindIgnored = formSameTaskCompleted && formApplyCount == 2;

        themedForm.EndEditMode();
        var formDarkTask = RunCaptured(
            () => formSource.Set(OverlayThemeKind.Dark, forceEvent: true),
            backgroundErrors);
        var formDarkTaskCompleted = PumpUntil(() => formDarkTask.IsCompleted);
        var formDarkApplied = PumpUntil(() => formApplyCount == 3);
        var formsSurviveTokenHandleRecreation = formDarkTaskCompleted
            && formDarkApplied
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette
            && formApplyThreads.All(threadId => threadId == uiThreadId);

        formBinding.Dispose();
        formBinding.Dispose();
        var postDisposeFormTask = RunCaptured(
            formSource.RaiseCapturedEvent,
            backgroundErrors);
        var postDisposeFormCompleted = PumpUntil(() => postDisposeFormTask.IsCompleted);
        Application.DoEvents();
        var formsPostDisposeIgnored = postDisposeFormCompleted
            && formApplyCount == 3
            && themedForm.CurrentThemePalette == darkPalette
            && themedHighlight.CurrentThemePalette == darkPalette;

        return new
        {
            Cases = new[]
            {
                new
                {
                    Name = "theme-lifecycle",
                    Supported = true,
                    SourceInitialDark = sourceInitialDark,
                    SourceChangedOnce = sourceChangedOnce,
                    SourceSameKindIgnored = sourceSameKindIgnored,
                    SourceUnsubscribedOnce = unsubscribeCount == 1 && registeredHandler is null,
                    SourceDisposeIdempotent = unsubscribeCount == 1,
                    SourcePostDisposeIgnored = sourcePostDisposeIgnored,
                    BindingInitialDark = bindingInitialDark,
                    BindingBackgroundLightOnUiThread = bindingBackgroundLightOnUiThread,
                    BindingSameKindIgnored = bindingSameKindIgnored,
                    BindingQueuedCallbackCancelledOnDispose = bindingQueuedCallbackCancelledOnDispose,
                    BindingUnsubscribedBeforeSourceDispose = fake.UnsubscribedBeforeDispose,
                    FormsSupported = true,
                    FormsInitialDark = formsInitialDark,
                    FormsBackgroundLightOnUiThread = formsBackgroundLightOnUiThread,
                    FormsStateUnchanged = formsStateUnchanged,
                    FormsSameKindIgnored = formsSameKindIgnored,
                    FormsSurviveTokenHandleRecreation = formsSurviveTokenHandleRecreation,
                    FormsPostDisposeIgnored = formsPostDisposeIgnored,
                    NoBackgroundException = backgroundErrors.Count == 0
                }
            }
        };
    }
}
