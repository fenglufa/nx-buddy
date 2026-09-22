using NxAssistant.Rules;

// 规则引擎离线金样测试。以退出码 0/1 表示通过/失败，风格同 build/smoke_*.py。
var packDir = FindPackDir();
var pack = RulePack.Load(packDir);
Console.WriteLine($"已加载规则包 {pack.Id} v{pack.Version}，规则数={pack.Rules.Count}，默认强制={pack.DefaultEnforcement}");

int failed = 0;
void Check(string name, bool cond)
{
    Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
    if (!cond) failed++;
}

// ---- 加载：主包 20 + 华恒子包 8 = 28 条规则；含两个标准 ----
Check("规则总数=28", pack.Rules.Count == 28);
Check("含 company_v3 规则", pack.Rules.Any(r => r.Standard == "company_v3"));
Check("含华恒子包规则", pack.Rules.Any(r => r.Standard == "huaheng_logistics_robot_v1"));

// ---- 金样 1：φ13.2 光孔必须 FAIL 且建议邻近 13/14 ----
var ev1 = new Evidence { Part = new PartEvidence { Holes = { Hole(13.2, "simple_through") } } };
var f1 = RuleEngine.Evaluate(pack, ev1).Where(f => f.RuleId == "HOLE-DIA-001").ToList();
Check("φ13.2 触发 HOLE-DIA-001", f1.Count == 1);
Check("φ13.2 为阻断级(block)", f1.Count == 1 && f1[0].IsBlocking);
Check("φ13.2 建议含 13 和 14", f1.Count == 1 && f1[0].Suggestions.Contains("13") && f1[0].Suggestions.Contains("14"));

// ---- 金样 2：系列内 φ10 光孔不报 HOLE-DIA-001 ----
var ev2 = new Evidence { Part = new PartEvidence { Holes = { Hole(10, "simple_through") } } };
Check("φ10 合规", !RuleEngine.Evaluate(pack, ev2).Any(f => f.RuleId == "HOLE-DIA-001"));

// ---- 螺栓通孔：M8 中等=9.0 合规；9.5 非法 -> 只报一条 ----
var ev3 = new Evidence { Part = new PartEvidence { Holes = {
    new HoleEvidence { Diameter = 9.0, Kind = "clearance_for_bolt", NominalBolt = "M8" },
    new HoleEvidence { Diameter = 9.5, Kind = "clearance_for_bolt", NominalBolt = "M8" } } } };
var bolt = RuleEngine.Evaluate(pack, ev3).Where(f => f.RuleId == "HOLE-BOLT-002").ToList();
Check("M8 通孔只报非法的那条", bolt.Count == 1 && bolt[0].Value == "9.5mm");

// ---- 螺纹白名单 / 重复孔 / 草图 / 拉伸 / 圆角 ----
var evM = new Evidence { Part = new PartEvidence
{
    Holes = { new HoleEvidence { Diameter = 11, Kind = "threaded", ThreadLabel = "M14" } },
    Extrudes = { new ExtrudeEvidence { Distance = -2, SketchStatus = "Partially constrained" } },
    Fillets = { new FilletEvidence { Radius = 2.2 } },
} };
var mf = RuleEngine.Evaluate(pack, evM);
Check("螺纹 M14 超白名单报", mf.Any(f => f.RuleId == "HOLE-THD-003" && f.IsBlocking));
Check("草图未完成阻断成型(SKETCH-001)", mf.Any(f => f.RuleId == "SKETCH-001" && f.IsBlocking));
Check("负拉伸距离报(FEAT-EXT-001)", mf.Any(f => f.RuleId == "FEAT-EXT-001" && f.IsBlocking));
Check("圆角 2.2 仅警告(FEAT-FILLET-002)", mf.Any(f => f.RuleId == "FEAT-FILLET-002" && f.Enforcement == "warn"));

var evDup = new Evidence { Part = new PartEvidence { Holes = {
    Hole(10, "simple_through"), Hole(10, "simple_through"), Hole(10, "simple_through"), Hole(10, "simple_through") } } };
Check("4 个同径独立孔警告", RuleEngine.Evaluate(pack, evDup).Any(f => f.RuleId == "HOLE-DUP-004" && f.Enforcement == "warn"));

// ---- 标题栏：图号/版本/材料/必填 + 图框 + 视图 ----
var sheet = new SheetEvidence { FrameId = "FRAME_A3_V3", ViewCount = 1 };
sheet.TitleBlock["drawing_no"] = "PX-001";   // 不匹配 P-#####-##
sheet.TitleBlock["revision"] = "1";           // 不匹配 A / A.2
sheet.TitleBlock["material"] = "A3钢";        // 不在白名单
// 缺 title/scale/drawn_by/date
var evD = new Evidence { Drawing = new DrawingEvidence { Sheets = { sheet } } };
var df = RuleEngine.Evaluate(pack, evD).GroupBy(f => f.RuleId).ToDictionary(g => g.Key, g => g.Count());
Check("TITLE-001 缺字段报", df.GetValueOrDefault("TITLE-001") == 1);
Check("TITLE-002 图号格式报", df.GetValueOrDefault("TITLE-002") == 1);
Check("TITLE-003 版本格式报", df.GetValueOrDefault("TITLE-003") == 1);
Check("TITLE-004 材料报", df.GetValueOrDefault("TITLE-004") == 1);
Check("FRAME-001 合规图框不报", !df.ContainsKey("FRAME-001"));

// ---- 图层：禁用层 DEFAULT 阻断；未知层仅警告；尺寸不在 DIM 报（标识在 Value）----
var sheet2 = new SheetEvidence { FrameId = "FRAME_A3_V3", ViewCount = 2 };
sheet2.LayersUsed.AddRange(new[] { "DIM", "DEFAULT", "神秘层" });
sheet2.Dimensions.Add(new DimensionEvidence { Layer = "OUTLINE" });
var layerF = RuleEngine.Evaluate(pack, new Evidence { Drawing = new DrawingEvidence { Sheets = { sheet2 } } });
Check("LAYER-001 禁用层阻断", layerF.Any(f => f.RuleId == "LAYER-001" && f.Value == "DEFAULT" && f.IsBlocking));
Check("LAYER-001 未知层仅警告", layerF.Any(f => f.RuleId == "LAYER-001" && f.Value == "神秘层" && f.Enforcement == "warn"));
Check("LAYER-002 尺寸错层报", layerF.Any(f => f.RuleId == "LAYER-002" && f.Value == "OUTLINE"));

// ---- 实体 / 视图 ----
var evB = new Evidence
{
    Part = new PartEvidence { SolidBodyCount = 0, SheetBodies = { new SheetBodyEvidence { Name = "片体1" } } },
    Drawing = new DrawingEvidence { Sheets = { new SheetEvidence { ViewCount = 0 } } },
};
var bf = RuleEngine.Evaluate(pack, evB);
Check("空图 BODY-001 阻断", bf.Any(f => f.RuleId == "BODY-001" && f.IsBlocking));
Check("多余片体 BODY-002 警告", bf.Any(f => f.RuleId == "BODY-002" && f.Enforcement == "warn"));
Check("无视图 VIEW-001 阻断", bf.Any(f => f.RuleId == "VIEW-001" && f.IsBlocking));

// ---- 华恒：板厚占位系列 6 合规 / 7 报（并标记 placeholder）----
var evP = new Evidence { Part = new PartEvidence { PlateThickness = 7 } };
var plate = RuleEngine.Evaluate(pack, evP).FirstOrDefault(f => f.RuleId == "PLATE-THK-001");
Check("板厚7报且建议邻近", plate != null && plate.Suggestions.Contains("6") && plate.Suggestions.Contains("8"));
Check("板厚规则标记 placeholder", plate != null && plate.Placeholder);

// ---- 法兰孔组：匹配样本不报；乱给报（标识在 Value）----
var goodPat = new HolePatternEvidence { Name = "F1", Pcd = 80, HoleCount = 4, HoleDiameter = 9.0, Bolt = "M8" };
var badPat = new HolePatternEvidence { Name = "F2", Pcd = 77, HoleCount = 5, HoleDiameter = 8.5, Bolt = "M7" };
var evF = new Evidence { Part = new PartEvidence { HolePatterns = { goodPat, badPat } } };
var flange = RuleEngine.Evaluate(pack, evF).Where(f => f.RuleId == "FLANGE-PCD-001").ToList();
Check("法兰只报不匹配的那个", flange.Count == 1 && flange[0].Value == "F2");

// ---- 销轴 / 传感器 / 焊缝 / 型钢 ----
var evH = new Evidence
{
    Part = new PartEvidence
    {
        Holes = {
            new HoleEvidence { Diameter = 15, Kind = "pin" },
            new HoleEvidence { Diameter = 5.0, Kind = "sensor" } },
        SteelProfileDesignations = { "H300x150", "H200x100" },
    },
    Drawing = new DrawingEvidence { HasWeldSymbol = false },
};
var hf = RuleEngine.Evaluate(pack, evH);
Check("销轴孔 15 不在系列报(PIN-BORE-001)", hf.Any(f => f.RuleId == "PIN-BORE-001" && f.Value == "15mm"));
Check("传感器孔 5.0 警告(SENSOR-HOLE-001)", hf.Any(f => f.RuleId == "SENSOR-HOLE-001" && f.Enforcement == "warn"));
Check("缺焊缝符号警告(WELD-ANN-001)", hf.Any(f => f.RuleId == "WELD-ANN-001" && f.Enforcement == "warn"));
Check("型钢 H300x150 警告、H200x100 放行", hf.Count(f => f.RuleId == "STEEL-PROF-001") == 1
    && hf.Single(f => f.RuleId == "STEEL-PROF-001").Value == "H300x150");

// ---- 异常/未知输入隔离：不抛出，产出人工项 ----
var evX = new Evidence { Part = new PartEvidence { Holes = { new HoleEvidence { Diameter = 9, Kind = "clearance_for_bolt", NominalBolt = "M99" } } } };
Check("未知螺纹规格不抛出并报告警", RuleEngine.Evaluate(pack, evX).Any(f => f.RuleId == "HOLE-BOLT-002"));

// ---- Blocking 过滤：只保留 block 级 ----
var all = RuleEngine.Evaluate(pack, evD);
Check("Blocking 过滤正确", all.Count >= RuleEngine.Blocking(pack, evD).Count
    && RuleEngine.Blocking(pack, evD).All(f => f.IsBlocking));

Console.WriteLine(failed == 0 ? "\nRULES TESTS PASS" : $"\n{failed} RULES TESTS FAILED");
return failed == 0 ? 0 : 1;

static HoleEvidence Hole(double dia, string kind) => new() { Diameter = dia, Kind = kind };

static string FindPackDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        var cand = Path.Combine(dir.FullName, "docs", "company_v3");
        if (Directory.Exists(cand)) return cand;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("找不到 docs/company_v3 规则包目录");
}
