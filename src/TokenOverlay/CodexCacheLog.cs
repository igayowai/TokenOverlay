using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

// One Codex model request as its session log counted it: when, how long after the request before it, and how much of
// its input was read from the cache. No conversation text.
internal sealed record CodexRequest(DateTimeOffset At, string? Session, string? Model, double? GapMinutes,
    long PreviousInput, long Input, long Cached);

// codex-cache.jsonl in the overlay's folder: every request of the Codex chats the overlay read, one line each, so
// whether a chat resumed after hours still hits the cache can be judged from this file alone. A request already
// written (same chat, not newer than its last line) is skipped, so reading a chat again adds nothing. Past 16 MB the
// file is moved to codex-cache.old.jsonl and a new one begun.
internal static class CodexCacheLog
{
    private const long SizeLimit = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static Dictionary<string, DateTimeOffset>? _written;
    private static string? _folderOverride;

    // Tests write somewhere else.
    internal static string? UseFolder(string? folder) { lock (Gate) { var previous = _folderOverride; _folderOverride = folder; _written = null; return previous; } }
    internal static string PathNow => Path.Combine(_folderOverride ?? AppPaths.DataFolder, "codex-cache.jsonl");

    public static void Append(IReadOnlyList<CodexRequest> requests)
    {
        if (requests.Count == 0) return;
        try
        {
            lock (Gate)
            {
                var path = PathNow;
                var written = _written ??= Load(path);
                var text = new StringBuilder();
                foreach (var request in requests)
                {
                    var key = request.Session ?? "";
                    if (written.TryGetValue(key, out var last) && request.At <= last) continue;
                    written[key] = request.At;
                    text.Append(Line(request)).Append('\n');
                }
                if (text.Length == 0) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= SizeLimit)
                    File.Move(path, Path.ChangeExtension(path, ".old.jsonl"), overwrite: true);
                File.AppendAllText(path, text.ToString(), new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Error("codex-cache", e.Message); }
    }

    internal static string Line(CodexRequest r) => JsonSerializer.Serialize(new
    {
        at = r.At.ToString("o", CultureInfo.InvariantCulture),
        session = r.Session,
        model = r.Model,
        gapMin = r.GapMinutes is double gap ? Math.Round(gap, 1) : (double?)null,
        prevInput = r.PreviousInput,
        input = r.Input,
        cached = r.Cached,
        hit = r.Input > 0 ? Math.Round(100.0 * r.Cached / r.Input, 1) : (double?)null,
    });

    // The newest request already written for each chat.
    private static Dictionary<string, DateTimeOffset> Load(string path)
    {
        var written = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!File.Exists(path)) return written;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("at", out var at) || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) continue;
                var key = root.TryGetProperty("session", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
                if (!written.TryGetValue(key, out var last) || time > last) written[key] = time;
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        }
        return written;
    }
}
