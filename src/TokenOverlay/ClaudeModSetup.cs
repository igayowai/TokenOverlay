using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenOverlay;

// The partner Claude Code mod (src/claude-mod: the cache pings and the compaction before a lapse) ships in the overlay's
// own folder. Switched on from the tray menu, the desktop app's Claude Code registers that folder as a folder
// marketplace and installs the mod from it. Claude Code reads such a mod where it lies ("loads in place"), so an overlay
// update reaches every session started after it (a running one on /reload-plugins). Switching off, or uninstalling the
// overlay, takes both out again. The mod runs inside Claude Code with the user's rights, so it is off until asked for.
internal static class ClaudeModSetup
{
    internal const string Marketplace = "tokenoverlay";
    internal const string Plugin = "tokenoverlay-cache@tokenoverlay";

    internal static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "claude-mod");

    internal enum State { NoClaudeCode, Off, On, OtherFolder }

    // From the Claude settings alone (no Claude Code run): on when the marketplace is this overlay's folder and the mod
    // is enabled; another folder (a development copy, an old install) counts as someone else's.
    internal static State Read(string settingsPath, string folder)
    {
        JsonObject? root;
        try
        {
            root = File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions:
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject : null;
        }
        catch (JsonException) { root = null; }
        var path = root?["extraKnownMarketplaces"]?[Marketplace]?["source"]?["path"] is JsonValue value
            && value.TryGetValue<string>(out var text) ? text : null;
        var enabled = root?["enabledPlugins"]?[Plugin] is JsonValue flag && flag.TryGetValue<bool>(out var on) && on;
        if (path is null || !enabled) return State.Off;
        return SameFolder(path, folder) ? State.On : State.OtherFolder;
    }

    internal static State Status(string? cli) =>
        cli is null ? State.NoClaudeCode : Read(TelemetrySetup.ClaudeSettingsPath, BundledFolder);

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // The steps, each a Claude Code command. Adding the marketplace again from another folder moves it (and the mod
    // installed from it) to this one; removing what is not there is not an error worth stopping for.
    internal static IReadOnlyList<string[]> Steps(bool enabled, string folder) => enabled
        ? [["plugin", "marketplace", "add", folder], ["plugin", "install", Plugin]]
        : [["plugin", "uninstall", Plugin], ["plugin", "marketplace", "remove", Marketplace]];

    // Runs the steps with the given Claude Code; returns what Claude Code said when a step failed, null when all went.
    internal static string? Set(bool enabled, string cli, string? folder = null, int timeoutMs = 60_000)
    {
        folder ??= BundledFolder;
        if (enabled && !File.Exists(Path.Combine(folder, ".claude-plugin", "marketplace.json")))
            return "확장 파일이 오버레이 폴더에 없음";
        foreach (var step in Steps(enabled, folder))
        {
            var (code, output) = Run(cli, step, timeoutMs);
            DiagnosticLog.Info("claude-mod", $"{string.Join(' ', step.Take(3))} → {code}");
            // Switching off what is already gone: "not found" is the state wanted.
            if (code != 0 && !(!enabled && output.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                return output.Length > 0 ? output : $"Claude Code가 {code}(으)로 끝남";
        }
        return null;
    }

    private static (int Code, string Output) Run(string cli, string[] arguments, int timeoutMs)
    {
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Claude Code를 실행하지 못했음");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (-1, "Claude Code가 응답하지 않음");
        }
        var text = (output.Result + "\n" + error.Result).Trim();
        // The last line says what happened ("√ Successfully …" or "× Failed …").
        return (process.ExitCode, text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0) ?? "");
    }

    // Uninstalling the overlay: only a registration of this overlay's own folder is taken out.
    public static void Uninstall()
    {
        if (ClaudeTokenRenewal.FindCli() is not { } cli || Status(cli) != State.On) return;
        if (Set(false, cli, timeoutMs: 20_000) is { } failed) DiagnosticLog.Info("uninstall", "claude mod not removed: " + failed);
    }

    // For checking the switch outside the tray: --claude-mod on|off|status <result.json> [folder].
    public static bool TryRun(string[] args)
    {
        if (args.Length < 3 || args[0] != "--claude-mod") return false;
        var folder = args.Length > 3 ? args[3] : BundledFolder;
        var cli = ClaudeTokenRenewal.FindCli();
        string? failed = null;
        if (cli is null) failed = "Claude Code 없음";
        else if (args[1] is "on" or "off") failed = Set(args[1] == "on", cli, folder);
        var state = cli is null ? State.NoClaudeCode : Read(TelemetrySetup.ClaudeSettingsPath, folder);
        if (failed is not null) Environment.ExitCode = 1;
        File.WriteAllText(args[2], JsonSerializer.Serialize(new { State = state.ToString(), Failed = failed, Cli = cli, Folder = folder }));
        return true;
    }
}
