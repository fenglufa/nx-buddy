using System.Collections.Generic;

namespace NxAssistant.Rules;

/// <summary>
/// 审图/建模的"证据"模型：由 NX 侧抽取层（inspect_* / 写前检查）填充，规则引擎消费。
/// 与 NXOpen 无关，可 JSON 序列化经 IPC 传输；缺某段证据则相关规则自然跳过。
/// </summary>
public sealed class Evidence
{
    public PartEvidence? Part { get; set; }
    public DrawingEvidence? Drawing { get; set; }
}

public sealed class PartEvidence
{
    /// <summary>实体（solid body）数量；null=未抽取。</summary>
    public int? SolidBodyCount { get; set; }
    public List<SheetBodyEvidence> SheetBodies { get; set; } = new();
    public List<HoleEvidence> Holes { get; set; } = new();
    public List<ExtrudeEvidence> Extrudes { get; set; } = new();
    public List<FilletEvidence> Fillets { get; set; } = new();
    public double? PlateThickness { get; set; }
    public List<string> SteelProfileDesignations { get; set; } = new();
    public List<HolePatternEvidence> HolePatterns { get; set; } = new();
}

public sealed class SheetBodyEvidence
{
    public string Name { get; set; } = "";
    /// <summary>命名为工艺辅助（展开/模体）的片体不算多余。</summary>
    public bool IsProcessAux { get; set; }
}

/// <summary>孔证据。Kind: simple_through | simple_blind | clearance_for_bolt | threaded | pin | sensor。</summary>
public sealed class HoleEvidence
{
    public double Diameter { get; set; }
    public string Kind { get; set; } = "simple_through";
    /// <summary>clearance_for_bolt 时的公称螺纹，如 "M8"。</summary>
    public string? NominalBolt { get; set; }
    /// <summary>threaded/pin/sensor 时的螺纹标记，如 "M6"。</summary>
    public string? ThreadLabel { get; set; }
    /// <summary>来源：model（建模孔特征）| drawing_callout（图纸注释孔）。</summary>
    public string Source { get; set; } = "model";
    /// <summary>是否来自阵列/孔特征组（重复孔合规手段）。</summary>
    public bool FromArrayOrHoleSet { get; set; }
}

public sealed class ExtrudeEvidence
{
    /// <summary>拉伸距离 mm。</summary>
    public double? Distance { get; set; }
    /// <summary>源草图状态（Sketch.Status 文本），如 "Finished"。</summary>
    public string? SketchStatus { get; set; }
}

public sealed class FilletEvidence
{
    public double? Radius { get; set; }
}

/// <summary>圆形孔组（法兰等）证据。</summary>
public sealed class HolePatternEvidence
{
    public string Name { get; set; } = "";
    public double? Pcd { get; set; }
    public int? HoleCount { get; set; }
    public double? HoleDiameter { get; set; }
    public string? Bolt { get; set; }
    public double? CenterBore { get; set; }
}

public sealed class DrawingEvidence
{
    public List<SheetEvidence> Sheets { get; set; } = new();
    public bool? HasWeldSymbol { get; set; }
    public List<BomRowEvidence> PartsList { get; set; } = new();
}

public sealed class SheetEvidence
{
    public string FrameId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public int? ViewCount { get; set; }
    /// <summary>标题栏字段名→值（缺失/空即视为未填）。</summary>
    public Dictionary<string, string> TitleBlock { get; set; } = new();
    public List<string> LayersUsed { get; set; } = new();
    public List<DimensionEvidence> Dimensions { get; set; } = new();
}

public sealed class DimensionEvidence
{
    public string Layer { get; set; } = "";
}

public sealed class BomRowEvidence
{
    public string ItemNumber { get; set; } = "";
    public string Name { get; set; } = "";
}
