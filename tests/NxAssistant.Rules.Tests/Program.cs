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

// ---- 批 5：标题栏别名表（title_block_fields 占位）+ 数字图层占位守卫 ----
var sheetAlias = new SheetEvidence { FrameId = "FRAME_A3_V3", ViewCount = 1 };
sheetAlias.TitleBlock["图样代号"] = "PX-001"; // 别名→drawing_no：解析成功才会进 TITLE-002 格式判定
sheetAlias.TitleBlock["材料"] = "40Cr";       // 别名→material：白名单外材料，解析成功才会报
var aliasF = RuleEngine.Evaluate(pack, new Evidence { Drawing = new DrawingEvidence { Sheets = { sheetAlias } } });
Check("别名表解析 drawing_no（格式报）", aliasF.Any(f => f.RuleId == "TITLE-002"));
Check("别名表解析 material（白名单报）", aliasF.Any(f => f.RuleId == "TITLE-004" && f.Value == "40Cr"));
Check("别名字段已填则不再算缺失",
    !aliasF.Any(f => f.RuleId == "TITLE-001" && f.Message.Contains("drawing_no")));

var sheetNum = new SheetEvidence { FrameId = "FRAME_A3_V3", ViewCount = 1 };
sheetNum.LayersUsed.Add("3");
sheetNum.Dimensions.Add(new DimensionEvidence { Layer = "3" });
var numF = RuleEngine.Evaluate(pack, new Evidence { Drawing = new DrawingEvidence { Sheets = { sheetNum } } });
Check("数字图层不误伤 LAYER-002", !numF.Any(f => f.RuleId == "LAYER-002"));
Check("数字图层 LAYER-001 只警告", numF.Any(f => f.RuleId == "LAYER-001" && f.Enforcement == "warn"));

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

// ===================== 第十三片：用户态覆盖层 rules_state =====================

// 1) 禁用规则：HOLE-DIA-001 关掉后 φ13.2 不再阻断，其他规则照旧
var st = new RulesState();
st.SetDisabled("HOLE-DIA-001", "光孔直径必须属于标准系列", true);
var packOff = RulePack.Load(packDir, st);
Check("覆盖层规则数 -1", packOff.Rules.Count == pack.Rules.Count - 1);
Check("禁用后 φ13.2 不再报 HOLE-DIA-001",
    !RuleEngine.Evaluate(packOff, ev1).Any(f => f.RuleId == "HOLE-DIA-001"));
Check("禁用不影响其他规则（螺纹 M14 仍阻断）",
    RuleEngine.Blocking(packOff, evM).Any(f => f.RuleId == "HOLE-THD-003"));

// 2) 参数替换：hole_series 增加 13.2 → φ13.2 放行（整文件替换语义）
var st2 = new RulesState();
var hsNode = System.Text.Json.Nodes.JsonNode.Parse(
    File.ReadAllText(Path.Combine(packDir, "hole_series.json"))!).AsObject();
hsNode["simple_through_holes"]!.AsArray().Add(13.2);
st2.SetDataOverride("hole_series.json", "hole_series.json 系列加入 13.2",
    System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(hsNode.ToJsonString()));
var packOn = RulePack.Load(packDir, st2);
Check("改参后 φ13.2 进系列放行",
    !RuleEngine.Evaluate(packOn, ev1).Any(f => f.RuleId == "HOLE-DIA-001"));
Check("改参不影响其他直径判定（φ11 仍合规）",
    !RuleEngine.Evaluate(packOn, new Evidence { Part = new PartEvidence { Holes = { Hole(11, "simple_through") } } })
        .Any(f => f.RuleId == "HOLE-DIA-001"));

// 3) 圆角系列已数据化（fillet_series.json）：加入 2.2 后不再告警
var st3 = new RulesState();
var fsNode = System.Text.Json.Nodes.JsonNode.Parse(
    File.ReadAllText(Path.Combine(packDir, "fillet_series.json"))!).AsObject();
fsNode["radius_series"]!.AsArray().Add(2.2);
st3.SetDataOverride("fillet_series.json", "fillet_series.json 系列加入 2.2",
    System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(fsNode.ToJsonString()));
var packFs = RulePack.Load(packDir, st3);
Check("圆角系列改参生效（2.2 不再告警）",
    !RuleEngine.Evaluate(packFs, evM).Any(f => f.RuleId == "FEAT-FILLET-002"));

// 4) 落盘往返 + 变更留痕（禁用与改参都要能在日志里回答"谁关的/改了什么"）
var statePath = Path.Combine(Path.GetTempPath(), "nxa_rules_test_" + Guid.NewGuid().ToString("N") + ".json");
try
{
    st2.Save(statePath);
    var back = RulesState.Load(statePath);
    Check("覆盖层保存/重载一致",
        back.TryDataOverride("hole_series.json", out _) &&
        System.Text.Json.JsonSerializer.Serialize(back.Data) ==
            System.Text.Json.JsonSerializer.Serialize(st2.Data));
    Check("改参留痕", back.Log.Any(e => e.Action == "data_override" && e.Detail.Contains("13.2")));
    st.Save(statePath);
    var backOff = RulesState.Load(statePath);
    Check("禁用留痕 + 重载认账",
        backOff.IsDisabled("HOLE-DIA-001") && backOff.Log.Any(e => e.Action == "disable"));
}
finally { File.Delete(statePath); }

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
