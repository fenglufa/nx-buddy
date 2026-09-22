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

/// <summary>V1 白名单工具宿主侧入口。读/存/建切片已接线；其余按迁移清单分批补。</summary>
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
    [Description("读取离线授权状态：验签/验期/绑机结果，含 lic_id、客户、有效期、剩余天数、机器指纹。create/review 类工具未授权时返回 LICENSE_INVALID。")]
    public JsonElement LicenseStatus()
    {
        return _license.Status();
    }

    [McpServerTool(Name = "get_part_summary")]
    [Description("读取当前工作部件概要：名称/完整路径/体数/特征清单（journal id）。max_features 控制特征截断，默认 100。")]
    public async Task<JsonElement> GetPartSummary(
        int? max_features,
        CancellationToken ct)
    {
        var p = new Dictionary<string, object?>();
        if (max_features is int mf) p["max_features"] = mf;
        return await _plugin.CallAsync(MethodNames.GetPartSummary, p, ct);
    }

    [McpServerTool(Name = "inspect_work_part_geometry")]
    [Description("逐体几何概览：每个体的面/边数与轴对齐包围盒（min/max/size）。max_bodies 控制体数上限，默认 50。")]
    public async Task<JsonElement> InspectWorkPartGeometry(
        int? max_bodies,
        CancellationToken ct)
    {
        var p = new Dictionary<string, object?>();
        if (max_bodies is int mb) p["max_bodies"] = mb;
        return await _plugin.CallAsync(MethodNames.InspectWorkPartGeometry, p, ct);
    }

    [McpServerTool(Name = "save_work_part")]
    [Description("保存当前工作部件（含组件），返回落盘路径/大小与未保存部件数。写操作前置规则守卫批次会挂在此链路上。")]
    public async Task<JsonElement> SaveWorkPart(CancellationToken ct)
    {
        return await _plugin.CallAsync(MethodNames.SaveWorkPart, null, ct);
    }

    [McpServerTool(Name = "create_part")]
    [Description("在工作区沙箱（NXA_WORKSPACE，默认 %LOCALAPPDATA%\\NXAssistant\\workspace）内新建并显示部件。units 取 millimeters/inches；file_name 只允许纯文件名。需有效授权。")]
    public async Task<JsonElement> CreatePart(
        string file_name,
        string? units,
        CancellationToken ct)
    {
        _license.EnsureValid("create_part");
        var p = new Dictionary<string, object?> { ["file_name"] = file_name };
        if (!string.IsNullOrWhiteSpace(units)) p["units"] = units;
        return await _plugin.CallAsync(MethodNames.CreatePart, p, ct);
    }
}
