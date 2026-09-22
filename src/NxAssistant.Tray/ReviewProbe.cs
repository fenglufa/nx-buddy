using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace NxAssistant.Tray;

/// <summary>
/// 无头审图链路探针（`--review-probe`）：不弹任何 UI，走真实托盘→宿主 stdio 客户端，
/// 调 license_status 与一个必然不存在的 run_id，断言"发起→结构化错误回传"闭环。
/// 供 build/smoke_tray.py 离线回归（不需要 NX 在跑；不产生任何写操作）。
/// </summary>
internal static class ReviewProbe
{
    public static async Task<int> RunAsync()
    {
        var result = new Dictionary<string, object?> { ["ok"] = false };
        try
        {
            var exe = StatusProbe.ResolveMcpExe();
            result["mcp_exe"] = exe;
            if (!File.Exists(exe))
            {
                result["error"] = "宿主 exe 不存在，请在设置里修正 mcp_exe";
                Print(result);
                return 1;
            }
            using var client = new McpHostClient(exe);
            await client.StartAsync();

            var lic = await client.CallToolAsync("license_status", null);
            bool? licensed = lic.TryGetProperty("licensed", out var l) &&
                             (l.ValueKind == JsonValueKind.True || l.ValueKind == JsonValueKind.False)
                ? l.GetBoolean() : null;
            result["license_licensed"] = licensed;
            result["license_state"] = lic.TryGetProperty("state", out var st) ? st.GetString() : null;
            if (licensed == null)
            {
                result["error"] = "license_status 未返回 licensed 布尔（宿主 stdio 闭环失败）";
                Print(result);
                return 1;
            }

            // 不存在的 run_id：无论本机授权与否，都应拿到 Guard 的结构化 {ok:false,error}
            var bad = await client.CallToolAsync("review_status",
                new Dictionary<string, object?> { ["run_id"] = "nxa-probe-missing-run" });
            string? reviewErr = null;
            bool structuredErr = bad.ValueKind == JsonValueKind.Object &&
                                 bad.TryGetProperty("ok", out var okEl) &&
                                 okEl.ValueKind == JsonValueKind.False &&
                                 bad.TryGetProperty("error", out var errEl) &&
                                 errEl.ValueKind == JsonValueKind.String &&
                                 !string.IsNullOrWhiteSpace(reviewErr = errEl.GetString());
            result["review_status_error"] = reviewErr ?? bad.GetRawText();
            if (!structuredErr)
            {
                result["error"] = "review_status 对不存在 run 未返回结构化错误";
                Print(result);
                return 1;
            }

            result["ok"] = true;
            Print(result);
            return 0;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            Print(result);
            return 1;
        }
    }

    private static void Print(Dictionary<string, object?> r) =>
        Console.Out.Write(JsonSerializer.Serialize(r, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        }));
}

/// <summary>
/// 主页构建探针（`--ui-probe`）：真构造 MainWindow（概览/审图/规则三分页与全部控件布局），
/// 显示 1.2 秒后走"真关"路径退出（同时 dispose 其宿主客户端）。任何构造/布局/关闭异常都会
/// 让进程以非零码退出——WinForms 冒烟不需要人肉点托盘图标。
/// </summary>
internal static class UiProbe
{
    public static int Run()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var icon = new System.Windows.Forms.NotifyIcon { Visible = false };
        var result = new Dictionary<string, object?> { ["ok"] = false };
        try
        {
            var f = new MainWindow(icon);
            using var closeOnce = new System.Threading.Timer(_ => f.RealClose(), null, 1200, Timeout.Infinite);
            System.Windows.Forms.Application.Run(f);
            result["ok"] = true;
            Console.Out.Write(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }));
            return 0;
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
            Console.Out.Write(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            }));
            return 1;
        }
    }
}
