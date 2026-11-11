namespace TokenOverlay;

// Prompt-cache facts for the chat on screen, from its session log. Claude logs which cache lifetime each request
// wrote (1 hour for a subscription session's main agent, 5 minutes for sub-agents); Codex logs only the cached share.
// Input counts every input token of the session, cache reads and writes included; Cached is the part read from cache.
// HitOnly: Codex. OpenAI said on 2026-10-20 that signed-in Codex users keep their cache with no expiry, so no lifetime
// is counted; the last request's own hit share (LastCached ÷ LastContext) is shown instead.
// Compacted: the chat was compacted after its last request, so the old context size says nothing about the next
// request, which writes the short summary afresh. While the cache is alive its time left still comes first.
internal sealed record CacheState(long SessionInput, long SessionCached, DateTimeOffset? LastRequest = null,
    int? TtlMinutes = null, long LastContext = 0, bool HitOnly = false, bool Compacted = false, long LastCached = 0);

internal enum CacheLevel { Fresh, Middle, Soon }

// Capsule bar: share of the lifetime left (1 = just written) and its colour band.
internal sealed record CacheBar(double Fill, CacheLevel Level);

internal static class CacheStatus
{
    internal const int SoonMinutes = 15;
    internal const int FreshMinutes = 30;

    public static string? Percent(long? cached, long? input) =>
        input is > 0 && cached is >= 0 ? $"{100.0 * cached.Value / input.Value:0}%" : null;

    // "99.1%", like the worker's 캐시히트 line: one decimal, and never 100% while any token missed.
    public static string? HitPercent(long cached, long input) => input > 0 && cached >= 0
        ? $"{Math.Min(cached < input ? 99.9 : 100, Math.Round(100.0 * cached / input, 1, MidpointRounding.AwayFromZero)):0.0}%" : null;

    private static string? LastHit(CacheState? state) =>
        state is { HitOnly: true } ? HitPercent(state.LastCached, state.LastContext) : null;

    // Minutes until the cache written by the last request lapses. An estimate: the server does not say when it drops it.
    public static double? RemainingMinutes(CacheState? state, DateTimeOffset now) =>
        state is { LastRequest: { } at, TtlMinutes: int ttl } ? (at.AddMinutes(ttl) - now).TotalMinutes : null;

    // Line under the recent-turn list: is the cache still warm, and how much of the session's input came from it.
    public static string? Line(CacheState? state, DateTimeOffset now)
    {
        if (state is null) return null;
        var parts = new List<string>();
        if (LastHit(state) is { } hit) parts.Add($"마지막 요청 캐시히트 {hit}");
        switch (RemainingMinutes(state, now))
        {
            case double left when left > SoonMinutes: parts.Add($"캐시 유지 · {Math.Ceiling(left):0}분 남음"); break;
            case double left when left > 0: parts.Add($"캐시 곧 만료 · {Math.Ceiling(left):0}분 남음"); break;
            case double when state.Compacted: parts.Add("캐시 만료(추정) · 압축됨 · 다시 보내면 요약부터 새로 저장"); break;
            case double:
                parts.Add($"캐시 만료(추정) · 다시 보내면 {OverlayPresentationBuilder.FormatContextCount(state.LastContext)} 전체 재저장");
                break;
        }
        if (state.Compacted && !(RemainingMinutes(state, now) <= 0)) parts.Add("압축됨 · 다음 요청 때 새로 저장");
        if (Percent(state.SessionCached, state.SessionInput) is { } share) parts.Add($"세션 캐시 읽기 {share}");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // Lower left of the capsule: minutes the cache has left, or the rebuild a new message would cost once it lapsed.
    // After a compaction: minutes left while the cache lives; once it lapsed, the lapse first (the rebuild size is unknown).
    // Codex: the last request's hit share.
    public static string? CapsuleNote(CacheState? state, DateTimeOffset now) => state is { HitOnly: true }
        ? LastHit(state) is { } hit ? $"캐시히트 {hit}" : null
        : RemainingMinutes(state, now) switch
    {
        double left when left > 0 => $"캐시 {Math.Ceiling(left):0}분 남음",
        double when state!.Compacted => "캐시 만료 · 압축됨",
        // The count is drawn under the turn's token count; the panel line says it is the rebuild.
        double when state!.LastContext > 0 => $"캐시 만료 {OverlayPresentationBuilder.FormatContextCount(state.LastContext)}",
        _ => null,
    };

    // Bar behind the capsule note while the cache is alive: green above 30 minutes, amber above 15, red after that.
    public static CacheBar? CapsuleBar(CacheState? state, DateTimeOffset now) =>
        RemainingMinutes(state, now) is double left && left > 0 && state!.TtlMinutes is int ttl
            ? new(Math.Clamp(left / ttl, 0, 1), left > FreshMinutes ? CacheLevel.Fresh : left > SoonMinutes ? CacheLevel.Middle : CacheLevel.Soon)
            : null;

    // Drawn in the accent colour: little time left, or already lapsed (compacted or not).
    public static bool CapsuleAlert(CacheState? state, DateTimeOffset now) =>
        RemainingMinutes(state, now) is double left && left <= SoonMinutes;

    public static string? JoinLines(params string?[] lines) =>
        lines.Where(line => !string.IsNullOrEmpty(line)).ToArray() is { Length: > 0 } kept ? string.Join("\n", kept) : null;
}
