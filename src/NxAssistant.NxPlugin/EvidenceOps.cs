using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NXOpen;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 审图证据抽取（内部方法 extract_evidence，供宿主 review_folder 长任务调用）。
/// 在 FILE-001 沙箱内按路径打开 .prt（非显示打开、只读抽取、抽完即关），产出 Evidence.Part 段的 JSON。
/// 抽取范围按 V1 金样契约：孔=减料圆柱/孔特征柱面直径（特征名前缀承载 pin/sensor 语义，见 test/drawings/README.md）、
/// 实体/片体计数、圆角半径。缺段的证据（extrude/图面）留空——规则引擎"缺证据即跳过"。
/// 绝不保存被审文件；关闭仅针对本次调用打开的部件，用户已加载的部件不动。
/// </summary>
internal static partial class ToolService
{
    private const int UfCylindricalFace = (int)NXOpen.UF.UFConstants.UF_MODL_CYLINDRICAL_FACE;

    private static object ExtractEvidence(JsonElement p)
    {
        var session = Session.GetSession();
        var full = Workspace.ResolveExisting(GetString(p, "file_path"));

        var prevWorkPath = TryWorkPartFullPath(session);
        BasePart? already = null;
        foreach (object item in session.Parts)
        {
            if (item is BasePart bp && bp.Tag != Tag.Null &&
                string.Equals(bp.FullPath, full, StringComparison.OrdinalIgnoreCase))
            { already = bp; break; }
        }

        bool weOpened = false;
        PartLoadStatus? loadStatus = null;
        Part target;
        try
        {
            if (already == null)
            {
                try
                {
                    target = session.Parts.Open(full, out loadStatus)
                        ?? throw new InvalidOperationException("部件打开后返回空对象: " + full);
                }
                catch (NXException ex) when (SameNameLoaded(session, full))
                {
                    throw new InvalidOperationException(
                        $"同名部件已在会话中打开（{Path.GetFileNameWithoutExtension(full)}，取自其他路径），" +
                        "审图以磁盘文件为准，故本张记失败；请先关闭会话中该部件再审。原始错误: " + ex.Message);
                }
                weOpened = true;
            }
            else
            {
                target = already as Part
                    ?? throw new InvalidOperationException("已加载部件不是 part（可能是装配草稿），无法抽证据: " + full);
            }
            var evidence = BuildPartEvidence(target);
            string? closeError = null;
            string workPartAfter = string.Empty;
            if (weOpened)
            {
                try
                {
                    target.Close(BasePart.CloseWholeTree.False,
                        BasePart.CloseModified.CloseModified, session.Parts.NewPartCloseResponses());
                }
                catch (Exception ex) { closeError = ex.Message; }
                // 恢复用户此前的工作部件上下文（审图不得偷走 Work part）
                if (prevWorkPath != null && TryWorkPartFullPath(session) != prevWorkPath &&
                    File.Exists(prevWorkPath))
                {
                    try { session.Parts.Open(prevWorkPath, out _); } catch { /* 尽力恢复 */ }
                }
                workPartAfter = TryWorkPartFullPath(session) ?? string.Empty;
            }
            else
            {
                workPartAfter = TryWorkPartFullPath(session) ?? string.Empty;
            }
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["part"] = Path.GetFileNameWithoutExtension(full),
                ["file_path"] = full,
                ["reused_already_loaded"] = !weOpened,
                ["close_error"] = closeError,
                ["work_part_after"] = workPartAfter,
                ["evidence"] = new Dictionary<string, object?> { ["part"] = evidence },
            };
        }
        finally
        {
            try { loadStatus?.Dispose(); } catch { /* ignore */ }
        }
    }

    private static bool SameNameLoaded(Session session, string full)
    {
        var leaf = Path.GetFileNameWithoutExtension(full);
        foreach (object item in session.Parts)
        {
            if (item is BasePart bp && bp.Tag != Tag.Null &&
                string.Equals(Path.GetFileNameWithoutExtension(bp.Name), leaf, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? TryWorkPartFullPath(Session session)
    {
        try
        {
            var w = session.Parts?.Work;
            return w == null || w.Tag == Tag.Null ? null : w.FullPath;
        }
        catch { return null; }
    }

    private static Dictionary<string, object?> BuildPartEvidence(Part part)
    {
        var uf = NXOpen.UF.UFSession.GetUFSession();
        int solidCount = 0;
        var sheets = new List<object?>();
        foreach (Body b in part.Bodies)
        {
            try
            {
                if (b.IsSolidBody) solidCount++;
                else if (b.IsSheetBody)
                    sheets.Add(new Dictionary<string, object?>
                    {
                        ["name"] = b.Name,
                        ["is_process_aux"] = IsProcessAuxName(b.Name),
                    });
            }
            catch { /* 单个体读取失败不影响整体证据 */ }
        }

        var holes = new List<object?>();
        var fillets = new List<object?>();
        foreach (NXOpen.Features.Feature f in part.Features)
        {
            string typeName;
            try
            {
                if (f.Suppressed) continue;
                typeName = f.GetType().Name;
            }
            catch { continue; }

            if (typeName is "Cylinder" or "HolePackage")
            {
                var dia = FirstCylindricalDiameter(uf, f);
                if (dia != null)
                    holes.Add(new Dictionary<string, object?>
                    {
                        ["diameter"] = dia,
                        ["kind"] = HoleKindFromFeatureName(f.Name),
                        ["source"] = "model",
                        ["from_array_or_hole_set"] = f.IsOccurrence,
                        ["feature_name"] = f.Name,
                    });
            }
            else if (typeName == "EdgeBlend")
            {
                foreach (var r in CylindricalRadii(uf, f).Distinct())
                    fillets.Add(new Dictionary<string, object?> { ["radius"] = r });
            }
        }

        return new Dictionary<string, object?>
        {
            ["solid_body_count"] = solidCount,
            ["sheet_bodies"] = sheets,
            ["holes"] = holes,
            ["fillets"] = fillets,
        };
    }

    private static double? FirstCylindricalDiameter(NXOpen.UF.UFSession uf, NXOpen.Features.Feature f)
    {
        foreach (var face in SafeFaces(f))
        {
            var (t, radius) = AskFaceShape(uf, face);
            if (t == UfCylindricalFace && radius > 1e-9) return radius * 2.0;
        }
        return null;
    }

    private static IEnumerable<double> CylindricalRadii(NXOpen.UF.UFSession uf, NXOpen.Features.Feature f)
    {
        foreach (var face in SafeFaces(f))
        {
            var (t, radius) = AskFaceShape(uf, face);
            if (t == UfCylindricalFace && radius > 1e-9) yield return radius;
        }
    }

    private static NXOpen.Face[] SafeFaces(NXOpen.Features.Feature f)
    {
        try { return f.GetFaces(); } catch { return Array.Empty<NXOpen.Face>(); }
    }

    private static (int ufType, double radius) AskFaceShape(NXOpen.UF.UFSession uf, NXOpen.Face face)
    {
        try
        {
            var point = new double[3];
            var dir = new double[3];
            var box = new double[6];
            uf.Modl.AskFaceData(face.Tag, out int ufType, point, dir, box,
                out double radius, out double minorRadius, out int normDir);
            return (ufType, radius);
        }
        catch { return (-1, 0); }
    }

    /// <summary>test/drawings README 命名契约：NXA_PIN_*→销轴孔、NXA_SENSOR_*→传感器安装孔，其余按通光孔系列判定。</summary>
    private static string HoleKindFromFeatureName(string name)
    {
        var upper = (name ?? string.Empty).ToUpperInvariant();
        if (upper.StartsWith("NXA_PIN")) return "pin";
        if (upper.StartsWith("NXA_SENSOR")) return "sensor";
        return "simple_through";
    }

    /// <summary>工艺辅助片体命名（展开/模体/blank）不算 BODY-002 的多余片体。</summary>
    private static bool IsProcessAuxName(string name)
    {
        var upper = (name ?? string.Empty).ToUpperInvariant();
        return upper.Contains("FLATTEN") || upper.Contains("EXPAN") || upper.Contains("BLANK") ||
               upper.Contains("展开") || upper.Contains("模体");
    }
}
