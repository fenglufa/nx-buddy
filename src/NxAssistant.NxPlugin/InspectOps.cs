using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NXOpen;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 批 5 抽取类工具（迁移文档 §3）：inspect_layers / inspect_title_block / inspect_drawing_sheet /
/// inspect_weld_annotations / inspect_parts_list / inspect_sheet_thickness / inspect_hole_pattern /
/// inspect_drawing_annotations。全部只读、不改模型、不保存。
/// 工程图正路径受限（§9.3 把工程图创建排到 V1.1，V1 工具造不出带图框的图纸页）：
/// 无图纸页时诚实返回 drawing_sheet_count=0 + 空证据，规则引擎"缺证据即跳过"；
/// 图框 id/标题栏字段别名的真实校准等客户 2D 样件（test/drawings README 待办）。
/// 板厚：V1 无钣金工具，按"实体最小包围盒棱长"启发（平板类零件即厚度），method 字段如实标注。
/// </summary>
internal static partial class ToolService
{
    // ---- inspect_layers：模型体所在图层分布（旧桥 body.Layer 同源），LAYER-001/002 喂证 ----

    private static object InspectLayers(JsonElement p)
    {
        var work = Work();
        var bodies = new List<object?>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int index = 0;
        foreach (Body b in work.Bodies)
        {
            try
            {
                string kind = b.IsSolidBody ? "solid" : b.IsSheetBody ? "sheet" : "other";
                int layer = b.Layer;
                bodies.Add(new Dictionary<string, object?>
                {
                    ["index"] = index, ["name"] = b.Name, ["kind"] = kind, ["layer"] = layer,
                });
                counts.TryGetValue(layer.ToString(), out int c);
                counts[layer.ToString()] = c + 1;
            }
            catch { /* 单体读失败不影响整体分布 */ }
            index++;
        }
        int workLayer = 0;
        try { NXOpen.UF.UFSession.GetUFSession().Layer.AskWorkLayer(out workLayer); }
        catch { /* 取不到不报错 */ }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["body_layers"] = bodies,
            ["layer_counts"] = counts,
            ["work_layer"] = workLayer,
            ["note"] = "NX 图层名是属性文本，V1 以图层号对外；工程图标注图层见 inspect_drawing_annotations。",
        };
    }

    // ---- inspect_drawing_annotations（§9.1 读：注释/尺寸清单，含所在图层，喂 DIM-001/LAYER-002） ----

    private static object InspectDrawingAnnotations(JsonElement p)
    {
        var work = Work();
        var notes = new List<object?>();
        try
        {
            foreach (NXOpen.Annotations.Note note in work.Notes)
            {
                try
                {
                    notes.Add(new Dictionary<string, object?>
                    {
                        ["name"] = note.Name,
                        ["journal_id"] = note.JournalIdentifier,
                        ["layer"] = note.Layer,
                        ["text"] = note.GetText()?.ToList(),
                    });
                }
                catch { /* 单条失败跳过 */ }
            }
        }
        catch { /* 无注释集合 */ }
        var dims = DescribeDimensions(work);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["note_count"] = notes.Count,
            ["notes"] = notes,
            ["dimension_count"] = dims.Count,
            ["dimensions"] = dims,
        };
    }

    private static List<object?> DescribeDimensions(Part work)
    {
        var dims = new List<object?>();
        try
        {
            foreach (NXOpen.Annotations.Dimension d in work.Dimensions)
            {
                try
                {
                    dims.Add(new Dictionary<string, object?>
                    {
                        ["name"] = d.Name,
                        ["journal_id"] = d.JournalIdentifier,
                        ["layer"] = d.Layer,
                        ["computed_size"] = (double)d.ComputedSize,
                    });
                }
                catch { /* 单条失败跳过 */ }
            }
        }
        catch { /* 无尺寸集合 */ }
        return dims;
    }

    // ---- inspect_title_block / inspect_drawing_sheet / inspect_weld_annotations / inspect_parts_list ----

    private static object InspectTitleBlock(JsonElement p)
    {
        var work = Work();
        var sheets = SheetRecords(work);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["drawing_sheet_count"] = sheets.Count,
            ["sheets"] = sheets.Select(SheetAttrRecord).ToList(),
            ["note"] = sheets.Count == 0
                ? "工作部件没有工程图纸页（V1 不创建工程图，§9.3）；字段抽取器就位，正路径待客户 2D 样件校准。"
                : "title_block_raw 为图纸页字符串属性全集；规范字段名→属性标题的别名映射在规则包 title_block_fields.json（占位，待客户确认）。",
        };
    }

    private static object InspectDrawingSheet(JsonElement p)
    {
        var work = Work();
        var sheets = SheetRecords(work);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["drawing_sheet_count"] = sheets.Count,
            ["sheets"] = sheets.Select(SheetGeomRecord).ToList(),
            ["note"] = "frame_id/template_name 从图纸页字符串属性按候选标题探测（占位）；真实图框 id 待客户确认（迁移文档 §5 待确认项）。",
        };
    }

    private static object InspectWeldAnnotations(JsonElement p)
    {
        var work = Work();
        int weldCount = 0;
        try { weldCount = work.Annotations.Welds.ToArray().Length; }
        catch { /* 无焊缝环境按 0 */ }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["drawing_sheet_count"] = SheetCount(work),
            ["weld_symbol_count"] = weldCount,
            ["has_weld_symbol"] = weldCount > 0,
            ["note"] = "WELD-ANN-001 只查有无（warn 级）；V1 不创建工程图，图纸焊缝符号正路径待客户样件。",
        };
    }

    private static object InspectPartsList(JsonElement p)
    {
        var work = Work();
        var lists = new List<object?>();
        try
        {
            foreach (object pl in work.Annotations.PartsLists)
            {
                string name;
                try { name = (string)(pl.GetType().GetProperty("Name")?.GetValue(pl, null) ?? ""); }
                catch { name = ""; }
                lists.Add(new Dictionary<string, object?> { ["name"] = name });
            }
        }
        catch { /* 无明细表 */ }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["parts_list_count"] = lists.Count,
            ["parts_lists"] = lists,
            ["note"] = "V1 只报明细表存在性与名字；BOM 行文本抽取待客户装配/明细样件后接（BOM-BUY-001 缺证据即跳过）。",
        };
    }

    // ---- inspect_sheet_thickness：实体最小包围盒棱长启发（无钣金工具时的板厚代理） ----

    private static object InspectSheetThickness(JsonElement p)
    {
        var work = Work();
        var (thickness, bodyRecords) = ThicknessProbe(work);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["plate_thickness"] = thickness,
            ["method"] = "solid_bbox_min_dim",
            ["bodies"] = bodyRecords,
            ["note"] = "V1 无钣金/壁特征工具，板厚=实体最小包围盒棱长（平板类零件即厚度）；多实体取最小值，" +
                        "非平板实体的该启发值不代表工艺厚度，PLATE-THK-001 判定请结合零件类型解读。",
        };
    }

    /// <summary>板厚探测（工具与 extract_evidence 共用）：返回（最小实体棱长或 null，逐体记录）。</summary>
    private static (double? thickness, List<object?> records) ThicknessProbe(Part work)
    {
        var uf = NXOpen.UF.UFSession.GetUFSession();
        double? min = null;
        var records = new List<object?>();
        foreach (Body b in work.Bodies)
        {
            try
            {
                if (!b.IsSolidBody) continue;
                var box = new double[6];
                uf.Modl.AskBoundingBox(b.Tag, box);
                var dims = new[]
                {
                    box[3] - box[0], box[4] - box[1], box[5] - box[2],
                };
                double bodyMin = dims.Where(d => d > 1e-9).DefaultIfEmpty(double.MaxValue).Min();
                if (bodyMin == double.MaxValue) continue;
                records.Add(new Dictionary<string, object?>
                {
                    ["name"] = b.Name,
                    ["min_dim"] = Math.Round(bodyMin, 6),
                    ["edges"] = dims.Select(d => Math.Round(d, 6)).ToList(),
                });
                if (min == null || bodyMin < min) min = bodyMin;
            }
            catch { /* 单体失败跳过 */ }
        }
        return (min == null ? null : Math.Round(min.Value, 6), records);
    }

    // ---- inspect_hole_pattern：同直径同轴向孔的圆形分组探测（法兰 PCD 证据） ----

    private static object InspectHolePattern(JsonElement p)
    {
        var work = Work();
        var uf = NXOpen.UF.UFSession.GetUFSession();
        var shapes = CollectCylindricalHoles(uf, work);
        var patterns = DescribePatterns(shapes);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["hole_count"] = shapes.Count,
            ["patterns"] = patterns,
            ["note"] = "同直径同轴向 ≥3 孔且径向散布 <15% 视为圆形孔组，pcd=平均半径×2；V1 不创建阵列特征（§9.3），" +
                       "FLANGE-PCD-001 正路径待客户法兰样件（阵列件请从特征树读原阵列参数）。",
        };
    }

    private sealed class HoleShape
    {
        public string FeatureName = "";
        public double Diameter;
        public double[] Center = new double[3];
        public double[] Axis = new double[3];
        public bool FromArray;
    }

    private static List<HoleShape> CollectCylindricalHoles(NXOpen.UF.UFSession uf, Part work)
    {
        var list = new List<HoleShape>();
        foreach (NXOpen.Features.Feature f in work.Features)
        {
            string typeName;
            try
            {
                if (f.Suppressed) continue;
                typeName = f.GetType().Name;
            }
            catch { continue; }
            if (typeName is not ("Cylinder" or "HolePackage")) continue;
            foreach (var face in SafeFaces(f))
            {
                var (t, radius, point, dir) = AskFaceShapeFull(uf, face);
                if (t != UfCylindricalFace || radius <= 1e-9) continue;
                list.Add(new HoleShape
                {
                    FeatureName = f.Name,
                    Diameter = radius * 2.0,
                    Center = point,
                    Axis = NormalizeAxis(dir),
                    FromArray = f.IsOccurrence,
                });
                break; // 一特征一代表柱面，与孔证据口径一致
            }
        }
        return list;
    }

    private static double[] NormalizeAxis(double[] dir)
    {
        var d = (double[])dir.Clone();
        double mag = Math.Sqrt(d.Sum(v => v * v));
        if (mag <= 1e-12) return new[] { 0.0, 0.0, 0.0 };
        d = d.Select(v => v / mag).ToArray();
        int dominant = 0;
        for (int i = 1; i < 3; i++)
            if (Math.Abs(d[i]) > Math.Abs(d[dominant])) dominant = i;
        if (d[dominant] < 0) d = d.Select(v => -v).ToArray();
        return d.Select(v => Math.Round(v, 2)).ToArray();
    }

    private static List<object?> DescribePatterns(List<HoleShape> shapes)
    {
        var patterns = new List<object?>();
        var groups = shapes.GroupBy(h =>
            (Math.Round(h.Diameter, 3), string.Join(",", h.Axis)));
        foreach (var g in groups)
        {
            var members = g.ToList();
            if (members.Count < 3) continue;
            var centroid = new double[3];
            foreach (var m in members)
                for (int i = 0; i < 3; i++) centroid[i] += m.Center[i] / members.Count;
            var n = members[0].Axis;
            var radii = members.Select(m =>
            {
                var v = new double[3];
                for (int i = 0; i < 3; i++) v[i] = m.Center[i] - centroid[i];
                double dot = v.Select((vi, i) => vi * n[i]).Sum();
                for (int i = 0; i < 3; i++) v[i] -= dot * n[i];
                return Math.Sqrt(v.Sum(vi => vi * vi));
            }).ToList();
            double meanR = radii.Average();
            double spread = radii.Max(r => Math.Abs(r - meanR));
            if (meanR <= 1e-6 || spread > 0.15 * meanR) continue;
            patterns.Add(new Dictionary<string, object?>
            {
                ["name"] = "HOLE_GROUP_" + Math.Round(g.Key.Item1, 3).ToString("0.###") +
                           "_" + members.Count,
                ["pcd"] = Math.Round(2 * meanR, 3),
                ["hole_count"] = members.Count,
                ["hole_diameter"] = Math.Round(g.Key.Item1, 3),
                ["feature_names"] = members.Select(m => m.FeatureName).Distinct().ToList(),
                ["from_array_or_hole_set"] = members.All(m => m.FromArray),
                ["radial_spread"] = Math.Round(spread, 3),
            });
        }
        return patterns;
    }

    // ---- 工程图页共享记录（inspect_title_block/inspect_drawing_sheet/extract_evidence 同一口径） ----

    private static List<NXOpen.Drawings.DrawingSheet> SheetRecords(Part work)
    {
        var list = new List<NXOpen.Drawings.DrawingSheet>();
        try
        {
            foreach (NXOpen.Drawings.DrawingSheet sh in work.DrawingSheets)
                list.Add(sh);
        }
        catch { /* 无工程图环境按空 */ }
        return list;
    }

    private static int SheetCount(Part work) => SheetRecords(work).Count;

    private static Dictionary<string, string> SheetUserAttributes(NXOpen.Drawings.DrawingSheet sh)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var a in sh.GetUserAttributes())
            {
                try
                {
                    if (a.Type == NXOpen.NXObject.AttributeType.String && a.StringValue != null)
                        attrs[a.Title] = a.StringValue;
                }
                catch { /* 单属性读取失败跳过 */ }
            }
        }
        catch { /* 无属性 */ }
        return attrs;
    }

    private static object SheetGeomRecord(NXOpen.Drawings.DrawingSheet sh)
    {
        double num = 0, den = 0;
        try { sh.GetScale(out num, out den); } catch { /* 未设比例 */ }
        int viewCount = 0;
        try { viewCount = sh.GetDraftingViews().Length; } catch { /* 空页 */ }
        var attrs = SheetUserAttributes(sh);
        string Find(params string[] candidates) =>
            candidates.FirstOrDefault(c => attrs.ContainsKey(c)) is string key ? attrs[key] : "";
        return new Dictionary<string, object?>
        {
            ["name"] = sh.Name,
            ["journal_id"] = sh.JournalIdentifier,
            ["length"] = (double)sh.Length,
            ["height"] = (double)sh.Height,
            ["units"] = sh.Units.ToString(),
            ["projection_angle"] = sh.ProjectionAngle.ToString(),
            ["scale_numerator"] = num,
            ["scale_denominator"] = den,
            ["drafting_view_count"] = viewCount,
            ["frame_id"] = Find("图框", "FRAME", "DrawingFrame", "frame_id"),
            ["template_name"] = Find("模板", "图框模板", "TEMPLATE", "template_name"),
        };
    }

    private static object SheetAttrRecord(NXOpen.Drawings.DrawingSheet sh)
    {
        double num = 0, den = 0;
        try { sh.GetScale(out num, out den); } catch { /* 未设比例 */ }
        return new Dictionary<string, object?>
        {
            ["name"] = sh.Name,
            ["journal_id"] = sh.JournalIdentifier,
            ["scale"] = den > 1e-12 ? Math.Round(num / den, 6) : (double?)null,
            ["title_block_raw"] = SheetUserAttributes(sh),
        };
    }

    /// <summary>
    /// extract_evidence 的工程图段（无图纸页返回 null=缺证据，规则引擎自动跳过）。
    /// 多图纸页时标注/图层证据只归第一页并带 dimension_assignment_note（V1 无创建工具，单页是常态）。
    /// </summary>
    private static Dictionary<string, object?>? BuildDrawingEvidence(Part work)
    {
        var sheets = SheetRecords(work);
        if (sheets.Count == 0) return null;
        int weldCount = 0;
        try { weldCount = work.Annotations.Welds.ToArray().Length; }
        catch { /* 无焊缝环境 */ }
        var lists = new List<object?>();
        try
        {
            foreach (object pl in work.Annotations.PartsLists) lists.Add(pl);
        }
        catch { /* 无明细表 */ }
        var dims = DescribeDimensions(work);
        var annotationLayers = new List<string>();
        foreach (var rec in dims)
            if (rec is Dictionary<string, object?> rd && rd["layer"] is int ly &&
                !annotationLayers.Contains(ly.ToString()))
                annotationLayers.Add(ly.ToString());

        var sheetEvidence = new List<object?>();
        for (int i = 0; i < sheets.Count; i++)
        {
            var rec = (Dictionary<string, object?>)SheetGeomRecord(sheets[i]);
            var entry = new Dictionary<string, object?>
            {
                ["frame_id"] = rec["frame_id"],
                ["template_name"] = rec["template_name"],
                ["view_count"] = rec["drafting_view_count"],
                ["title_block_raw"] = SheetUserAttributes(sheets[i]),
                ["layers_used"] = i == 0 ? annotationLayers : new List<string>(),
                ["dimensions"] = i == 0
                    ? dims.Select(d => new Dictionary<string, object?>
                      {
                          ["layer"] = ((Dictionary<string, object?>)d)["layer"]?.ToString(),
                      }).ToList<object?>()
                    : new List<object?>(),
            };
            sheetEvidence.Add(entry);
        }
        return new Dictionary<string, object?>
        {
            ["sheets"] = sheetEvidence,
            ["has_weld_symbol"] = weldCount > 0,
            ["parts_list_count"] = lists.Count,
            ["parts_list"] = new List<object?>(),
            ["note"] = sheets.Count > 1
                ? "多图纸页：标注图层证据暂归第一页（V1 简化，正路径待客户样件校准）。"
                : null,
        };
    }

    // ---- undo_last_assistant_change ----

    private static object UndoLastAssistantChange(JsonElement p) => AssistantMarks.UndoLast(Work());
}
