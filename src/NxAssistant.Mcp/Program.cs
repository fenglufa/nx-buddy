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

    [McpServerTool(Name = "export_exchange")]
    [Description("把当前工作部件导出为工作区内的 STEP 文件（DexManager StepCreator，ap203/ap214/ap242/ap242ed2，精确实体+曲面+可选曲线）。file_name 必须是纯文件名且扩展名 .stp/.step，落在 FILE-001 沙箱内；已存在需 overwrite=true；当前部件必须已保存到盘上。导出后回读文件存在且 size>0，0 字节/未生成即报错。V1 仅支持 STEP（Parasolid 待后续）。需有效授权。")]
    public Task<JsonElement> ExportExchange(
        string file_name,
        string format = "step",
        string application_protocol = "ap242",
        bool include_curves = true,
        bool overwrite = false,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("export_exchange");
            var p = new Dictionary<string, object?>
            {
                ["file_name"] = file_name,
                ["format"] = format,
                ["application_protocol"] = application_protocol,
                ["include_curves"] = include_curves,
                ["overwrite"] = overwrite,
            };
            return _plugin.CallAsync(MethodNames.ExportExchange, p, ct);
        }, ct);
    }

    [McpServerTool(Name = "import_exchange")]
    [Description("把工作区内的 STEP 文件（.stp/.step，FILE-001 沙箱、必须已存在）导入当前工作部件：ap203/ap214/ap242 StepImporter，ImportTo=WorkPart，默认缝合成实体+简化几何。回读护栏：实体数增量为 0 或 update 报错即 ok:false（响应含 body/feature 前后计数与 update_error_count）。导入件是非特征实体——move_object 正路径依赖本工具。V1 仅 STEP。需有效授权。")]
    public Task<JsonElement> ImportExchange(
        string file_name,
        string application_protocol = "ap242",
        bool include_curves = true,
        bool sew_surfaces = true,
        bool simplify_geometry = true,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("import_exchange");
            var p = new Dictionary<string, object?>
            {
                ["file_name"] = file_name,
                ["application_protocol"] = application_protocol,
                ["include_curves"] = include_curves,
                ["sew_surfaces"] = sew_surfaces,
                ["simplify_geometry"] = simplify_geometry,
            };
            return _plugin.CallAsync(MethodNames.ImportExchange, p, ct);
        }, ct);
    }

    // ---- 批 5：抽取类 inspect_*（只读，无授权闸门）+ undo_last_assistant_change（写侧，有闸门） ----

    [McpServerTool(Name = "inspect_layers")]
    [Description("读工作部件对象所在图层分布：每个体的 index/名称/类型（solid/sheet/other）与图层号、layer_counts 汇总、当前工作图层。LAYER-001/002 证据源之一；图层号→图层名的别名映射待客户图样校准（V1 以数字对外）。")]
    public Task<JsonElement> InspectLayers(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectLayers, null, ct), ct);

    [McpServerTool(Name = "inspect_drawing_annotations")]
    [Description("读工作部件的注释与尺寸清单（名称/journal id/所在图层/文本或测量值）。工程图注释正路径待客户 2D 样件；模型件返回空清单+0 计数是正常结果。DIM-001/LAYER-002 证据源。")]
    public Task<JsonElement> InspectDrawingAnnotations(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectDrawingAnnotations, null, ct), ct);

    [McpServerTool(Name = "inspect_title_block")]
    [Description("读图纸标题栏字段：逐页返回 title_block_raw（图纸页字符串属性全集）与比例。规范字段名（drawing_no/revision/title/material/scale/drawn_by/date）由规则包 title_block_fields.json 别名表解析（占位，待客户确认）。无图纸页时 drawing_sheet_count=0 属正常（V1 不创建工程图）。")]
    public Task<JsonElement> InspectTitleBlock(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectTitleBlock, null, ct), ct);

    [McpServerTool(Name = "inspect_drawing_sheet")]
    [Description("读图纸页/图框/视图清单：每页名称、幅面尺寸、单位、投影角、比例、视图数与 frame_id/template_name（按候选属性标题探测，真实图框 id 待客户确认）。喂 FRAME-001/VIEW-001。")]
    public Task<JsonElement> InspectDrawingSheet(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectDrawingSheet, null, ct), ct);

    [McpServerTool(Name = "inspect_weld_annotations")]
    [Description("判断工作部件有无焊缝符号（Annotations.Welds 计数）。华恒 WELD-ANN-001 只查有无（warn 级）。")]
    public Task<JsonElement> InspectWeldAnnotations(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectWeldAnnotations, null, ct), ct);

    [McpServerTool(Name = "inspect_parts_list")]
    [Description("读明细表/BOM 存在性与名称。V1 只报清单（行文本抽取待客户装配样件）；BOM-BUY-001 缺证据即跳过。")]
    public Task<JsonElement> InspectPartsList(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectPartsList, null, ct), ct);

    [McpServerTool(Name = "inspect_sheet_thickness")]
    [Description("读板厚：V1 无钣金工具，按实体最小包围盒棱长启发（method=solid_bbox_min_dim，多实体取最小），逐体给出三棱长。PLATE-THK-001 白名单比对用；非平板零件该值不代表工艺厚度，判定请结合零件类型解读。")]
    public Task<JsonElement> InspectSheetThickness(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectSheetThickness, null, ct), ct);

    [McpServerTool(Name = "inspect_hole_pattern")]
    [Description("读法兰/孔组分布：同直径同轴向 ≥3 孔且径向散布 <15% 视为圆形孔组，返回 pcd/孔数/孔径/特征名。FLANGE-PCD-001 样本比对用；V1 不创建阵列特征，正路径待客户法兰样件。")]
    public Task<JsonElement> InspectHolePattern(CancellationToken ct) =>
        Guard(() => _plugin.CallAsync(MethodNames.InspectHolePattern, null, ct), ct);

    [McpServerTool(Name = "undo_last_assistant_change")]
    [Description("只撤销小助手在当前工作部件的上一次成功写入：依赖每个写 op 提交的具名可见 undo mark+模型指纹双校验——撤销栈最新可见标记不是助手所写、或模型自上次写入后又发生变化（可能有用户手工操作）时一律拒绝并提示在 NX 中手动回退，绝不误撤用户操作。账本在插件进程内，NX 重启/部件重开后不保留。仅回退会话，磁盘文件需再 save_work_part 才同步。需有效授权。")]
    public Task<JsonElement> UndoLastAssistantChange(CancellationToken ct) =>
        Guard(() =>
        {
            _license.EnsureValid("undo_last_assistant_change");
            return _plugin.CallAsync(MethodNames.UndoLastAssistantChange, null, ct);
        }, ct);

    [McpServerTool(Name = "review_folder")]
    [Description("审图长任务：提交工作区内的目录（FILE-001 沙箱，path 可为绝对路径或工作区相对路径），立即返回 run_id——不同步堵死对话。宿主内部串行逐张打开 *.prt、抽证据、对 company_v3 规则包判定，结束后生成 审图报告.xlsx+summary.json。Agent 协议：拿到 run_id 后告知用户总数，随后用 review_status 轮询；不要对目录里每个文件单独 inspect_*；不要按时间猜测完成。中断/失败后可用 resume_run_id 从下一张续跑。需有效授权。")]
    public Task<JsonElement> ReviewFolder(
        string path,
        string? resume_run_id = null,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("review_folder");
            return Task.FromResult(ReviewOrchestrator.Start(_plugin, path, resume_run_id));
        }, ct);
    }

    [McpServerTool(Name = "review_status")]
    [Description("按 run_id 查审图进度。状态落磁盘 run 文件，宿主进程重启后仍可查。status ∈ queued/running/completed/completed_with_errors/failed/cancelled/interrupted，始终带 run_id/done/total/report_path/error_code/can_resume。判读：completed 或 completed_with_errors=审完（可 review_findings 拉明细，后者附失败文件清单）；running=报进度后继续隔轮再查；failed/interrupted=原样转告用户原因，问是否用 review_folder(resume_run_id) 续跑；cancelled=已停止、报告不完整。cancel=true 可向运行中的 run 请求停止（当前张完成后生效）。")]
    public Task<JsonElement> ReviewStatus(
        string run_id,
        bool cancel = false,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("review_status");
            return Task.FromResult(ReviewOrchestrator.Status(run_id, cancel));
        }, ct);
    }

    [McpServerTool(Name = "review_findings")]
    [Description("拉取某 run 的 finding 明细（分页：offset/limit，limit≤500）。行结构对齐验收金样：drawing_no/version/file/rule_id/name/enforcement(block=阻断|warn)/severity/object(含孔特征名)/actual/message/suggestions/placeholder(客户待替表标记)/standard。汇总统计直接看返回的 summary_path 或 report_path 的 xlsx。")]
    public Task<JsonElement> ReviewFindings(
        string run_id,
        int offset = 0,
        int limit = 50,
        CancellationToken ct = default)
    {
        return Guard(() =>
        {
            _license.EnsureValid("review_findings");
            return Task.FromResult(ReviewOrchestrator.Findings(run_id, offset, limit));
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
