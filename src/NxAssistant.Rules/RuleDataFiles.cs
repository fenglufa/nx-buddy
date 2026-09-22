using System;
using System.Collections.Generic;

namespace NxAssistant.Rules;

/// <summary>
/// 规则 → 数据文件映射（与 RuleEngine.Dispatch 的取数口径同步维护）。
/// 供托盘"规则"分页渲染可编辑参数；文件名不含扩展名，相对规则所在包目录解析。
/// </summary>
public static class RuleDataFiles
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        ["HOLE-DIA-001"] = new[] { "hole_series" },
        ["HOLE-BOLT-002"] = new[] { "hole_series" },
        ["HOLE-THD-003"] = new[] { "hole_series" },
        ["FEAT-FILLET-002"] = new[] { "fillet_series" },
        ["FRAME-001"] = new[] { "drawing_frames" },
        ["TITLE-001"] = new[] { "drawing_frames" },
        ["TITLE-002"] = new[] { "part_number_pattern" },
        ["TITLE-003"] = new[] { "part_number_pattern" },
        ["TITLE-004"] = new[] { "part_number_pattern" },
        ["LAYER-001"] = new[] { "layers" },
        ["LAYER-002"] = new[] { "layers" },
        ["PLATE-THK-001"] = new[] { "plate_thickness" },
        ["STEEL-PROF-001"] = new[] { "steel_profiles" },
        ["WELD-ANN-001"] = new[] { "weld_policy" },
        ["FLANGE-PCD-001"] = new[] { "flange_patterns" },
        ["PIN-BORE-001"] = new[] { "pin_bore" },
        ["SENSOR-HOLE-001"] = new[] { "sensor_holes" },
    };

    public static IReadOnlyList<string> For(string ruleId) =>
        Map.TryGetValue(ruleId, out var files) ? files : Array.Empty<string>();
}
