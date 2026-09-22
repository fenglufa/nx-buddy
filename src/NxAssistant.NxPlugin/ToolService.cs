using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 插件内方法分发。handler 对应 NX-MCP 的 _OPS：一个方法名 → 一段 NXOpen 逻辑，返回可 JSON 化对象。
/// 后续每加一个工具，在此登记并在 MethodNames 里补常量。
/// </summary>
internal static class ToolService
{
    // THREADING: 接线批次需将 handler 调用 marshal 到 NX 主线程后再执行。
    public static object Dispatch(string method, JsonElement @params)
    {
        switch (method)
        {
            case MethodNames.Ping:
                return Ping();
            default:
                throw new NotSupportedException($"unknown method: '{method}' (not implemented yet)");
        }
    }

    /// <summary>对齐 _op_ping：证明"宿主→IPC→插件→NX 会话"这条链是否通。</summary>
    private static object Ping()
    {
        var session = NXOpen.Session.GetSession();
        string workPart = string.Empty;
        try
        {
            var work = session.Parts?.Work;
            if (work != null && work.Tag != NXOpen.Tag.Null) workPart = work.Leaf;
        }
        catch { /* 无工作部件时留空 */ }

        var proc = Process.GetCurrentProcess();
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["plugin_version"] = typeof(ToolService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["application"] = session.ApplicationName,
            ["pid"] = proc.Id,
            ["work_part"] = workPart,
            ["transport"] = "named-pipe",
            ["log"] = NxLog.LogPath,
        };
    }
}
