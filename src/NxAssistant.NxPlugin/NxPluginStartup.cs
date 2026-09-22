using System;

namespace NxAssistant.NxPlugin;

/// <summary>
/// NX 启动时自动加载 %UGII_USER_DIR%\startup 下的程序集并调用静态 Startup()。
/// 与 NX-MCP 的 NXMcPRemotingServer 同一机制；这里非阻塞地拉起命名管道服务。
/// 注意：不得覆盖同目录中已存在的其它 startup DLL（PRD 7.1）。
/// </summary>
public static class NxPluginStartup
{
    public static int Startup()
    {
        try
        {
            PipeServer.Instance.EnsureStarted();
            NxLog.Write("Startup: pipe server bootstrapped, not blocking NX");
        }
        catch (Exception ex)
        {
            NxLog.Write("Startup failed: " + ex);
        }
        // 返回 0 让 NX 继续加载流程；Main 不阻塞 UI。
        return 0;
    }

    public static int GetUnloadOption(string dummy)
    {
        try { PipeServer.Instance.Stop(); } catch { /* ignore */ }
        return (int)NXOpen.Session.LibraryUnloadOption.AtTermination;
    }
}
