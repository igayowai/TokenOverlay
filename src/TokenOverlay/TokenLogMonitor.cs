using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace TokenOverlay;

internal sealed record TokenSnapshot(
    string ThreadId,
    string LogPath,
    long TotalTokens,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long ContextUsedTokens,
    long ContextWindowTokens,
    DateTime UpdatedAtUtc)
{
    public TurnStatistics? Turn { get; init; }
    public string? ThreadTitle { get; init; }
    public HostApp App { get; init; } = HostApp.Codex;
    public TurnHistory? History { get; init; }
    // Extra line under the recent-turn list (Claude: this PC's share of the weekly limit).
    public string? HistoryFooter { get; init; }
    // Capsule lower-left: the chat's prompt-cache time left or its lapse (Claude only); Alert when little or none is left.
    public string? CacheNote { get; init; }
    public bool CacheAlert { get; init; }
    public CacheBar? CacheBar { get; init; }
    // Capsule ring: share of the context limit in use (1 = full); none when unknown.
    public double? ContextFill { get; init; }
    // Under the ring: the model and its context limit ("Opus 5.5\n600K").
    public string? RingNote { get; init; }
    // Two more capsule rows (Claude): the latest DeepSeek Harness task, while it is recent.
    public DeepSeekTurn? DeepSeek { get; init; }
    // When this Claude session's tool calls launched the DeepSeek worker: only its own tasks get the rows above.
    public IReadOnlyList<DateTimeOffset> WorkerCalls { get; init; } = [];

    public double ContextPercent => ContextWindowTokens <= 0
        ? 0
        : Math.Clamp(ContextUsedTokens * 100d / ContextWindowTokens, 0, 100);

    public double CacheHitPercent => InputTokens <= 0
        ? 0d
        : Math.Clamp((double)CachedInputTokens * 100d / InputTokens, 0d, 100d);

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);
}

internal sealed class TokenLogMonitor : IDisposable
{
    private const int TailBytes = 4 * 1024 * 1024;
    private const int HistoricalOverlapBytes = 256 * 1024;
    private readonly string _sessionRoot;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _changedPaths = new();
    private readonly ConcurrentDictionary<string, bool> _rootSessionCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeLogPath;
    private DateTime _activeWriteUtc;
    private long _activeLength;
    private DateTime _lastFullScanUtc = DateTime.MinValue;
    private TokenSnapshot? _lastSnapshot;
    private string? _selectedThreadId;

    public long ActiveSessionVersion { get; private set; }

    public string? ActiveThreadId => _selectedThreadId;

    public string? PreferredThreadId { get; set; }
    private bool _threadFileChanged;

    public TokenLogMonitor(string? sessionRoot = null)
    {
        _sessionRoot = sessionRoot ?? SessionPathResolver.Resolve();
        StartWatching();
    }

    // Also called from Poll: Codex creates the sessions folder on its first chat, possibly after the overlay started.
    private void StartWatching()
    {
        if (_watcher is not null || !Directory.Exists(_sessionRoot))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_sessionRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnLogChanged;
        _watcher.Created += OnLogChanged;
        _watcher.Renamed += (_, eventArgs) => _changedPaths.Enqueue(eventArgs.FullPath);
    }

    public bool PinActiveSession { get; set; }

    // For a bug report: whether the last connected chat's log was found and why it was or was not used.
    // The first line's app name, source and version, sizes and timings only; never the chat's content.
    internal string Describe(string? threadId)
    {
        var text = new StringBuilder();
        text.AppendLine($"codex home {(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CODEX_HOME")) ? "default" : "set")}"
            + $" · sessions folder {(Directory.Exists(_sessionRoot) ? "found" : "missing")}");
        if (string.IsNullOrWhiteSpace(threadId) || !Directory.Exists(_sessionRoot))
        {
            text.AppendLine("codex last connected chat: none");
            return text.ToString();
        }
        try
        {
            var files = Directory.EnumerateFiles(_sessionRoot, $"*{threadId}*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(SafeGetLastWriteUtc).Take(5).ToArray();
            text.AppendLine($"codex last connected chat {OverlayPresentationBuilder.ShortThreadId(threadId)} · log files {files.Length}");
            foreach (var file in files)
            {
                string? originator = null, source = null, version = null;
                var firstLine = 0L;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024))
                {
                    var line = reader.ReadLine();
                    firstLine = line?.Length ?? 0;
                    try
                    {
                        using var document = JsonDocument.Parse(line ?? "");
                        if (document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                        {
                            string? Field(string name) => payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                            originator = Field("originator"); source = Field("source"); version = Field("cli_version");
                        }
                    }
                    catch (JsonException) { originator = "(first line not JSON)"; }
                }
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var found = TryReadLatestTokenSnapshot(file, SafeGetLastWriteUtc(file)) is not null;
                text.AppendLine($"  log {new FileInfo(file).Length / 1048576.0:0.0} MB · written {SafeGetLastWriteUtc(file).ToLocalTime():MM-dd HH:mm}"
                    + $" · first line {firstLine} chars · app {originator ?? "none"} · source {source ?? "none"} · version {version ?? "none"}"
                    + $" · used {(IsRootDesktopSession(file) ? "yes" : "no")} · token count {(found ? "found" : "missing")} in {clock.Elapsed.TotalSeconds:0.0} s");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            text.AppendLine("codex log lookup failed: " + e.GetType().Name);
        }
        return text.ToString();
    }

    public TokenSnapshot? Poll(bool forceFullScan = false)
    {
        if (!Directory.Exists(_sessionRoot))
        {
            return null;
        }

        StartWatching();
        var usePreferredThread = !PinActiveSession && !string.IsNullOrWhiteSpace(PreferredThreadId);
        ProcessChangedPaths(allowAutomaticSwitch: !usePreferredThread);

        if (usePreferredThread)
        {
            SelectPreferredRootSession(PreferredThreadId!);
        }
        else if (forceFullScan || _activeLogPath is null || DateTime.UtcNow - _lastFullScanUtc > TimeSpan.FromSeconds(20))
        {
            SelectNewestRootSession();
        }

        if (_activeLogPath is null || !File.Exists(_activeLogPath))
        {
            return _lastSnapshot;
        }

        DateTime writeUtc;
        long length;
        try
        {
            // The size counts too: while Codex keeps a log open, its write time can stay at the first write as the file grows.
            var info = new FileInfo(_activeLogPath);
            writeUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (IOException)
        {
            return _lastSnapshot;
        }

        if (_lastSnapshot is not null && writeUtc == _activeWriteUtc && length == _activeLength)
        {
            return _lastSnapshot;
        }

        var parsed = TryReadLatestTokenSnapshot(_activeLogPath, writeUtc);
        if (parsed is not null)
        {
            // The file version is committed only after a full parse, so a half-written JSON line is never stuck on.
            _activeWriteUtc = writeUtc;
            _activeLength = length;
            // A continuation file's name ends in a different id; the snapshot still belongs to the chosen thread.
            _lastSnapshot = _selectedThreadId is { Length: > 0 } selected ? parsed with { ThreadId = selected } : parsed;
        }

        return _lastSnapshot;
    }

    private void OnLogChanged(object sender, FileSystemEventArgs eventArgs)
    {
        _changedPaths.Enqueue(eventArgs.FullPath);
    }

    private void ProcessChangedPaths(bool allowAutomaticSwitch)
    {
        var newestPath = _activeLogPath;
        var newestWriteUtc = _activeLogPath is null ? DateTime.MinValue : SafeGetLastWriteUtc(_activeLogPath);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (_changedPaths.TryDequeue(out var path))
        {
            if (!allowAutomaticSwitch)
            {
                // Another file of the chosen thread was written (its continuation): choose again among them.
                if (PreferredThreadId is { Length: > 0 } wanted
                    && !path.Equals(_activeLogPath, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(path).Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    _threadFileChanged = true;
                }
                continue;
            }

            if (PinActiveSession
                && _activeLogPath is not null
                && !path.Equals(_activeLogPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!visited.Add(path) || !File.Exists(path) || !IsRootDesktopSession(path))
            {
                continue;
            }

            var writeUtc = SafeGetLastWriteUtc(path);
            if (writeUtc >= newestWriteUtc)
            {
                newestPath = path;
                newestWriteUtc = writeUtc;
            }
        }

        if (newestPath is not null && !newestPath.Equals(_activeLogPath, StringComparison.OrdinalIgnoreCase))
        {
            SwitchActiveLog(newestPath);
        }
    }

    private void SelectPreferredRootSession(string threadId)
    {
        if (_selectedThreadId?.Equals(threadId, StringComparison.OrdinalIgnoreCase) == true
            && _activeLogPath is not null
            && File.Exists(_activeLogPath)
            && !_threadFileChanged)
        {
            return;
        }
        _threadFileChanged = false;

        try
        {
            // A chat that was continued later is written to a second file named <thread id>_<other id>.jsonl,
            // so every file carrying the thread id is a candidate and the most recently written one is current.
            var searchPattern = $"*{threadId}*.jsonl";
            var candidate = Directory.EnumerateFiles(_sessionRoot, searchPattern, SearchOption.AllDirectories)
                .Where(IsRootDesktopSession)
                .OrderByDescending(SafeGetLastWriteUtc)
                .FirstOrDefault();

            SwitchActiveLog(candidate, threadId);
        }
        catch (IOException)
        {
            SwitchActiveLog(null, threadId);
        }
        catch (UnauthorizedAccessException)
        {
            SwitchActiveLog(null, threadId);
        }
        catch (ArgumentException)
        {
            // IPC chat ids should be UUIDs; anything else only shows the waiting state.
            SwitchActiveLog(null, threadId);
        }
    }

    private void SelectNewestRootSession()
    {
        _lastFullScanUtc = DateTime.UtcNow;
        // Every log ever seen is remembered; past a few thousand the memory is dropped and refilled as needed
        // (a full scan only reads the first line of the newest few files).
        if (_rootSessionCache.Count > 4096)
        {
            _rootSessionCache.Clear();
        }

        if (PinActiveSession && _activeLogPath is not null && File.Exists(_activeLogPath))
        {
            return;
        }

        try
        {
            var candidates = Directory.EnumerateFiles(_sessionRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new { Path = path, WriteUtc = SafeGetLastWriteUtc(path) })
                .OrderByDescending(item => item.WriteUtc);

            foreach (var candidate in candidates)
            {
                if (!IsRootDesktopSession(candidate.Path))
                {
                    continue;
                }

                if (!_activeLogPath?.Equals(candidate.Path, StringComparison.OrdinalIgnoreCase) ?? true)
                {
                    SwitchActiveLog(candidate.Path, ExtractThreadId(candidate.Path));
                }
                return;
            }
        }
        catch (IOException)
        {
            // While Codex rotates its logs, the next poll tries again.
        }
        catch (UnauthorizedAccessException)
        {
            // When an old folder cannot be read, the last good result is kept.
        }
    }

    private bool IsRootDesktopSession(string path)
    {
        if (_rootSessionCache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                // Created can fire before Codex has written the first line: a passing failure must not be cached for good.
                return false;
            }

            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta")
            {
                _rootSessionCache[path] = false;
                return false;
            }

            if (!root.TryGetProperty("payload", out var payload))
            {
                _rootSessionCache[path] = false;
                return false;
            }

            // Desktop builds name themselves differently ("Codex Desktop", "codex_work_desktop"); the CLI and editor plugins never say desktop.
            if (!payload.TryGetProperty("originator", out var originator)
                || originator.ValueKind != JsonValueKind.String
                || originator.GetString()?.Contains("desktop", StringComparison.OrdinalIgnoreCase) != true)
            {
                _rootSessionCache[path] = false;
                return false;
            }

            // A desktop root session's first line always has source="vscode"; sub-agents later replay the parent session,
            // so only the first physical line is checked, by exact string match, never the whole file.
            var isRoot = payload.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.String
                && string.Equals(source.GetString(), "vscode", StringComparison.OrdinalIgnoreCase);
            _rootSessionCache[path] = isRoot;
            return isRoot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The first line may still be being written; the failure is not cached, the next Changed or full scan retries.
            return false;
        }
    }

    private void SwitchActiveLog(string? path, string? threadId = null)
    {
        threadId ??= path is null ? null : ExtractThreadId(path);
        if (string.Equals(_activeLogPath, path, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_selectedThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeLogPath = path;
        _selectedThreadId = threadId;
        _activeWriteUtc = DateTime.MinValue;
        _activeLength = 0;
        _lastSnapshot = null;
        ActiveSessionVersion++;
    }

    private static TokenSnapshot? TryReadLatestTokenSnapshot(string path, DateTime writeUtc)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var blockEnd = stream.Length;

            while (blockEnd > 0)
            {
                var blockStart = Math.Max(0, blockEnd - TailBytes);
                var bytesToRead = (int)(blockEnd - blockStart);
                var buffer = new byte[bytesToRead];
                stream.Seek(blockStart, SeekOrigin.Begin);
                var offset = 0;
                while (offset < bytesToRead)
                {
                    var read = stream.Read(buffer, offset, bytesToRead - offset);
                    if (read == 0)
                    {
                        break;
                    }
                    offset += read;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, offset);
                if (blockStart > 0)
                {
                    // This chunk may start mid-line; the next chunk's overlap completes that line.
                    var firstNewLine = text.IndexOf('\n');
                    text = firstNewLine >= 0 ? text[(firstNewLine + 1)..] : string.Empty;
                }

                var parsed = TryParseLatestTokenSnapshot(text, path, writeUtc);
                if (parsed is not null)
                {
                    return parsed;
                }

                if (blockStart == 0)
                {
                    break;
                }
                blockEnd = Math.Min(stream.Length, blockStart + HistoricalOverlapBytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // The log may be half written; the last frame stays and the next update parses again.
        }

        return null;
    }

    private static TokenSnapshot? TryParseLatestTokenSnapshot(string text, string path, DateTime writeUtc)
    {
        var lines = text.Split('\n');

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || !line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var eventType)
                    || eventType.ValueKind != JsonValueKind.String
                    || eventType.GetString() != "event_msg")
                {
                    continue;
                }

                if (!root.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object
                    || !payload.TryGetProperty("type", out var payloadType)
                    || payloadType.ValueKind != JsonValueKind.String
                    || payloadType.GetString() != "token_count"
                    || !payload.TryGetProperty("info", out var info)
                    || info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!info.TryGetProperty("total_token_usage", out var total)
                    || total.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("last_token_usage", out var last)
                    || last.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var threadId = ExtractThreadId(path);
                return new TokenSnapshot(
                    threadId,
                    path,
                    GetLong(total, "total_tokens"),
                    GetLong(total, "input_tokens"),
                    GetLong(total, "cached_input_tokens"),
                    GetLong(total, "output_tokens"),
                    GetLong(total, "reasoning_output_tokens"),
                    GetLong(last, "total_tokens"),
                    GetLong(info, "model_context_window"),
                    writeUtc);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // Codex may be appending the last line; keep looking for the previous complete snapshot.
            }
        }

        return null;
    }

    private static long GetLong(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : 0;
    }

    private static string ExtractThreadId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.Length >= 36 ? fileName[^36..] : fileName;
    }

    private static DateTime SafeGetLastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
