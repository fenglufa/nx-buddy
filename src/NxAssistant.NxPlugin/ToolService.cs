using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using NXOpen;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 插件内方法分发。handler 对应 NX-MCP 的 _OPS：一个方法名 → 一段 NXOpen 逻辑，返回可 JSON 化对象。
/// 所有 handler 一律经 MainThread.Run 在 NX 主线程执行（NXOpen 线程亲和）。
/// </summary>
internal static partial class ToolService
{
    public static object Dispatch(string method, JsonElement @params) =>
        MainThread.Run(() => DispatchCore(method, @params));

    private static object DispatchCore(string method, JsonElement p)
    {
        switch (method)
        {
            case MethodNames.Ping: return Ping();
            case MethodNames.GetPartSummary: return GetPartSummary(p);
            case MethodNames.InspectWorkPartGeometry: return InspectWorkPartGeometry(p);
            case MethodNames.SaveWorkPart: return SaveWorkPart();
            case MethodNames.CreatePart: return CreatePart(p);
            case MethodNames.InspectBodyTopology: return InspectBodyTopology(p);
            case MethodNames.ResolveTopology: return ResolveTopology(p);
            case MethodNames.InspectFeature: return InspectFeature(p);
            case MethodNames.RebuildWorkPart: return RebuildWorkPart();
            case MethodNames.CreateBlock: return CreateBlock(p);
            case MethodNames.CreateParametricSketch: return CreateParametricSketch(p);
            case MethodNames.InspectSketch: return InspectSketch(p);
            case MethodNames.ExtrudeSketch: return ExtrudeSketch(p);
            case MethodNames.SetFeatureExpression: return SetFeatureExpression(p);
            default:
                throw new NotSupportedException($"unknown method: '{method}' (not implemented yet)");
        }
    }

    private static Part Work()
    {
        var work = Session.GetSession().Parts?.Work;
        if (work == null || work.Tag == Tag.Null)
            throw new InvalidOperationException("NX 中没有打开的工作部件（先 create_part 或在 NX 里打开文件）");
        return work;
    }

    /// <summary>对齐 _op_ping：证明"宿主→IPC→插件→NX 会话"这条链是否通。</summary>
    private static object Ping()
    {
        var session = Session.GetSession();
        string workPart = string.Empty;
        try
        {
            var work = session.Parts?.Work;
            if (work != null && work.Tag != Tag.Null) workPart = work.Leaf;
        }
        catch { /* 无工作部件时留空 */ }

        var proc = Process.GetCurrentProcess();
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["plugin_version"] = typeof(ToolService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["application"] = session.ApplicationName,
            ["pid"] = proc.Id,
            ["work_part"] = workPart,
            ["transport"] = "named-pipe",
            ["main_thread"] = MainThread.OnMainThread,
            ["log"] = NxLog.LogPath,
        };
    }

    /// <summary>对齐 _op_part_summary：部件名/路径/体数/特征清单（max_features 截断）。</summary>
    private static object GetPartSummary(JsonElement p)
    {
        var work = Work();
        int maxFeatures = GetInt(p, "max_features", 100, 1, 1000);
        int bodyCount = 0;
        foreach (Body unused in work.Bodies) bodyCount++;
        var features = new List<object>();
        int total = 0;
        foreach (NXOpen.Features.Feature f in work.Features)
        {
            total++;
            if (features.Count < maxFeatures)
                features.Add(new Dictionary<string, object?>
                {
                    ["name"] = f.Name,
                    ["journal_id"] = f.JournalIdentifier,
                });
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["name"] = work.Leaf,
            ["full_path"] = work.FullPath,
            ["body_count"] = bodyCount,
            ["feature_count"] = total,
            ["features"] = features,
            ["features_truncated"] = total > features.Count,
        };
    }

    /// <summary>对齐 _op_body_geometry：每体面/边/顶点数 + UF 包围盒（顶点回退）。</summary>
    private static object InspectWorkPartGeometry(JsonElement p)
    {
        var work = Work();
        int maxBodies = GetInt(p, "max_bodies", 50, 1, 500);
        var uf = NXOpen.UF.UFSession.GetUFSession();
        var all = new List<Body>();
        foreach (Body b in work.Bodies) all.Add(b);
        var results = new List<object>();
        foreach (var body in all.GetRange(0, Math.Min(maxBodies, all.Count)))
        {
            var edges = body.GetEdges();
            var faces = body.GetFaces();
            var vertices = new List<double[]>();
            foreach (var edge in edges)
            {
                try
                {
                    edge.GetVertices(out NXOpen.Point3d v1, out NXOpen.Point3d v2);
                    vertices.Add(new[] { v1.X, v1.Y, v1.Z });
                    vertices.Add(new[] { v2.X, v2.Y, v2.Z });
                }
                catch { /* 退化边（如整圆）拿不到端点，跳过 */ }
            }

            double[]? min = null, max = null;
            string? method = null;
            try
            {
                var box = AskBoundingBox(uf, body);
                if (box.Length == 6)
                {
                    min = new[] { box[0], box[1], box[2] };
                    max = new[] { box[3], box[4], box[5] };
                    method = "UF_MODL_ask_bounding_box";
                }
            }
            catch { /* 回退到顶点点集 */ }
            if (min == null && vertices.Count > 0)
            {
                min = new[]{ vertices.Min(v => v[0]), vertices.Min(v => v[1]), vertices.Min(v => v[2]) };
                max = new[]{ vertices.Max(v => v[0]), vertices.Max(v => v[1]), vertices.Max(v => v[2]) };
                method = "edge_vertices_fallback";
            }
            object? bounds = min != null
                ? new Dictionary<string, object>
                {
                    ["min"] = min, ["max"] = max!,
                    ["size"] = new[] { max![0] - min[0], max[1] - min[1], max[2] - min[2] },
                }
                : null;

            results.Add(new Dictionary<string, object?>
            {
                ["name"] = body.Name,
                ["face_count"] = faces.Length,
                ["edge_count"] = edges.Length,
                ["vertex_samples"] = vertices.Count,
                ["bounds"] = bounds,
                ["bounds_method"] = method,
            });
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["body_count"] = all.Count,
            ["bodies"] = results,
            ["bodies_truncated"] = all.Count > results.Count,
        };
    }

    private static double[] AskBoundingBox(NXOpen.UF.UFSession uf, Body body)
    {
        // NX2412 C# 包装：UFModl.AskBoundingBox(Tag, double[6])，数组按引用填充。
        var box = new double[6];
        uf.Modl.AskBoundingBox(body.Tag, box);
        return box;
    }

    /// <summary>对齐 _op_save_work_part：保存 + SaveStatus.Dispose + 文件回读。</summary>
    private static object SaveWorkPart()
    {
        var work = Work();
        int unsaved = -1;
        var status = work.Save(BasePart.SaveComponents.True, BasePart.CloseAfterSave.False);
        try { unsaved = status.NumberUnsavedParts; }
        finally { status.Dispose(); }
        var path = work.FullPath;
        bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["name"] = work.Leaf,
            ["full_path"] = path,
            ["number_unsaved_parts"] = unsaved,
            ["file_exists"] = exists,
            ["file_size"] = exists ? new FileInfo(path!).Length : (long?)null,
        };
    }

    /// <summary>对齐 _op_create_part：工作区沙箱内新建并显示部件（FILE-001）。</summary>
    private static object CreatePart(JsonElement p)
    {
        var fileName = GetString(p, "file_name");
        var unitsName = (p.TryGetProperty("units", out var u) ? u.GetString() : "millimeters")?.Trim().ToLowerInvariant() ?? "millimeters";
        Part.Units units = unitsName switch
        {
            "millimeter" or "millimeters" or "mm" => Part.Units.Millimeters,
            "inch" or "inches" or "in" => Part.Units.Inches,
            _ => throw new ArgumentException("units 必须是 millimeters 或 inches"),
        };
        var full = Workspace.Resolve(fileName, existsFail: true);
        var part = Session.GetSession().Parts.NewDisplay(full, units);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["name"] = part.Leaf,
            ["full_path"] = part.FullPath,
            ["units"] = unitsName,
            ["workspace"] = Workspace.Root,
        };
    }

    // ---- 参数工具：宁严宽入参错误，报错文本对齐 NX-MCP 约定 ----

    private static int GetInt(JsonElement p, string name, int dflt, int lo, int hi)
    {
        if (!p.TryGetProperty(name, out var v)) return dflt;
        var n = v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s
            : throw new ArgumentException($"{name} must be an integer");
        if (n < lo || n > hi) throw new ArgumentException($"{name} must be between {lo} and {hi}");
        return n;
    }

    private static string GetString(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(v.GetString()))
            throw new ArgumentException($"{name} must be a non-empty string");
        return v.GetString()!.Trim();
    }
}

