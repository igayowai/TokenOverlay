using System.Text.Json;

namespace TokenOverlay;

// Capsule ring: how full the chat's context is. Claude: the last request's context against the session's auto-compact
// window. A session settles it when it starts, so the one its mod reports comes first; without the mod, the window set
// for that model in Claude Code's settings now (modelSettings.<model>.autoCompactWindow), else the model's whole window.
// Codex: its log's own context window.
internal static class ContextUsage
{
    // Claude 5 models' context window, used when no auto-compact window is set.
    internal const long DefaultClaudeWindow = 1_000_000;
    internal const double MiddleFrom = 0.5;
    internal const double SoonFrom = 0.8;

    private static (string Path, DateTime Written, Dictionary<string, long> Windows)? _cached;

    public static CacheLevel Level(double fill) => fill >= SoonFrom ? CacheLevel.Soon : fill >= MiddleFrom ? CacheLevel.Middle : CacheLevel.Fresh;

    public static double? ClaudeFill(long context, string? model, long? window = null, string? settingsPath = null)
    {
        if (context <= 0) return null;
        return (double)context / (window ?? ClaudeLimit(model, settingsPath ?? TelemetrySetup.ClaudeSettingsPath));
    }

    // The lines under the ring: the model's short name (left out when unknown) and the limit the ring is measured
    // against, in thousands below a million and in millions from there ("Opus 5.5\n600K", "1M"). Null without a limit.
    public static string? Note(string? model, long limit) =>
        limit <= 0 ? null : (ModelNames.Short(model) is { } name ? name + "\n" : "") + Limit(limit);

    private static string Limit(long limit) => Math.Round(limit / 1000.0) is var thousands && thousands >= 1000
        ? $"{thousands / 1000:0.#}M" : $"{thousands:0}K";

    public static string? ClaudeNote(string? model, long? window = null, string? settingsPath = null) =>
        Note(model, window ?? ClaudeLimit(model, settingsPath ?? TelemetrySetup.ClaudeSettingsPath));

    public static double? CodexFill(TokenSnapshot snapshot) =>
        snapshot.ContextWindowTokens > 0 && snapshot.ContextUsedTokens > 0
            ? (double)snapshot.ContextUsedTokens / snapshot.ContextWindowTokens : null;

    internal static long ClaudeLimit(string? model, string settingsPath)
    {
        if (string.IsNullOrEmpty(model)) return DefaultClaudeWindow;
        var windows = Windows(settingsPath);
        // A model id may carry a suffix such as "[1m]" in the settings key.
        if (windows.TryGetValue(model, out var limit)) return limit;
        foreach (var (key, value) in windows)
            if (key.StartsWith(model + "[", StringComparison.Ordinal)) return value;
        return DefaultClaudeWindow;
    }

    private static Dictionary<string, long> Windows(string path)
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (_cached is { } c && c.Path == path && c.Written == written) return c.Windows;
            var windows = new Dictionary<string, long>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("modelSettings", out var models) && models.ValueKind == JsonValueKind.Object)
                    foreach (var model in models.EnumerateObject())
                        if (model.Value.ValueKind == JsonValueKind.Object
                            && model.Value.TryGetProperty("autoCompactWindow", out var window)
                            && window.ValueKind == JsonValueKind.Number && window.TryGetInt64(out var tokens) && tokens > 0)
                            windows[model.Name] = tokens;
            }
            _cached = (path, written, windows);
            return windows;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return _cached is { } c && c.Path == path ? c.Windows : new();
        }
    }
}
