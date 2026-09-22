using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NxAssistant.Rules;

/// <summary>
/// 规则引擎：按规则包内启用的规则逐条判定证据，产出 findings。
/// 判定逻辑内建在代码中（按 rule id 派发），规则是否启用/严重级/数据表来自 JSON；
/// 缺对应证据段则跳过（返回 0 条），因此同一引擎可用于建模写前拦截与审图。
/// </summary>
public static class RuleEngine
{
    private const double Eps = 1e-6;

    public static List<Finding> Evaluate(RulePack pack, Evidence ev)
    {
        var findings = new List<Finding>();
        foreach (var rule in pack.Rules)
        {
            var enforcement = ResolveEnforcement(pack, rule);
            if (enforcement == "off") continue;
            try
            {
                foreach (var f in Dispatch(pack, rule, ev, enforcement))
                    findings.Add(f);
            }
            catch (Exception e)
            {
                // 规则判定异常不应中断整轮审图：降级为一条 warn 提示，交人工。
                findings.Add(new Finding
                {
                    RuleId = rule.Id, Standard = rule.Standard, Group = rule.Group, Name = rule.Name,
                    Severity = "warn", Enforcement = "warn",
                    Message = $"规则判定异常，转人工：{e.Message}",
                });
            }
        }
        return findings;
    }

    /// <summary>写前拦截用：只保留会阻断（block 且 fail）的判定。</summary>
    public static List<Finding> Blocking(RulePack pack, Evidence ev) =>
        Evaluate(pack, ev).Where(f => f.IsBlocking).ToList();

    private static string ResolveEnforcement(RulePack pack, RuleDef rule)
    {
        if (rule.Severity == "warn") return "warn";
        var def = pack.DefaultEnforcement;
        return string.IsNullOrEmpty(def) ? "block" : def;
    }

    private static IEnumerable<Finding> Dispatch(RulePack pack, RuleDef r, Evidence ev, string enf)
    {
        var part = ev.Part;
        var drawing = ev.Drawing;
        switch (r.Id)
        {
            // ---- 孔 ----
            case "HOLE-DIA-001": return HoleDiameter(pack, r, ev, enf);
            case "HOLE-BOLT-002": return BoltClearance(pack, r, ev, enf);
            case "HOLE-THD-003": return ThreadWhitelist(pack, r, ev, enf);
            case "HOLE-DUP-004": return DuplicateHoles(r, ev, enf);
            // ---- 草图 / 特征 ----
            case "SKETCH-001": return ExtrudeSketchFinished(r, ev, enf);
            case "FEAT-EXT-001": return ExtrudeDistance(r, ev, enf);
            case "FEAT-FILLET-002": return FilletRadius(pack, r, ev, enf);
            // ---- 实体 ----
            case "BODY-001": return SolidExists(r, part, enf);
            case "BODY-002": return StraySheetBodies(r, part, enf);
            // ---- 工程图 / 标题栏 / 图层 ----
            case "FRAME-001": return FrameAllowed(pack, r, drawing, enf);
            case "TITLE-001": return TitleRequiredFields(pack, r, drawing, enf);
            case "TITLE-002": return TitleRegex(pack, r, drawing, enf, "drawing_no", "drawing_no_regex", "图号");
            case "TITLE-003": return TitleRegex(pack, r, drawing, enf, "revision", "revision_regex", "版本号");
            case "TITLE-004": return MaterialWhitelist(pack, r, drawing, enf);
            case "LAYER-001": return LayersAllowed(pack, r, drawing, enf);
            case "LAYER-002": return DimensionsOnLayer(pack, r, drawing, enf);
            case "VIEW-001": return HasView(r, drawing, enf);
            // ---- 华恒子包 ----
            case "PLATE-THK-001": return PlateThickness(pack, r, part, enf);
            case "STEEL-PROF-001": return SteelProfile(pack, r, part, enf);
            case "WELD-ANN-001": return WeldSymbol(pack, r, drawing, enf);
            case "FLANGE-PCD-001": return FlangePattern(pack, r, part, enf);
            case "PIN-BORE-001": return PinBore(pack, r, part, enf);
            case "SENSOR-HOLE-001": return SensorHole(pack, r, part, enf);
            // FILE-001/DIM-001/BOM-BUY-001/EDGE-SAFE-001/SKETCH-002：需审图工作区上下文或 V1 暂不判定的证据，留待相应批次；无证据即跳过。
            default: return Array.Empty<Finding>();
        }
    }

    // ===================== 孔 =====================

    private static IEnumerable<Finding> HoleDiameter(RulePack pack, RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        var data = pack.Data(r, "hole_series");
        var through = Dou(data, "simple_through_holes");
        var blind = Dou(data, "simple_blind_holes");
        foreach (var h in ev.Part.Holes)
        {
            var series = h.Kind == "simple_blind" ? blind : through;
            if (h.Kind != "simple_through" && h.Kind != "simple_blind") continue;
            if (series.Any(s => Math.Abs(s - h.Diameter) < Eps)) continue;
            yield return Make(r, enf, WithObj(h.Source == "model" ? "模型孔" : "图纸孔注释", h),
                Num(h.Diameter) + "mm",
                $"孔直径 {Num(h.Diameter)} 不在标准系列内",
                NearestTwo(series, h.Diameter).Select(Num).ToList(),
                IsPlaceholder(pack, r, "hole_series"));
        }
    }

    private static IEnumerable<Finding> BoltClearance(RulePack pack, RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        var data = pack.Data(r, "hole_series");
        if (!data.TryGetProperty("clearance_holes_for_metric_bolt", out var table)) yield break;
        foreach (var h in ev.Part.Holes.Where(h => h.Kind == "clearance_for_bolt"))
        {
            var bolt = h.NominalBolt ?? h.ThreadLabel ?? "";
            if (!table.TryGetProperty(bolt, out var row))
            {
                yield return Make(r, enf, WithObj($"螺栓通孔 {bolt}", h), Num(h.Diameter) + "mm",
                    $"未知公称螺纹 {bolt}，无法核对通孔直径", new List<string> { "确认螺栓规格是否在 M3–M16" });
                continue;
            }
            var medium = Scalar(row, "medium");
            var coarse = Scalar(row, "coarse");
            if (Math.Abs(medium - h.Diameter) < Eps || Math.Abs(coarse - h.Diameter) < Eps) continue;
            yield return Make(r, enf, WithObj($"螺栓 {bolt} 通孔", h), Num(h.Diameter) + "mm",
                $"{bolt} 通孔直径应为中等/粗制系列之一", new[] { Num(medium), Num(coarse) }.ToList());
        }
    }

    private static IEnumerable<Finding> ThreadWhitelist(RulePack pack, RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        var allowed = Strs(pack.Data(r, "hole_series"), "thread_holes");
        foreach (var h in ev.Part.Holes.Where(h => h.Kind == "threaded"))
        {
            var t = h.ThreadLabel ?? "";
            if (allowed.Contains(t, StringComparer.OrdinalIgnoreCase)) continue;
            yield return Make(r, enf, WithObj("螺纹孔", h), t,
                $"螺纹规格 {t} 不在公司白名单（M3–M16 粗牙）内", allowed.ToList());
        }
    }

    private static IEnumerable<Finding> DuplicateHoles(RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        var groups = ev.Part.Holes
            .Where(h => h.Source == "model" && !h.FromArrayOrHoleSet)
            .GroupBy(h => Math.Round(h.Diameter, 4))
            .Where(g => g.Count() >= 4);
        foreach (var g in groups)
        {
            var names = g.Select(h => h.FeatureName).Where(n => !string.IsNullOrEmpty(n)).ToList();
            yield return Make(r, "warn", "重复孔" + (names.Count > 0 ? "（" + string.Join("、", names) + "）" : ""),
                Num(g.Key) + "mm",
                $"直径 {Num(g.Key)} 的独立孔特征有 {g.Count()} 个，建议改用阵列或孔特征组",
                new List<string> { "使用矩形/圆形阵列或孔特征组" });
        }
    }

    // ===================== 草图 / 特征 =====================

    private static IEnumerable<Finding> ExtrudeSketchFinished(RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        foreach (var x in ev.Part.Extrudes)
        {
            if (x.SketchStatus == null) continue;
            if (string.Equals(x.SketchStatus, "Finished", StringComparison.OrdinalIgnoreCase)) continue;
            yield return Make(r, enf, "成型草图", x.SketchStatus,
                "用于成型的草图未完全约束（状态非 Finished），禁止成型",
                new List<string> { "补全约束使草图尺寸完全定义" });
        }
    }

    private static IEnumerable<Finding> ExtrudeDistance(RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        foreach (var x in ev.Part.Extrudes)
        {
            if (x.Distance == null) continue;
            var d = x.Distance.Value;
            if (!double.IsNaN(d) && !double.IsInfinity(d) && d > 0) continue;
            yield return Make(r, enf, "拉伸特征", Num(d),
                "拉伸距离必须为正且有限", new List<string> { "distance > 0" });
        }
    }

    private static IEnumerable<Finding> FilletRadius(RulePack pack, RuleDef r, Evidence ev, string enf)
    {
        if (ev.Part == null) yield break;
        // 系列数据在 fillet_series.json（可被用户覆盖层整文件替换）；旧包缺该文件时回落内建值。
        var series = pack.TryData(r, "fillet_series", out var data)
            ? Dou(data, "radius_series")
            : new List<double> { 0.5, 1.0, 1.5, 2.0, 3.0, 5.0 };
        foreach (var f in ev.Part.Fillets)
        {
            if (f.Radius == null) continue;
            if (series.Any(s => Math.Abs(s - f.Radius.Value) < Eps)) continue;
            yield return Make(r, "warn", "圆角特征", Num(f.Radius.Value) + "mm",
                "圆角半径不在建议系列（" + string.Join("/", series.Select(Num)) + "）",
                NearestTwo(series, f.Radius.Value).Select(Num).ToList());
        }
    }

    // ===================== 实体 =====================

    private static IEnumerable<Finding> SolidExists(RuleDef r, PartEvidence? part, string enf)
    {
        if (part?.SolidBodyCount == null) yield break;
        if (part.SolidBodyCount == 0)
            yield return Make(r, enf, "工作部件", "0", "工作件不含任何实体（空图）", new List<string>());
    }

    private static IEnumerable<Finding> StraySheetBodies(RuleDef r, PartEvidence? part, string enf)
    {
        if (part == null) yield break;
        var stray = part.SheetBodies.Count(s => !s.IsProcessAux);
        if (stray > 0)
            yield return Make(r, "warn", "片体", stray.ToString(),
                $"存在 {stray} 个未命名为工艺辅助的多余片体", new List<string> { "删除或命名工艺辅助片体" });
    }

    // ===================== 工程图 / 标题栏 / 图层 =====================

    private static IEnumerable<Finding> FrameAllowed(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        var data = pack.Data(r, "drawing_frames");
        var ids = Strs(data, "allowed_frame_ids");
        var names = Strs(data, "allowed_template_names");
        foreach (var s in dw.Sheets)
        {
            if (ids.Contains(s.FrameId) || names.Contains(s.TemplateName)) continue;
            yield return Make(r, enf, "图框", s.FrameId + "/" + s.TemplateName,
                "未使用公司图框模板", ids.ToList(), IsPlaceholder(pack, r, "drawing_frames"));
        }
    }

    private static IEnumerable<Finding> TitleRequiredFields(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        var required = Strs(pack.Data(r, "drawing_frames"), "required_title_block_fields");
        foreach (var s in dw.Sheets)
        {
            var missing = required.Where(k => string.IsNullOrWhiteSpace(TitleField(pack, r, s, k))).ToList();
            if (missing.Count == 0) continue;
            yield return Make(r, enf, "标题栏", "sheet",
                "标题栏必填字段缺失/为空：" + string.Join("、", missing), missing.ToList());
        }
    }

    /// <summary>
    /// 规范字段名 → 图纸页属性标题：先按字段名本身（忽略大小写），再查 title_block_fields.json
    /// 别名表（占位行业惯用名，待客户真实图样属性标题确认后替换）；查不到返回空串。
    /// </summary>
    public static string TitleField(RulePack pack, RuleDef r, SheetEvidence s, string field)
    {
        foreach (var kv in s.TitleBlock)
            if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kv.Value))
                return kv.Value.Trim();
        if (pack.TryData(r, "title_block_fields", out var data) &&
            data.TryGetProperty("field_titles", out var ft) &&
            ft.TryGetProperty(field, out var aliases))
            foreach (var a in aliases.EnumerateArray())
            {
                var title = a.GetString();
                if (title == null) continue;
                foreach (var kv in s.TitleBlock)
                    if (string.Equals(kv.Key, title, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(kv.Value))
                        return kv.Value.Trim();
            }
        return "";
    }

    /// <summary>宿主报告行取真实图号/版本用：以 TITLE-001 为锚解析别名表。</summary>
    public static string TitleField(RulePack pack, SheetEvidence s, string field)
    {
        var anchor = pack.Rules.FirstOrDefault(x => x.Id == "TITLE-001");
        return anchor == null ? "" : TitleField(pack, anchor, s, field);
    }

    private static IEnumerable<Finding> TitleRegex(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf,
        string field, string regexKey, string label)
    {
        if (dw == null) yield break;
        var pattern = pack.Data(r, "part_number_pattern").GetProperty(regexKey).GetString() ?? "";
        System.Text.RegularExpressions.Regex rx;
        try { rx = new System.Text.RegularExpressions.Regex(pattern); }
        catch { yield break; }
        foreach (var s in dw.Sheets)
        {
            var val = TitleField(pack, r, s, field);
            if (string.IsNullOrEmpty(val)) continue; // 缺失由 TITLE-001 管
            if (rx.IsMatch(val)) continue;
            yield return Make(r, enf, label, val, $"字段 {field} 不符合编码格式 /{pattern}/");
        }
    }

    private static IEnumerable<Finding> MaterialWhitelist(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        var allowed = Strs(pack.Data(r, "part_number_pattern"), "material_whitelist");
        foreach (var s in dw.Sheets)
        {
            var m = TitleField(pack, r, s, "material");
            if (string.IsNullOrEmpty(m)) continue;
            if (allowed.Contains(m, StringComparer.Ordinal)) continue;
            yield return Make(r, enf, "材料", m, "材料不在公司白名单内", allowed.ToList());
        }
    }

    private static IEnumerable<Finding> LayersAllowed(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        var data = pack.Data(r, "layers");
        var allowed = Strs(data, "allowed_layers");
        var forbidden = Strs(data, "forbidden_layers");
        foreach (var s in dw.Sheets)
            foreach (var layer in s.LayersUsed.Distinct())
            {
                if (allowed.Contains(layer)) continue;
                if (forbidden.Contains(layer))
                    yield return Make(r, enf, "图层", layer, $"使用了禁用图层 {layer}", allowed.ToList());
                else
                    yield return Make(r, "warn", "图层", layer, $"未知图层 {layer}（不在登记列表），请人工确认", allowed.ToList());
            }
    }

    private static IEnumerable<Finding> DimensionsOnLayer(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        var must = Strs(pack.Data(r, "layers"), "dimension_must_on");
        foreach (var s in dw.Sheets)
            foreach (var d in s.Dimensions)
            {
                if (string.IsNullOrEmpty(d.Layer)) continue;
                // 占位守卫：NX 侧图层名解析待客户样件校准，纯数字图层不做阻断判定。
                if (int.TryParse(d.Layer, out _)) continue;
                if (must.Contains(d.Layer)) continue;
                yield return Make(r, enf, "尺寸", d.Layer, "尺寸不在 DIM 层", must.ToList());
            }
    }

    private static IEnumerable<Finding> HasView(RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw == null) yield break;
        foreach (var s in dw.Sheets)
        {
            if (s.ViewCount == null) continue;
            if (s.ViewCount > 0) continue;
            yield return Make(r, enf, "图幅视图", "0", "图纸不含任何投影视图", new List<string>());
        }
    }

    // ===================== 华恒子包 =====================

    private static IEnumerable<Finding> PlateThickness(RulePack pack, RuleDef r, PartEvidence? part, string enf)
    {
        if (part?.PlateThickness == null) yield break;
        var allowed = Dou(pack.Data(r, "plate_thickness"), "allowed_thickness");
        var t = part.PlateThickness.Value;
        if (allowed.Any(a => Math.Abs(a - t) < Eps)) yield break;
        yield return Make(r, enf, "板厚", Num(t) + "mm", "板厚不在公司系列内",
            NearestTwo(allowed, t).Select(Num).ToList(), IsPlaceholder(pack, r, "plate_thickness"));
    }

    private static IEnumerable<Finding> SteelProfile(RulePack pack, RuleDef r, PartEvidence? part, string enf)
    {
        if (part == null || part.SteelProfileDesignations.Count == 0) yield break;
        var data = pack.Data(r, "steel_profiles");
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in data.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Array)
                foreach (var e in prop.Value.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) allowed.Add(e.GetString()!);
        foreach (var d in part.SteelProfileDesignations)
        {
            if (allowed.Contains(d)) continue;
            yield return Make(r, "warn", "型钢", d, "型钢/方管规格不在清单内", new List<string>(), IsPlaceholder(pack, r, "steel_profiles"));
        }
    }

    private static IEnumerable<Finding> WeldSymbol(RulePack pack, RuleDef r, DrawingEvidence? dw, string enf)
    {
        if (dw?.HasWeldSymbol == null) yield break;
        var policy = pack.Data(r, "weld_policy");
        var must = policy.TryGetProperty("drawing_must_have_weld_symbol", out var m) && m.GetBoolean();
        if (!must || dw.HasWeldSymbol.Value) yield break;
        var sev = policy.TryGetProperty("severity_if_missing", out var sv) ? sv.GetString() ?? "warn" : "warn";
        yield return Make(r, sev == "fail" ? pack.DefaultEnforcement : "warn", "焊缝标注", "",
            "工程图缺少焊缝符号", new List<string>(), IsPlaceholder(pack, r, "weld_policy"));
    }

    private static IEnumerable<Finding> FlangePattern(RulePack pack, RuleDef r, PartEvidence? part, string enf)
    {
        if (part == null || part.HolePatterns.Count == 0) yield break;
        var patterns = pack.Data(r, "flange_patterns").GetProperty("patterns");
        foreach (var hp in part.HolePatterns)
        {
            bool matched = false;
            foreach (var p in patterns.EnumerateArray())
            {
                if (Near(p, "pcd", hp.Pcd) && NearInt(p, "hole_count", hp.HoleCount) &&
                    Near(p, "hole_diameter", hp.HoleDiameter) &&
                    Str(p, "bolt").Equals(hp.Bolt ?? "", StringComparison.OrdinalIgnoreCase))
                { matched = true; break; }
            }
            if (matched) continue;
            yield return Make(r, enf, "法兰孔组", hp.Name,
                "驱动/舵轮法兰孔组不匹配任何样本（PCD/孔数/孔径/螺栓）",
                SampleHints(patterns), IsPlaceholder(pack, r, "flange_patterns"));
        }
    }

    private static IEnumerable<Finding> PinBore(RulePack pack, RuleDef r, PartEvidence? part, string enf)
    {
        if (part == null) yield break;
        var allowed = Dou(pack.Data(r, "pin_bore"), "pin_diameters");
        foreach (var h in part.Holes.Where(h => h.Kind == "pin"))
        {
            if (allowed.Any(a => Math.Abs(a - h.Diameter) < Eps)) continue;
            yield return Make(r, enf, WithObj("销轴/轮轴孔", h), Num(h.Diameter) + "mm", "销轴孔径不在系列内",
                NearestTwo(allowed, h.Diameter).Select(Num).ToList(), IsPlaceholder(pack, r, "pin_bore"));
        }
    }

    private static IEnumerable<Finding> SensorHole(RulePack pack, RuleDef r, PartEvidence? part, string enf)
    {
        if (part == null) yield break;
        var data = pack.Data(r, "sensor_holes");
        var clearance = Dou(data, "allowed_clearance");
        var threads = Strs(data, "allowed_thread");
        foreach (var h in part.Holes.Where(h => h.Kind == "sensor"))
        {
            var okDia = clearance.Any(c => Math.Abs(c - h.Diameter) < Eps);
            var okThread = (h.ThreadLabel ?? "") != "" && threads.Contains(h.ThreadLabel!, StringComparer.OrdinalIgnoreCase);
            if (okDia || okThread) continue;
            yield return Make(r, "warn", WithObj("传感器安装孔", h), Num(h.Diameter) + "mm",
                "传感器安装孔不在允许通孔/螺纹系列（仅卡安装孔径，不查激光视野）",
                clearance.Select(Num).Concat(threads).ToList(), IsPlaceholder(pack, r, "sensor_holes"));
        }
    }

    // ===================== helpers =====================

    private static Finding Make(RuleDef r, string enf, string subject, string? value, string message,
        IReadOnlyList<string>? suggestions = null, bool placeholder = false) => new()
    {
        RuleId = r.Id, Standard = r.Standard, Group = r.Group, Name = r.Name,
        Severity = r.Severity, Enforcement = enf, Subject = subject, Value = value,
        Message = message, Suggestions = suggestions ?? Array.Empty<string>(), Placeholder = placeholder,
    };

    /// <summary>finding 主题带上孔特征名（§7.5 报告行需要对象 id）；无名则维持原主题。</summary>
    private static string WithObj(string subject, HoleEvidence h) =>
        string.IsNullOrEmpty(h.FeatureName) ? subject : $"{subject}（{h.FeatureName}）";

    private static List<double> NearestTwo(IEnumerable<double> series, double value)
    {
        var s = series.OrderBy(x => x).ToList();
        if (s.Count == 0) return new List<double>();
        var below = s.LastOrDefault(x => x <= value + Eps);
        var above = s.FirstOrDefault(x => x >= value - Eps);
        var set = new List<double>();
        if (below > 0 || s[0] == 0) set.Add(below);
        if (above > 0 && Math.Abs(above - below) > Eps) set.Add(above);
        return set.Distinct().ToList();
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static double Scalar(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;

    private static List<double> Dou(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return new List<double>();
        var list = new List<double>();
        foreach (var x in arr.EnumerateArray())
            if (x.ValueKind == JsonValueKind.Number) list.Add(x.GetDouble());
        return list;
    }

    private static List<string> Strs(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return new List<string>();
        return arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).ToList();
    }

    private static bool Near(JsonElement p, string prop, double? value)
    {
        if (value == null) return !p.TryGetProperty(prop, out _);
        return p.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && Math.Abs(e.GetDouble() - value.Value) < Eps;
    }

    private static bool NearInt(JsonElement p, string prop, int? value)
    {
        if (value == null) return !p.TryGetProperty(prop, out _);
        return p.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number && e.GetInt32() == value.Value;
    }

    private static string Str(JsonElement p, string prop) =>
        p.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";

    private static List<string> SampleHints(JsonElement patterns)
    {
        var list = new List<string>();
        foreach (var p in patterns.EnumerateArray())
            list.Add($"{Str(p, "id")}: PCD={NumOr(p, "pcd")} 孔数={NumOr(p, "hole_count")} 孔径={NumOr(p, "hole_diameter")} {Str(p, "bolt")}");
        return list;
    }

    private static string NumOr(JsonElement p, string prop) =>
        p.TryGetProperty(prop, out var e) && e.ValueKind == JsonValueKind.Number ? Num(e.GetDouble()) : "?";

    private static bool IsPlaceholder(RulePack pack, RuleDef r, string dataFile)
    {
        try
        {
            var d = pack.Data(r, dataFile);
            return d.TryGetProperty("placeholder", out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }
}
