using System;
using System.Threading;
using System.Windows.Forms;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 把 NXOpen 调用串行化到 NX 主线程执行。
/// NXOpen 的建模/查询 API 有 UI 主线程亲和性；命名管道服务在后台线程收请求，
/// 必须经此把委托 marshal 回主线程（等价于 NX-MCP 用常驻 journal 线程串行化的效果，
/// 但对宿主端仍是异步请求/响应）。
///
/// 实现：Startup 时（在主线程）创建一个不显示的 Control 作消息泵，其窗口句柄归属主线程消息循环；
/// 工作线程用 BeginInvoke + AsyncWaitHandle 等待（可超时），EndInvoke 把主线程异常回传给请求方。
/// </summary>
internal static class MainThread
{
    public const int DefaultTimeoutMs = 120_000;

    private static Control? _pump;
    private static int _mainThreadId = -1;
    private static readonly object InitLock = new object();

    /// <summary>必须在 NX 加载库的线程（主线程）上调用一次。</summary>
    public static void Init()
    {
        lock (InitLock)
        {
            if (_pump != null) return;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _pump = new Control();
            var _ = _pump.Handle;    // 访问 Handle 强制在本线程创建窗口句柄（不可见 Control 的 CreateControl 不建句柄）
            NxLog.Write($"MainThread.Init on managed thread {_mainThreadId}");
        }
    }

    /// <summary>在主线程执行 fn 并等待结果（默认 120s 超时）。用于管道工作线程。</summary>
    public static T Run<T>(Func<T> fn, int timeoutMs = DefaultTimeoutMs)
    {
        var pump = _pump;
        if (pump == null)
            throw new InvalidOperationException("MainThread dispatcher 未初始化（插件 Startup 未执行）");
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
            return fn(); // 已在主线程，直接执行避免重入死锁
        if (!pump.IsHandleCreated)
            _ = pump.Handle;
        // 不用 Control.Invoke：主线程若被模态对话框卡住会永挂。BeginInvoke + 等待句柄可超时。
        // 闭包本体在主线程消息循环里执行；其中抛出的异常由 EndInvoke 回传到调用线程。
        Func<object> remote = () => fn()!;
        var ar = pump.BeginInvoke(remote, Array.Empty<object>());
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            throw new TimeoutException(
                $"NX 主线程 {timeoutMs / 1000}s 内未响应（可能有模态对话框待处理）；该操作稍后可能仍会执行，请勿盲目重试。");
        return (T)pump.EndInvoke(ar);
    }

    public static object Run(Func<object> fn) => Run<object>(fn);

    public static bool OnMainThread =>
        _mainThreadId > 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
}
