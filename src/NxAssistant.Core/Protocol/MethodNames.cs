namespace NxAssistant.Core.Protocol;

/// <summary>
/// 宿主→插件的方法名。必须与 MCP 工具名、插件 handler 表键三者一致（命名三位一体，参考 ADDING_TOOLS）。
/// </summary>
public static class MethodNames
{
    // §9.1 读
    public const string Ping = "ping";
    public const string GetPartSummary = "get_part_summary";
    public const string InspectWorkPartGeometry = "inspect_work_part_geometry";
    public const string InspectBodyTopology = "inspect_body_topology";
    public const string ResolveTopology = "resolve_topology";
    public const string InspectFeature = "inspect_feature";
    public const string RebuildWorkPart = "rebuild_work_part";
    public const string InspectSketch = "inspect_sketch";
    public const string InspectDrawingAnnotations = "inspect_drawing_annotations";

    // §9.1 写
    public const string SaveWorkPart = "save_work_part";
    public const string CreatePart = "create_part";
    public const string CreateBlock = "create_block";
    public const string SetFeatureExpression = "set_feature_expression";
    public const string CreateParametricSketch = "create_parametric_sketch";
    public const string ExtrudeSketch = "extrude_sketch";
    public const string CreateCylindricalHole = "create_cylindrical_hole";
    public const string FilletEdges = "fillet_edges";
    public const string ChamferEdges = "chamfer_edges";
    public const string ExportExchange = "export_exchange";
    public const string ImportExchange = "import_exchange";
    public const string MoveObject = "move_object";

    // 内部 IPC 方法（不对外暴露为 MCP 工具；审图长任务由宿主编排时调用）
    public const string ExtractEvidence = "extract_evidence";
}
