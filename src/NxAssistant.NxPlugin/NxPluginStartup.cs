using System;
using System.IO;
using System.Reflection;

/// <summary>
/// NX startup 目录加载约定（NX-MCP 已在本机 2412 验证）：
/// %UGII_USER_DIR%\startup\<X>.dll 必须包含与文件同名的公共类 <X>，
/// NX 在其主线程上自动调用静态 Startup()；必须立即返回、不得阻塞 UI。
/// 因此本类不放在命名空间内，且类名与 AssemblyName（NxAssistant_NxPlugin）一致。
/// </summary>
public static class NxAssistant_NxPlugin
{
    private static bool _hooked;

    public static int Startup()
    {
        try
        {
            HookResolve();                                        // 先装解析钩子，后续访问 Core/Json 才不炸
            NxAssistant.NxPlugin.MainThread.Init();               // 主线程句柄，供后续 marshal
            NxAssistant.NxPlugin.PipeServer.Instance.EnsureStarted();
            NxAssistant.NxPlugin.NxLog.Write("Startup: dispatcher + pipe server ready");
        }
        catch (Exception ex)
        {
            // 连累到本类都不一定加载得动时只能静默；写日志失败也不影响 NX 继续启动。
            try { NxAssistant.NxPlugin.NxLog.Write("Startup failed: " + ex); }
            catch { }
        }
        return 0;
    }

    public static int GetUnloadOption(string dummy)
    {
        try { NxAssistant.NxPlugin.PipeServer.Instance.Stop(); } catch { /* ignore */ }
        return (int)NXOpen.Session.LibraryUnloadOption.AtTermination;
    }

    /// <summary>
    /// ugraf.exe 的 CLR AppDomain 探测基准目录是 NXBIN，不是 startup 目录，
    /// 故与本插件同目录部署的依赖（NxAssistant.Core.dll / System.Text.Json.dll 等）默认解析不到。
    /// 这里按简单名从"本插件 DLL 所在目录"回退加载。
    /// </summary>
    private static void HookResolve()
    {
        if (_hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
    }

    private static Assembly? OnResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var name = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(name)) return null;
            var dir = Path.GetDirectoryName(typeof(NxAssistant_NxPlugin).Assembly.Location);
            if (dir == null) return null;
            var candidate = Path.Combine(dir, name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }
        catch { return null; }
    }
}
