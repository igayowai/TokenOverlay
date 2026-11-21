namespace TokenOverlay;

// The Codex capsule's "압축 좋음" notice (2026-11-19, the user's "오버레이가 알려주기"). The overlay cannot start a
// compaction in the Codex app, so it says when one would be cheap, by the rule the Claude mod compacts with: the chat's
// context is at least 300k, its last turn finished with an answer, two minutes have passed with nothing new, and no
// background work (a sub-agent the chat started and has not seen finish) is still going. The user types the compaction.
// Codex's own setting (model_post_turn_compact_threshold_percent = 70, about 407k) is the backstop past this.
internal static class CodexCompactHint
{
    internal const long At = 300_000;
    internal static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);
    internal const string Text = "압축 좋음";

    internal static bool Show(TurnStatistics? turn, long context, int liveSubAgents, DateTimeOffset now) =>
        context >= At && liveSubAgents == 0
        && turn is { Running: false, State: "완료", EndedAt: { } ended } && now - ended >= Quiet;

    // The notice takes the ring note's last line (the compaction point); the model's name above it stays.
    internal static TokenSnapshot Apply(TokenSnapshot snapshot, int liveSubAgents, DateTimeOffset now) =>
        Show(snapshot.Turn, snapshot.ContextUsedTokens, liveSubAgents, now)
            ? snapshot with { RingNote = (ModelNames.Short(snapshot.Turn?.Model) is { } name ? name + "\n" : "") + Text, RingNoteAlert = true }
            : snapshot;
}
