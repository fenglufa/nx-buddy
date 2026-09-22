using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [McpServerTool(Name = "create_block")]
    [Description("在工作部件原点系创建长方体（OriginAndEdgeLengths，布尔 Create）。length/width/height 默认 100/60/40，须 >0；origin 默认 [0,0,0]。需有效授权。")]
    public Task<JsonElement> CreateBlock(
        double? length = null,
        double? width = null,
        double? height = null,
        JsonElement? origin = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("create_block");
            var p = new Dictionary<string, object?>();
            if (length is double l) p["length"] = l;
            if (width is double w) p["width"] = w;
            if (height is double h) p["height"] = h;
            if (origin is JsonElement o) p["origin"] = o;
            return _plugin.CallAsync(MethodNames.CreateBlock, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "create_parametric_sketch")]
    [Description("在 XY/XZ/YZ 基准平面上创建参数化草图。geometry 必填：line(start/end)、rectangle(origin/width/height，自动加水平/垂直/重合约束)、circle/arc(center/radius[,start_angle_deg/end_angle_deg])，局部二维坐标自动映射世界系。constraints/dimensions 可选；dimensions 每项建独立表达式并命名 sketch_尺寸名，供 set_feature_expression 改名复用。需有效授权。")]
    public Task<JsonElement> CreateParametricSketch(
        JsonElement geometry,
        string? name = null,
        string? plane = null,
        JsonElement? origin = null,
        JsonElement? constraints = null,
        JsonElement? dimensions = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("create_parametric_sketch");
            var p = new Dictionary<string, object?> { ["geometry"] = geometry };
            if (!string.IsNullOrWhiteSpace(name)) p["name"] = name;
            if (!string.IsNullOrWhiteSpace(plane)) p["plane"] = plane;
            if (origin is JsonElement o) p["origin"] = o;
            if (constraints is JsonElement c) p["constraints"] = c;
            if (dimensions is JsonElement d) p["dimensions"] = d;
            return _plugin.CallAsync(MethodNames.CreateParametricSketch, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "inspect_sketch")]
    [Description("读取草图清单或单图（sketch_id 支持名/journal id/序号，缺省列全部）：原点、状态、几何记录与全部表达式（name/RHS/value/units）。")]
    public Task<JsonElement> InspectSketch(
        string? sketch_id = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            var p = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(sketch_id)) p["sketch_id"] = sketch_id;
            return _plugin.CallAsync(MethodNames.InspectSketch, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "extrude_sketch")]
    [Description("把草图截面拉伸成实体：distance 必填 >0；start 默认 0、direction 默认 [0,0,1]（自动单位化）；sketch_id 缺省时要求全部件只有一张草图。失败自动回滚撤销点。需有效授权。")]
    public Task<JsonElement> ExtrudeSketch(
        double distance,
        string? sketch_id = null,
        double? start = null,
        JsonElement? direction = null,
        string? feature_name = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("extrude_sketch");
            var p = new Dictionary<string, object?> { ["distance"] = distance };
            if (!string.IsNullOrWhiteSpace(sketch_id)) p["sketch_id"] = sketch_id;
            if (start is double s) p["start"] = s;
            if (direction is JsonElement d) p["direction"] = d;
            if (!string.IsNullOrWhiteSpace(feature_name)) p["feature_name"] = feature_name;
            return _plugin.CallAsync(MethodNames.ExtrudeSketch, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "set_feature_expression")]
    [Description("改特征/草图表达式右值并立即更新：expression_id 支持表达式名、journal id 或特征表达式序号；更新失败（error/过期）自动回滚，返回 old/new 对照。把尺寸命名成可读销轴直径/长度表达式的落点。需有效授权。")]
    public Task<JsonElement> SetFeatureExpression(
        string feature_id,
        string expression_id,
        string right_hand_side,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("set_feature_expression");
            var p = new Dictionary<string, object?>
            {
                ["feature_id"] = feature_id,
                ["expression_id"] = expression_id,
                ["right_hand_side"] = right_hand_side,
            };
            return _plugin.CallAsync(MethodNames.SetFeatureExpression, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "create_cylindrical_hole")]
    [Description("在目标实体上创建减料圆柱孔（CylinderBuilder：origin+direction 轴、diameter×depth，布尔 Subtract）。提交前先过 HOLE-DIA-001 写前守卫：hole_kind=simple_through/simple_blind 的直径必须∈公司标准系列，φ13.2 这类失配直径会被拒绝并返回邻近建议值（如 13/14），NX 侧不留任何改动。需有效授权。")]
    public Task<JsonElement> CreateCylindricalHole(
        double diameter,
        double depth,
        JsonElement? origin = null,
        JsonElement? direction = null,
        int target_body_index = 0,
        string hole_kind = "simple_through",
        string? feature_name = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("create_cylindrical_hole");
            if (hole_kind != "simple_through" && hole_kind != "simple_blind")
                throw new ArgumentException("hole_kind 目前仅支持 simple_through / simple_blind（螺纹/ bolt 孔待后续切片）");
            var blocked = HostRules.CheckHoleWrite(diameter, hole_kind);
            if (blocked != null) return Task.FromResult(blocked.Value);
            var p = new Dictionary<string, object?>
            {
                ["diameter"] = diameter,
                ["depth"] = depth,
                ["target_body_index"] = target_body_index,
            };
            if (origin is JsonElement o) p["origin"] = o;
            if (direction is JsonElement d) p["direction"] = d;
            if (feature_name != null) p["feature_name"] = feature_name;
            return _plugin.CallAsync(MethodNames.CreateCylindricalHole, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "fillet_edges")]
    [Description("对实体指定边倒圆角（EdgeBlendBuilder+Chainset；edge_indices 非空、不重复，与 inspect_body_topology 的边 index 同源）。FEAT-FILLET-002 为 warn 级守卫：半径不在建议系列（0.5/1/1.5/2/3/5）不拦截成孔，但响应附 rule_warnings+建议值。失败自动回滚。需有效授权。")]
    public Task<JsonElement> FilletEdges(
        double radius,
        JsonElement edge_indices,
        int body_index = 0,
        string? feature_name = null,
        CancellationToken ct = default)
    {
        return Guard(async () =>
        {
            _license.EnsureValid("fillet_edges");
            var p = new Dictionary<string, object?>
            {
                ["radius"] = radius,
                ["body_index"] = body_index,
                ["edge_indices"] = edge_indices,
            };
            if (feature_name != null) p["feature_name"] = feature_name;
            var result = await _plugin.CallAsync(MethodNames.FilletEdges, p, ct);
            return AttachRuleWarnings(result, HostRules.FilletWarnings(radius));
        }, ct);
    }

    [McpServerTool(Name = "chamfer_edges")]
    [Description("对实体指定边对称倒角（ChamferBuilder EdgesAlongFaces+SymmetricOffsets，两偏置=distance；edge_indices 规则同 fillet_edges）。失败自动回滚。需有效授权。")]
    public Task<JsonElement> ChamferEdges(
        double distance,
        JsonElement edge_indices,
        int body_index = 0,
        string? feature_name = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("chamfer_edges");
            var p = new Dictionary<string, object?>
            {
                ["distance"] = distance,
                ["body_index"] = body_index,
                ["edge_indices"] = edge_indices,
            };
            if (feature_name != null) p["feature_name"] = feature_name;
            return _plugin.CallAsync(MethodNames.ChamferEdges, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "move_object")]
    [Description("用 NX Move Body 特征平移工作部件中的实体：translation=[dx,dy,dz] 毫米（非零向量）。提交后按包围盒回读实际位移，与请求不一致（如对 extrude 等特征驱动实体 '提交成功但没动'）会自动回滚并报错提示改编辑特征参数——假成功被护栏消灭。成功响应含移动前后包围盒。失败自动回滚。需有效授权。")]
    public Task<JsonElement> MoveObject(
        JsonElement translation,
        int body_index = 0,
        string? feature_name = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("move_object");
            if (translation.ValueKind != JsonValueKind.Array || translation.GetArrayLength() != 3)
                throw new ArgumentException("translation must contain exactly three coordinates");
            if (translation.EnumerateArray().All(v => v.GetDouble() == 0))
                throw new ArgumentException("translation must not be the zero vector");
            var p = new Dictionary<string, object?>
            {
                ["translation"] = translation,
                ["body_index"] = body_index,
            };
            if (feature_name != null) p["feature_name"] = feature_name;
            return _plugin.CallAsync(MethodNames.MoveObject, p, ct);
        }, ct);
    }

    /// warn 级规则告警随成功响应附带返回（不改变 ok 语义，不拦截写入）。
    private static JsonElement AttachRuleWarnings(JsonElement result, IReadOnlyList<NxAssistant.Rules.Finding> warnings)
    {
        if (warnings.Count == 0 ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            return result;
        var node = JsonNode.Parse(result.GetRawText())!.AsObject();
        var arr = new JsonArray();
        foreach (var f in warnings)
        {
            arr.Add(JsonNode.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["rule_id"] = f.RuleId, ["name"] = f.Name, ["message"] = f.Message,
                ["suggestions"] = f.Suggestions, ["placeholder"] = f.Placeholder,
            })));
        }
        node["rule_warnings"] = arr;
        return JsonDocument.Parse(node.ToJsonString()!).RootElement;
    }

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
