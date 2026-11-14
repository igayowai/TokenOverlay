using System.Globalization;

namespace TokenOverlay;

internal sealed record OverlayMetric(
    DisplayField Field,
    string CompactLabel,
    string ExpandedLabel,
    string Value,
    bool HasValue)
{
    // Compact form reads "value unit": the last word of the label follows the value.
    public bool LabelAfterValue { get; init; }
}

internal sealed record OverlayPresentation(
    OverlayMetric Primary,
    OverlayMetric Secondary,
    IReadOnlyList<OverlayMetric> ExpandedRows,
    double ContextPercent,
    bool ShowContextProgress,
    string? StatusText)
{
    public OverlayMetric? CompactMiddle { get; init; }
    public HostApp App { get; init; }
    public bool Running { get; init; }
    public string? SubLine { get; init; }
    // A third line under it, drawn the same way ("생성 175.00 TPS").
    public string? SubLine2 { get; init; }
    // Left end of the capsule's lower line (prompt-cache time left); Alert draws it in the accent colour.
    public string? SubLineLeft { get; init; }
    public bool SubLineLeftAlert { get; init; }
    // When set, the left text is drawn inside a bar filled to the cache time left.
    public CacheBar? SubLineLeftBar { get; init; }
    // Ring at the capsule's right end: share of the context limit in use.
    public double? ContextFill { get; init; }
    // Part of the ring: the model in use and the size of its context limit ("Opus 5.5\n600K"), on a low step to the
    // ring's left resting on the capsule; shown and hidden with the ring. Null shows the ring alone.
    public string? RingNote { get; init; }
    // The CPU meter at the left end of the ring's row, which then runs the capsule's full width (the setting); its
    // reading is the form's own, redrawn once a second without a new layout.
    public bool CpuMeter { get; init; }
    // A worker outside Claude (DeepSeek Harness), under a divider: "DeepSeek V4.1 Flash 141.07 TPS" with the name at the
    // left and the rate under Claude's, then its 모델 and 생성 lines right-aligned like Claude's, and its cache hit last.
    public string? WorkerLine { get; init; }
    public string? WorkerSubLine { get; init; }
    public string? WorkerThirdLine { get; init; }
    public string? WorkerCacheLine { get; init; }
    // The worker's API provider routes, one per line, at the left of the rows under its name.
    public string? WorkerProvider { get; init; }
    public IReadOnlyList<HistoryRow> History { get; init; } = [];
    // The table's title and column header (when | tokens | time | rate | model | cache | name). The Ctrl list now fills
    // the table with OpenAI's complimentary tokens instead of the recent turns (OpenAiFreeTokens.cs), under its own words.
    public string HistoryTitle { get; init; } = "최근 턴";
    public IReadOnlyList<string> HistoryHeader { get; init; } = TurnHeader;
    internal static readonly string[] TurnHeader = ["", "소모", "시간", "전체", "모델", "캐시", "모델명"];
    public string? HistoryFooter { get; init; }
    // Rows the recent-turn list takes: its header, the turns and the footer line if there is one.
    // The footer may hold several lines, one panel row each.
    public IReadOnlyList<string> HistoryFooterLines =>
        HistoryFooter is { Length: > 0 } footer ? footer.Split('\n') : [];
    // Top to bottom: OpenAI, then OpenCode Go (user, 2026-11-12: "위에서부터 openai opencode 엔트로픽"; Anthropic then
    // went to the reset line instead of a table). The
    // first table's title is the panel's; each one under another gets a title row of its own.
    // OpenCode Go's three windows, a table of the same columns per account (OpenCodeGo.cs), top first: the second
    // account above the first (user, 2026-11-14: "새 api 추가할테니까 ctrl 표 위에다가 그 계정도 붙여줘"), the
    // third above the second (2026-11-15). Empty without a key.
    public IReadOnlyList<FreeTokenTable> GoTables { get; init; } = [];
    // Go table i gets a title row of its own when a table is above it; the first one under nothing takes the panel's.
    public bool GoTitle(int index) => index > 0 || History.Count > 0;
    // The row of Go table i's column header.
    public int GoTop(int index)
    {
        var row = TurnRows;
        for (var i = 0; i < index; i++) row += (GoTitle(i) ? 1 : 0) + GoTables[i].Rows.Count + 1;
        return row + (GoTitle(index) ? 1 : 0);
    }
    public int TableRows => GoTables.Count == 0 ? TurnRows : GoTop(GoTables.Count - 1) + GoTables[^1].Rows.Count + 1;
    public bool HasTable => GoTables.Count > 0 || History.Count > 0;
    // The Anthropic Console's dollars left ("$188.03"), drawn as "API: $188.03" in front of the week's devices
    // (AnthropicCredits.cs).
    // Null without the login cookie.
    public string? CreditLeft { get; init; }
    // The tables shown, top first, with their column headers.
    public IReadOnlyList<(IReadOnlyList<string> Header, IReadOnlyList<HistoryRow> Rows)> Tables =>
        [.. History.Count > 0 ? [(HistoryHeader, History)] : Array.Empty<(IReadOnlyList<string>, IReadOnlyList<HistoryRow>)>(),
         .. GoTables.Select(g => (g.Header ?? HistoryHeader, g.Rows))];
    // A weekly line takes a second row for its bar, the reset line one for its time line.
    // Without turns (no chat known yet) there is no column header: the list holds only the footer.
    public int HistoryPanelRows => SessionsTopRow + SessionRows
        + HistoryFooterLines.Count + HistoryFooterLines.Count(line => WeeklySplit.ParseLine(line) is not null || WeeklySplit.ParseReset(line) is not null);
    public bool HasHistoryPanel => Sessions is not null || HasTable || HistoryFooterLines.Count > 0;
    // Claude in front: the list shows the large Claude sessions whose cache is alive, each with a 핑 button (an empty
    // list says so in one line). Null leaves them out.
    public IReadOnlyList<SessionRow>? Sessions { get; init; }
    // The recent turns come first, the sessions under them (user, 2026-11-11: "사용량이랑 캐시세션 위에다가 최근턴을 붙여줘").
    // The turns take their column header and one row each; with turns above them the sessions get a title row of their
    // own, since the panel's header then names the turns.
    public int TurnRows => History.Count > 0 ? History.Count + 1 : 0;
    public bool SessionTitle => Sessions is not null && HasTable;
    public int SessionsTopRow => TableRows + (SessionTitle ? 1 : 0);
    public int SessionRows => Sessions is { } sessions ? Math.Max(1, sessions.Count) : 0;
}

// One row of the Ctrl list's sessions: the name, time left and size, what the mod will do or did, the 유지 switch
// (on, and whether it can be switched), the 핑 button and the 압축 button (pressed once and waiting: Arming).
internal sealed record SessionRow(string Id, string Name, string Left, bool Soon, string State, bool CanPing,
    bool Keeping = false, bool CanKeep = false, bool CanCompact = false, bool Arming = false);

internal static class OverlayPresentationBuilder
{
    private const string NoValue = "—";

    public static OverlayPresentation CreateWaiting(
        string statusText,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ValidateHighlightedFields(primaryField, secondaryField);
        return new OverlayPresentation(
            CreateWaitingMetric(primaryField),
            CreateWaitingMetric(secondaryField),
            CreateExpandedRows(visibleFields, primaryField, secondaryField, CreateWaitingMetric),
            0,
            false,
            SanitizeSingleLine(statusText));
    }

    public static OverlayPresentation Create(
        TokenSnapshot snapshot,
        DisplayField primaryField,
        DisplayField secondaryField,
        DisplayField visibleFields)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHighlightedFields(primaryField, secondaryField);
        var contextPercent = Math.Clamp(snapshot.ContextPercent, 0, 100);
        return new OverlayPresentation(
            CreateMetric(snapshot, primaryField, contextPercent),
            CreateMetric(snapshot, secondaryField, contextPercent),
            CreateExpandedRows(
                visibleFields,
                primaryField,
                secondaryField,
                field => CreateMetric(snapshot, field, contextPercent)),
            contextPercent,
            (visibleFields & DisplayField.ContextPercent) != 0,
            null);
    }

    // Tokens spent, rounded half away from zero (user, 2026-11-11: "K말고 억으로", then "0.0억이나 0.00억은 안 와닿네,
    // 5999만 이렇게 가다가 1억 넘으면 1.N억"): below 1억 in whole 만 ("207만", "4826만"), from 1억 with one decimal
    // ("1.5억"). The step is chosen on the rounded value, so 9999.5만 reads "1.0억". Under 1만 the count itself shows
    // ("4,800"), since "0만" would hide it.
    public static string FormatTokenCount(long value)
    {
        if (value < 10_000) return value.ToString("N0", CultureInfo.InvariantCulture);
        var man = Math.Round(value / 10_000d, MidpointRounding.AwayFromZero);
        return man < 10_000
            ? man.ToString("0", CultureInfo.InvariantCulture) + "만"
            : Math.Round(value / 100_000_000d, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "억";
    }

    // A context's size stays in k and M ("109.0k", "1.20M"): in 억 every context would read 0.00억 or 0.01억.
    public static string FormatContextCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    // Whole seconds, rounded; a minute or more reads "1분 2초", an hour or more "1시간 1분" (the seconds dropped).
    public static string FormatDuration(double seconds)
    {
        var total = (long)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        var (hours, minutes, rest) = (total / 3600, total / 60 % 60, total % 60);
        return hours > 0 ? $"{hours}시간 {minutes}분" : minutes > 0 ? $"{minutes}분 {rest}초" : $"{rest}초";
    }

    public static string ShortThreadId(string threadId, int maximumLength = 12)
    {
        var singleLineThreadId = SanitizeSingleLine(threadId);
        if (singleLineThreadId.Length <= maximumLength)
        {
            return singleLineThreadId;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(4, maximumLength - 1);
        var suffixLength = Math.Min(6, maximumLength - prefixLength - 1);
        return string.Concat(
            singleLineThreadId.AsSpan(0, prefixLength),
            "…",
            singleLineThreadId.AsSpan(singleLineThreadId.Length - suffixLength, suffixLength));
    }

    private static void ValidateHighlightedFields(DisplayField primaryField, DisplayField secondaryField)
    {
        if (!DisplayFieldRules.IsSingleSupported(primaryField)
            || !DisplayFieldRules.IsSingleSupported(secondaryField))
        {
            throw new ArgumentException("Collapsed metrics must each be one supported field.");
        }
    }

    private static IReadOnlyList<OverlayMetric> CreateExpandedRows(
        DisplayField visibleFields,
        DisplayField primaryField,
        DisplayField secondaryField,
        Func<DisplayField, OverlayMetric> createMetric)
    {
        return DisplayFieldRules.Ordered
            .Where(field => (visibleFields & field) != 0)
            .Where(field => field != primaryField && field != secondaryField)
            .Select(createMetric)
            .ToArray();
    }

    private static OverlayMetric CreateWaitingMetric(DisplayField field)
    {
        var labels = GetLabels(field);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, NoValue, false);
    }

    private static OverlayMetric CreateMetric(
        TokenSnapshot snapshot,
        DisplayField field,
        double contextPercent)
    {
        var labels = GetLabels(field);
        var value = field switch
        {
            DisplayField.Total => FormatTokenCount(snapshot.TotalTokens),
            DisplayField.Input => FormatTokenCount(snapshot.InputTokens),
            DisplayField.Output => FormatTokenCount(snapshot.OutputTokens),
            DisplayField.CacheHit => FormatTokenCount(snapshot.CachedInputTokens),
            DisplayField.CacheHitRate => $"{snapshot.CacheHitPercent:0}%",
            DisplayField.CacheMiss => FormatTokenCount(snapshot.UncachedInputTokens),
            DisplayField.Context => $"{FormatContextCount(snapshot.ContextUsedTokens)} / {FormatContextCount(snapshot.ContextWindowTokens)}",
            DisplayField.ContextPercent => $"{contextPercent:0}%",
            DisplayField.Reasoning => FormatTokenCount(snapshot.ReasoningOutputTokens),
            DisplayField.Thread => ShortThreadId(snapshot.ThreadId),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
        var hasValue = field != DisplayField.Thread || !string.IsNullOrWhiteSpace(snapshot.ThreadId);
        return new OverlayMetric(field, labels.Compact, labels.Expanded, value, hasValue);
    }

    private static (string Compact, string Expanded) GetLabels(DisplayField field)
    {
        return field switch
        {
            DisplayField.Total => ("총", "총 토큰"),
            DisplayField.Input => ("입력", "입력"),
            DisplayField.Output => ("출력", "출력"),
            DisplayField.CacheHit => ("캐시", "캐시 적중"),
            DisplayField.CacheHitRate => ("적중률", "캐시 적중률"),
            DisplayField.CacheMiss => ("미적중", "캐시 미적중"),
            DisplayField.Context => ("컨텍스트", "컨텍스트 사용량"),
            DisplayField.ContextPercent => ("컨텍스트", "컨텍스트 사용률"),
            DisplayField.Reasoning => ("추론", "추론 출력"),
            DisplayField.Thread => ("채팅", "채팅"),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unsupported display field.")
        };
    }

    private static string SanitizeSingleLine(string value)
    {
        return value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
    }
}

internal sealed class PresentationProbeRequest
{
    public List<PresentationProbeCase> Cases { get; set; } = new();
}

internal sealed class PresentationProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public TokenSnapshot? Snapshot { get; set; }
    public int? PrimaryField { get; set; }
    public int? SecondaryField { get; set; }
    public int? VisibleFields { get; set; }
    public string? StatusText { get; set; }
}

internal sealed record PresentationProbeCaseResult(string Name, OverlayPresentation Presentation);

internal sealed record PresentationProbeResult(IReadOnlyList<PresentationProbeCaseResult> Cases);

internal static class PresentationProbe
{
    public static PresentationProbeResult Execute(PresentationProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<PresentationProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var primaryField = RequireField(probeCase.PrimaryField, nameof(probeCase.PrimaryField));
            var secondaryField = RequireField(probeCase.SecondaryField, nameof(probeCase.SecondaryField));
            var visibleFields = (DisplayField)(probeCase.VisibleFields ?? 0);
            var presentation = probeCase.Operation switch
            {
                "Create" => OverlayPresentationBuilder.Create(
                    probeCase.Snapshot ?? throw new ArgumentException("Create needs Snapshot.", nameof(probeCase)),
                    primaryField,
                    secondaryField,
                    visibleFields),
                "Waiting" => OverlayPresentationBuilder.CreateWaiting(
                    probeCase.StatusText ?? string.Empty,
                    primaryField,
                    secondaryField,
                    visibleFields),
                _ => throw new ArgumentException($"Unsupported presentation probe operation: {probeCase.Operation}", nameof(probeCase))
            };
            results.Add(new PresentationProbeCaseResult(probeCase.Name, presentation));
        }

        return new PresentationProbeResult(results);
    }

    private static DisplayField RequireField(int? value, string parameterName)
    {
        if (!value.HasValue)
        {
            throw new ArgumentException("A presentation probe needs a field.", parameterName);
        }

        return (DisplayField)value.Value;
    }
}
