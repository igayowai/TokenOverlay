using System.Text.Json;

namespace TokenOverlay;

internal static partial class ProbeRunner
{
    private static object ExecuteLayoutProbe(string path)
    {
        var request = ReadJson<LayoutProbeEnvelopeRequest>(path);
        var results = new List<object>(request.Cases.Count);
        foreach (var item in request.Cases)
        {
            var operation = item.TryGetProperty("Operation", out var operationElement)
                ? operationElement.GetString()
                : null;
            if (operation?.Equals("ConvertCaptionBounds", StringComparison.OrdinalIgnoreCase) == true)
            {
                var conversion = item.Deserialize<CaptionBoundsProbeRequest>(JsonOptions)
                    ?? throw new JsonException("Empty title-button coordinate request.");
                results.Add(new CaptionBoundsProbeResult(
                    conversion.Name,
                    CodexWindowLocator.ConvertRelativeToScreen(
                        conversion.WindowBounds,
                        conversion.RelativeBounds)));
                continue;
            }

            var layoutCase = item.Deserialize<LayoutProbeCaseRequest>(JsonOptions)
                ?? throw new JsonException("Empty layout probe case.");
            var layoutResult = LayoutProbe.Execute(new LayoutProbeRequest { Cases = [layoutCase] });
            results.Add(layoutResult.Cases[0]);
        }

        return new { Cases = results };
    }

    private static object ExecuteAttachmentProbe(string path)
    {
        var request = ReadJson<AttachmentProbeEnvelopeRequest>(path);
        var results = new List<object>(request.Cases.Count);
        foreach (var item in request.Cases)
        {
            var operation = item.TryGetProperty("Operation", out var operationElement)
                ? operationElement.GetString()
                : null;
            results.Add(operation switch
            {
                "ReferencePoints" => ExecuteReferencePointsProbe(
                    DeserializeAttachmentCase<AttachmentReferencePointsProbeRequest>(item)),
                "SelectReferencePoint" => ExecuteSelectReferencePointProbe(
                    DeserializeAttachmentCase<AttachmentReferencePointProbeRequest>(item)),
                "CaptureResolve" => ExecuteCaptureResolveProbe(
                    DeserializeAttachmentCase<AttachmentCaptureResolveProbeRequest>(item)),
                "SelectTargets" => ExecuteSelectTargetsProbe(
                    DeserializeAttachmentCase<AttachmentTargetSelectionProbeRequest>(item)),
                "CalculateScales" => ExecuteCalculateScalesProbe(
                    DeserializeAttachmentCase<AttachmentScaleProbeRequest>(item)),
                "EditState" => ExecuteEditStateProbe(
                    DeserializeAttachmentCase<AttachmentEditStateProbeRequest>(item)),
                _ => throw new InvalidOperationException($"Unknown manual attachment probe operation: {operation}")
            });
        }

        return new { Cases = results };
    }

    private static T DeserializeAttachmentCase<T>(JsonElement item)
    {
        return item.Deserialize<T>(JsonOptions)
            ?? throw new JsonException("Empty manual attachment probe case.");
    }

    private static object ExecuteReferencePointsProbe(AttachmentReferencePointsProbeRequest request)
    {
        var points = Enum.GetValues<AttachmentReferencePoint>()
            .Select(kind => new
            {
                Kind = kind,
                Point = ManualAttachmentCalculator.ResolveCenter(
                    request.Target,
                    new WindowAttachment(kind, 0d, 0d),
                    request.Dpi)
            })
            .ToArray();
        var rejectsEmptyTarget = ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.SelectReferencePoint(default, Point.Empty);
        });
        var rejectsZeroDpi = ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.Capture(request.Target, Point.Empty, 0);
        }) && ThrowsArgumentOutOfRange(() =>
        {
            _ = ManualAttachmentCalculator.ResolveCenter(
                request.Target,
                new WindowAttachment(AttachmentReferencePoint.TopLeft, 0d, 0d),
                0);
        });
        return new
        {
            request.Name,
            Points = points,
            RejectsEmptyTarget = rejectsEmptyTarget,
            RejectsZeroDpi = rejectsZeroDpi
        };
    }

    private static object ExecuteSelectReferencePointProbe(AttachmentReferencePointProbeRequest request) =>
        new
        {
            request.Name,
            ReferencePoint = ManualAttachmentCalculator.SelectReferencePoint(
                request.Target,
                request.Center)
        };

    private static object ExecuteCaptureResolveProbe(AttachmentCaptureResolveProbeRequest request)
    {
        var attachment = ManualAttachmentCalculator.Capture(
            request.Target,
            request.Center,
            request.Dpi);
        return new
        {
            request.Name,
            Attachment = attachment,
            ResolvedCenter = ManualAttachmentCalculator.ResolveCenter(
                request.Target,
                attachment,
                request.Dpi)
        };
    }

    private static object ExecuteSelectTargetsProbe(AttachmentTargetSelectionProbeRequest request) =>
        new
        {
            request.Name,
            Hits = request.Points
                .Select((point, index) => ManualAttachmentCalculator.SelectTarget(
                    request.Targets,
                    point,
                    index < request.HostSurfaceHits.Count && request.HostSurfaceHits[index]))
                .ToArray()
        };

    private static object ExecuteCalculateScalesProbe(AttachmentScaleProbeRequest request) =>
        new
        {
            request.Name,
            Scales = request.Cases
                .Select(item => ManualAttachmentCalculator.CalculateScale(
                    new Size(item.StartWidth, item.StartHeight),
                    item.StartScale,
                    item.DeltaX,
                    item.DeltaY))
                .ToArray()
        };

    private static object ExecuteEditStateProbe(AttachmentEditStateProbeRequest request)
    {
        var inactive = new ManualPlacementEditState();
        var attachmentApplyThrows = ThrowsInvalidOperation(() => inactive.ApplyAttachment(
            ManualAttachmentRules.DefaultMainAttachment));
        var scaleApplyThrows = ThrowsInvalidOperation(() => inactive.ApplyScale(73));
        var commitThrows = ThrowsInvalidOperation(() => { _ = inactive.Commit(); });
        var cancelThrows = ThrowsInvalidOperation(() => { _ = inactive.Cancel(); });

        var commitState = new ManualPlacementEditState();
        commitState.Begin(request.CommitOriginal);
        commitState.ApplyAttachment(request.CommitAttachment);
        commitState.ApplyScale(request.CommitScale);
        var committed = commitState.Commit();

        var cancelState = new ManualPlacementEditState();
        cancelState.Begin(request.CancelOriginal);
        cancelState.ApplyAttachment(request.CancelAttachment);
        cancelState.ApplyScale(request.CancelScale);
        var cancelled = cancelState.Cancel();

        return new
        {
            request.Name,
            ThrowsBeforeBegin = attachmentApplyThrows && scaleApplyThrows && commitThrows && cancelThrows,
            Committed = committed,
            ActiveAfterCommit = commitState.IsActive,
            Cancelled = cancelled,
            CancelOriginal = request.CancelOriginal,
            ActiveAfterCancel = cancelState.IsActive
        };
    }

    private static bool ThrowsInvalidOperation(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool ThrowsArgumentOutOfRange(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
