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
    public Task<JsonElement> Ping(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.Ping, null, ct), ct);

    [McpServerTool(Name = "license_status")]
    [Description("读取离线授权状态：验签/验期/绑机结果，含 lic_id、客户、有效期、剩余天数、机器指纹。create/review 类工具未授权时返回 LICENSE_INVALID。")]
    public JsonElement LicenseStatus()
    {
        return _license.Status();
    }

    [McpServerTool(Name = "get_part_summary")]
    [Description("读取当前工作部件概要：名称/完整路径/体数/特征清单（journal id）。max_features 控制特征截断，默认 100。")]
    public Task<JsonElement> GetPartSummary(
        int? max_features = null,
        CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?>();
        if (max_features is int mf) p["max_features"] = mf;
        return Guard(() => _plugin.CallAsync(MethodNames.GetPartSummary, p, ct), ct);
    }

    [McpServerTool(Name = "inspect_work_part_geometry")]
    [Description("逐体几何概览：每个体的面/边数与轴对齐包围盒（min/max/size）。max_bodies 控制体数上限，默认 50。")]
    public Task<JsonElement> InspectWorkPartGeometry(
        int? max_bodies = null,
        CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?>();
        if (max_bodies is int mb) p["max_bodies"] = mb;
        return Guard(() => _plugin.CallAsync(MethodNames.InspectWorkPartGeometry, p, ct), ct);
    }

    [McpServerTool(Name = "save_work_part")]
    [Description("保存当前工作部件（含组件），返回落盘路径/大小与未保存部件数。写操作前置规则守卫批次会挂在此链路上。")]
    public Task<JsonElement> SaveWorkPart(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.SaveWorkPart, null, ct), ct);

    [McpServerTool(Name = "create_part")]
    [Description("在工作区沙箱（NXA_WORKSPACE，默认 %LOCALAPPDATA%\\NXAssistant\\workspace）内新建并显示部件。units 取 millimeters/inches；file_name 只允许纯文件名。需有效授权。")]
    public Task<JsonElement> CreatePart(
        string file_name,
        string? units = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("create_part");
            var p = new Dictionary<string, object?> { ["file_name"] = file_name };
            if (!string.IsNullOrWhiteSpace(units)) p["units"] = units;
            return _plugin.CallAsync(MethodNames.CreatePart, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "inspect_body_topology")]
    [Description("逐面/逐边拓扑清单：每条记录含 stable_id（几何指纹，重建模型后仍可命中）、类型、法向/端点、包围盒/长度等。体由 body_index（默认 0）或 body_feature_id(+body_occurrence) 指定。")]
    public Task<JsonElement> InspectBodyTopology(
        int? body_index = null,
        string? body_feature_id = null,
        int? body_occurrence = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            var p = new Dictionary<string, object?>();
            if (body_index is int bi) p["body_index"] = bi;
            if (!string.IsNullOrWhiteSpace(body_feature_id)) p["body_feature_id"] = body_feature_id;
            if (body_occurrence is int bo) p["body_occurrence"] = bo;
            return _plugin.CallAsync(MethodNames.InspectBodyTopology, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "resolve_topology")]
    [Description("按 selector 定位唯一面/边：kind 取 face/edge；selector 优先 stable_id，失配时可用几何回退字段（uf_type/normal/plane_offset/radius/near_point/direction/length/sort_by 等）。unique 默认 true，多命中需 sort_by 或 occurrence。返回 selected 与 matches。")]
    public Task<JsonElement> ResolveTopology(
        string kind,
        JsonElement selector,
        int? body_index = null,
        string? body_feature_id = null,
        int? body_occurrence = null,
        bool? unique = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            var p = new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["selector"] = selector,
            };
            if (body_index is int bi) p["body_index"] = bi;
            if (!string.IsNullOrWhiteSpace(body_feature_id)) p["body_feature_id"] = body_feature_id;
            if (body_occurrence is int bo) p["body_occurrence"] = bo;
            if (unique is bool u) p["unique"] = u;
            return _plugin.CallAsync(MethodNames.ResolveTopology, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "inspect_feature")]
    [Description("单特征详情：类型、是否过期/抑制、表达式清单、父/子特征 journal id、所属体 tag、错误/警告消息。feature_id 接受特征名、journal id 或序号。")]
    public Task<JsonElement> InspectFeature(
        string feature_id,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            var p = new Dictionary<string, object?> { ["feature_id"] = feature_id };
            return _plugin.CallAsync(MethodNames.InspectFeature, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "rebuild_work_part")]
    [Description("重建当前工作部件（UpdateManager.DoUpdate），返回 update_error_count 与逐特征诊断（过期/错误/警告）。写操作后用它验证模型是否健康。")]
    public Task<JsonElement> RebuildWorkPart(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.RebuildWorkPart, null, ct), ct);

    /// <summary>
    /// SDK 2.2.0 会把工具异常吞成 "An error occurred invoking ..."。这里对齐 NX-MCP 约定：
    /// 失败转成可读 JSON 载荷 {ok:false,error,error_type} 交给 Agent 判读，而不是丢协议错误。
    /// </summary>
    private static async Task<JsonElement> Guard(Func<Task<JsonElement>> call, CancellationToken ct = default)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var payload = new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
            };
            return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
        }
    }
}
