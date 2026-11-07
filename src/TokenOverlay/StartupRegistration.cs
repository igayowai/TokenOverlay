using Microsoft.Win32;
using System.Text.Json;

namespace TokenOverlay;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenOverlay";

    public static string Command
    {
        get
        {
            var path = Environment.ProcessPath ?? throw new IOException("실행파일 경로를 확인할 수 없음.");
            if (!Path.GetFileName(path).Equals("TokenOverlay.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("TokenOverlay.exe에서 자동 실행을 설정해야 함.");
            return $"\"{path}\" --watch-codex";
        }
    }

    // Auto-start was on for a copy somewhere else (the program moved, e.g. into its installed folder):
    // point it at this copy. Nothing changes when auto-start was off.
    public static bool Repoint()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key?.GetValue(ValueName) is not string current || current.Length == 0 || IsEnabled()) return false;
        SetEnabled(true);
        return true;
    }

    // Called by the uninstaller (no window, short time limit). Auto-start is removed only when it points at this
    // copy; each part is tried on its own so one failure does not leave the others behind.
    public static void Uninstall()
    {
        try { if (IsEnabled()) SetEnabled(false); }
        catch (Exception e) { DiagnosticLog.Error("uninstall", e); }
        try { TelemetrySetup.SetEnabled(false); }
        catch (Exception e) { DiagnosticLog.Error("uninstall", e); }
        // The Claude Code mod is read from this install's folder, which goes with it.
        try { ClaudeModSetup.Uninstall(); }
        catch (Exception e) { DiagnosticLog.Error("uninstall", e); }
        DiagnosticLog.Info("uninstall", "auto-start, telemetry links and the Claude Code mod removed");
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("자동 실행 설정을 열 수 없음.");
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
        if (IsEnabled() != enabled) throw new IOException("자동 실행 설정을 저장하지 못했음.");
        // The Run value stays the switch (and the fallback); the logon task only starts the watcher sooner,
        // because Windows works through the Run list slowly after sign-in.
        if (enabled) RegisterLogonTask(); else RemoveLogonTask();
        if (enabled) CodexLaunchWatcher.EnsureRunning();
    }

    public static bool LogonTaskRegistered()
    {
        var (code, output, _) = Schtasks("/Query", "/TN", ValueName, "/XML");
        return code == 0 && output.Contains(System.Security.SecurityElement.Escape(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase);
    }

    // A failed registration leaves the old task in place (its path still matches, so the query alone says
    // "registered"), so schtasks' own answer goes to the log a bug report collects.
    public static bool RegisterLogonTask()
    {
        var file = Path.Combine(Path.GetTempPath(), $"TokenOverlay-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, LogonTaskXml(Environment.ProcessPath!,
                System.Security.Principal.WindowsIdentity.GetCurrent().Name), System.Text.Encoding.Unicode);
            var (code, output, error) = Schtasks("/Create", "/TN", ValueName, "/XML", file, "/F");
            if (code != 0)
            {
                DiagnosticLog.Error("startup", $"logon task not registered: schtasks exit {code}: {(error + " " + output).Trim()}");
                return false;
            }
            if (LogonTaskRegistered()) return true;
            DiagnosticLog.Error("startup", "logon task registered but not found for this path");
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Error("startup", e);
            return false;
        }
        finally { try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    public static void RemoveLogonTask()
    {
        try { Schtasks("/Delete", "/TN", ValueName, "/F"); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
    }

    // Normal priority (task default is below normal), no time limit, runs on battery, one instance.
    public static string LogonTaskXml(string path, string user)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts the turn stats overlay watcher at sign-in.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{X(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{X(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{X(path)}</Command><Arguments>--watch-codex</Arguments></Exec></Actions>
            </Task>
            """;
    }

    private static (int Code, string Output, string Error) Schtasks(params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new IOException("schtasks 실행 실패");
        var error = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) lock (error) error.AppendLine(line); };
        process.BeginErrorReadLine();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        lock (error) return (process.ExitCode, output, error.ToString().Trim());
    }

    // Same implementation as the tray toggle, callable for installation verification.
    public static bool TryRun(string[] args)
    {
        if (args.Length < 3 || args[0] != "--startup") return false;
        object result;
        try
        {
            if (args[1] == "on") SetEnabled(true);
            else if (args[1] == "off") SetEnabled(false);
            else if (args[1] != "status") throw new ArgumentException("Unknown startup action");
            result = new { Success = true, Enabled = IsEnabled(), Command, Scope = "current-user-logon", LogonTask = LogonTaskRegistered() };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Environment.ExitCode = 1;
            result = new { Success = false, Error = e.Message };
        }
        File.WriteAllText(args[2], JsonSerializer.Serialize(result));
        return true;
    }
}
