using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NxAssistant.Core.Protocol;
using NxAssistant.Mcp;

// stdio 传输下 stdout 只走 JSON-RPC：诊断信息一律写到 stderr。
Console.Error.WriteLine("[nx-buddy] MCP host starting (stdio)...");

var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

// stdio 传输下 stdout 只承载 JSON-RPC：清空默认 console 日志，避免污染协议通道。
builder.Logging.ClearProviders();

builder.Services.AddSingleton<NxPluginClient>();
builder.Services.AddSingleton<LicenseService>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<NxTools>();

await builder.Build().RunAsync();

/// <summary>V1 白名单工具宿主侧入口。骨架批只登记 ping / license_status，其余按迁移清单分批补。</summary>
[McpServerToolType]
public sealed class NxTools
{
    private readonly NxPluginClient _plugin;
    private readonly LicenseService _license;

    public NxTools(NxPluginClient plugin, LicenseService license)
    {
        _plugin = plugin;
        _license = license;
    }

    [McpServerTool(Name = "ping")]
    [Description("连通性与就绪检查：验证 Agent→MCP→IPC→NX 插件链路是否通，返回 NX 进程与工作部件信息。")]
    public async Task<JsonElement> Ping(CancellationToken ct)
    {
        return await _plugin.CallAsync(MethodNames.Ping, null, ct);
    }

    [McpServerTool(Name = "license_status")]
    [Description("读取离线授权状态（有效/过期/未绑机/未安装）。骨架批尚未接入验签，返回占位状态。")]
    public JsonElement LicenseStatus()
    {
        return _license.Status();
    }
}
