using System.Windows.Automation;
using System.Runtime.InteropServices;

namespace TokenOverlay;

// Subscription order is not navigation order. Read only the foreground app document title.
internal sealed class VisibleThreadRouteMonitor(string sessionRoot)
{
    private readonly ThreadTitleReader _titles = new(sessionRoot);
    private string? _threadId;
    private long _version;
    public string? VisibleTitle { get; private set; }

    public ActiveThreadRouteStatus Poll()
    {
        string? id = null, error = null;
        string? visibleTitle = null;
        var connected = false;
        try
        {
            if (CodexWindowLocator.TryGetForegroundCodexTarget(out var target) && target.App == HostApp.Codex)
            {
                var root = AutomationElement.FromHandle(target.HostWindow.Handle);
                var document = root.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "RootWebArea")));
                if (document is not null && !document.Current.IsOffscreen)
                {
                    visibleTitle = document.Current.Name;
                    id = _titles.ResolveUniqueTitle(visibleTitle);
                    connected = id is not null;
                    if (id is null) error = "현재 화면 제목과 일치하는 유일한 로컬 채팅을 확인할 수 없음";
                }
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            error = "현재 화면 채팅 확인 대기";
        }
        if (_threadId != id) { _threadId = id; _version++; }
        VisibleTitle = visibleTitle;
        return new ActiveThreadRouteStatus(id, connected ? 1 : 0, connected, _version, error);
    }
}
