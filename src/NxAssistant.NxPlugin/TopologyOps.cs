using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NXOpen;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 拓扑/stable_id + 特征检查 + 重建。对齐 NX-MCP nx_remote_ops.py 的
/// _topology_records / _op_body_topology / _op_resolve_topology / _op_inspect_feature / _op_rebuild_work_part。
///
/// stable_id 语义：对"身份指纹"（面：uf_type/法向/平面偏移/半径/包围盒；边：类型/两端点/相邻面 stable_id）
/// 做字段名升序的规范化 JSON，SHA1 取前 20 位十六进制。不含 tag/序号，重建模型后仍可命中同一几何面/边；
/// 失配时按 selector 里的几何回退字段（normal/radius/near_point…）重新匹配。
/// </summary>
internal static partial class ToolService
{
    // ---- 分发入口（由 DispatchCore 的 switch 调入，已在主线程） ----

    private static object InspectBodyTopology(JsonElement p)
    {
        var work = Work();
        var (body, bodyIndex, bodyFeatureId) = BodyFromTopologyParams(work, p);
        var (faces, edges) = TopologyRecords(body);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["body_index"] = bodyIndex,
            ["body_feature_id"] = bodyFeatureId,
            ["body_tag"] = (int)body.Tag,
            ["face_count"] = faces.Count,
            ["faces"] = faces,
            ["edge_count"] = edges.Count,
            ["edges"] = edges,
        };
    }

    private static object ResolveTopology(JsonElement p)
    {
        var work = Work();
        var kind = GetString(p, "kind").ToLowerInvariant();
        if (kind != "face" && kind != "edge")
            throw new ArgumentException("kind must be 'face' or 'edge'");
        if (!p.TryGetProperty("selector", out var selector) || selector.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("selector must be an object");
        var (body, bodyIndex, bodyFeatureId) = BodyFromTopologyParams(work, p);
        var (faces, edges) = TopologyRecords(body);
        var records = kind == "face" ? faces : edges;

        var stableId = SelectorString(selector, "stable_id");
        var matches = new List<Dictionary<string, object?>>();
        if (!string.IsNullOrEmpty(stableId))
            matches = records.Where(r => (string?)r["stable_id"] == stableId).ToList();
        if (matches.Count == 0)
        {
            var geometryKeys = new[]
            {
                "type_value", "uf_type", "normal", "plane_offset", "point_on_plane",
                "radius", "edge_count", "direction", "length", "adjacent_face_count", "near_point",
            };
            if (!string.IsNullOrEmpty(stableId) && !geometryKeys.Any(k => selector.TryGetProperty(k, out _)))
                throw new ArgumentException("topology stable_id no longer exists and has no geometry fallback");
            matches = records.Where(r => TopologyRecordMatches(r, selector, kind)).ToList();
        }

        var sortBy = SelectorString(selector, "sort_by") ?? string.Empty;
        bool hasOccurrence = selector.TryGetProperty("occurrence", out _);
        var nearPoint = TrySelectorVec(selector, "near_point");
        if (sortBy.Trim().Length > 0)
        {
            switch (sortBy.Trim().ToLowerInvariant())
            {
                case "largest":
                case "largest_area":
                    if (kind != "face") throw new ArgumentException("sort_by largest is face-only");
                    matches = matches.OrderByDescending(r => (double)r["area_proxy"]!).ToList();
                    break;
                case "longest":
                case "largest_length":
                    if (kind != "edge") throw new ArgumentException("sort_by longest is edge-only");
                    matches = matches.OrderByDescending(r => (double)r["length"]!).ToList();
                    break;
                case "nearest":
                    if (nearPoint == null) throw new ArgumentException("sort_by nearest requires near_point");
                    matches = matches.OrderBy(r => PointDistance(Representative(r, kind), nearPoint)).ToList();
                    break;
                case "min_x": case "min_y": case "min_z":
                case "max_x": case "max_y": case "max_z":
                {
                    int axis = sortBy[sortBy.Length - 1] switch { 'x' => 0, 'y' => 1, _ => 2 };
                    bool desc = sortBy.StartsWith("max");
                    matches = desc
                        ? matches.OrderByDescending(r => Representative(r, kind)[axis]).ToList()
                        : matches.OrderBy(r => Representative(r, kind)[axis]).ToList();
                    break;
                }
                default:
                    throw new ArgumentException("unknown sort_by: " + sortBy);
            }
        }

        bool unique = !p.TryGetProperty("unique", out var u) || u.ValueKind != JsonValueKind.False;
        if (unique && matches.Count != 1 && sortBy.Trim().Length == 0 && !hasOccurrence)
            throw new ArgumentException($"topology selector matched {matches.Count} {kind}s; add sort_by or occurrence");

        int occurrence = SelectorInt(selector, "occurrence", 0);
        if (occurrence < 0) throw new ArgumentException("selector occurrence must not be negative");
        if (matches.Count == 0) throw new ArgumentException($"topology selector matched no {kind}s");
        if (occurrence >= matches.Count) throw new ArgumentException("topology selector occurrence is out of range");

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["kind"] = kind,
            ["body_index"] = bodyIndex,
            ["body_feature_id"] = bodyFeatureId,
            ["match_count"] = matches.Count,
            ["selected"] = matches[occurrence],
            ["matches"] = matches.Take(50).ToList(),
            ["matches_truncated"] = matches.Count > 50,
        };
    }

    private static object InspectFeature(JsonElement p)
    {
        var work = Work();
        var feature = FindFeature(work, FeatureId(p));
        var expressions = new List<object?>();
        foreach (var e in feature.GetExpressions())
            expressions.Add(ExpressionRecord(e));
        var bodies = feature.GetBodies();
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["name"] = feature.Name,
            ["journal_id"] = feature.JournalIdentifier,
            ["tag"] = (int)feature.Tag,
            ["type"] = feature.GetType().Name,
            ["is_out_of_date"] = feature.IsOutOfDate(),
            ["suppressed"] = feature.Suppressed,
            ["expressions"] = expressions,
            ["parent_journal_ids"] = feature.GetParents().Select(x => x.JournalIdentifier).ToList(),
            ["child_journal_ids"] = feature.GetChildren().Select(x => x.JournalIdentifier).ToList(),
            ["body_tags"] = bodies.Select(x => (int)x.Tag).ToList(),
            ["error_messages"] = feature.GetFeatureErrorMessages().ToList(),
            ["warning_messages"] = feature.GetFeatureWarningMessages().ToList(),
            ["informational_messages"] = feature.GetFeatureInformationalMessages().ToList(),
        };
    }

    private static object RebuildWorkPart()
    {
        var work = Work();
        var session = Session.GetSession();
        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA rebuild work part");
        int updateErrors = session.UpdateManager.DoUpdate(mark);
        var diagnostics = new List<object?>();
        foreach (NXOpen.Features.Feature f in work.Features)
        {
            var errors = f.GetFeatureErrorMessages();
            var warnings = f.GetFeatureWarningMessages();
            if (errors.Length > 0 || warnings.Length > 0 || f.IsOutOfDate())
                diagnostics.Add(new Dictionary<string, object?>
                {
                    ["name"] = f.Name,
                    ["journal_id"] = f.JournalIdentifier,
                    ["is_out_of_date"] = f.IsOutOfDate(),
                    ["errors"] = errors.ToList(),
                    ["warnings"] = warnings.ToList(),
                });
        }
        try { session.SetUndoMarkName(mark, "NXA rebuild work part"); } catch { /* 命名失败不影响结果 */ }
        return new Dictionary<string, object?>
        {
            ["ok"] = updateErrors == 0 && !diagnostics.OfType<Dictionary<string, object?>>()
                .Any(d => ((List<string>)d["errors"]!).Count > 0),
            ["part"] = work.Leaf,
            ["update_error_count"] = updateErrors,
            ["diagnostics"] = diagnostics,
        };
    }

    // ---- 特征/body 定位（对齐 _find_feature / _body_from_topology_params） ----

    private static string FeatureId(JsonElement p)
    {
        if (!p.TryGetProperty("feature_id", out var v))
            throw new ArgumentException("feature_id must be a feature name, journal id, or index");
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString()!.Trim(),
            _ => throw new ArgumentException("feature_id must be a feature name, journal id, or index"),
        };
    }

    private static NXOpen.Features.Feature FindFeature(Part work, string identifier)
    {
        var features = new List<NXOpen.Features.Feature>();
        foreach (NXOpen.Features.Feature f in work.Features) features.Add(f);
        var text = (identifier ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new ArgumentException("feature_id must be a feature name, journal id, or index");
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
            && index >= 0 && index < features.Count)
            return features[index];
        foreach (var f in features)
            if (text == f.Name || text == f.JournalIdentifier) return f;
        throw new ArgumentException("feature was not found: " + text);
    }

    private static Body BodyByIndex(Part work, int index)
    {
        var bodies = new List<Body>();
        foreach (Body b in work.Bodies) bodies.Add(b);
        if (index < 0 || index >= bodies.Count)
            throw new ArgumentException($"body index {index} is out of range for {bodies.Count} bodies");
        return bodies[index];
    }

    private static (Body body, int? bodyIndex, string? bodyFeatureId) BodyFromTopologyParams(Part work, JsonElement p)
    {
        var featureId = p.TryGetProperty("body_feature_id", out var bf) ? bf.GetString() : null;
        if (!string.IsNullOrWhiteSpace(featureId))
        {
            var feature = FindFeature(work, featureId!.Trim());
            var bodies = feature.GetBodies();
            if (bodies.Length == 0) throw new ArgumentException("body_feature_id does not own a body");
            int occurrence = GetInt(p, "body_occurrence", 0, 0, int.MaxValue);
            if (occurrence >= bodies.Length) throw new ArgumentException("body_occurrence is out of range");
            return (bodies[occurrence], null, feature.JournalIdentifier);
        }
        int bodyIndex = GetInt(p, "body_index", 0, 0, int.MaxValue);
        return (BodyByIndex(work, bodyIndex), bodyIndex, null);
    }

    private static Dictionary<string, object?> ExpressionRecord(NXOpen.Expression e)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = e.Name,
            ["journal_id"] = e.JournalIdentifier,
            ["tag"] = (int)e.Tag,
            ["right_hand_side"] = e.RightHandSide,
            ["value"] = e.Value,
            ["units"] = e.Units?.TypeName,
        };
    }

    // ---- 拓扑记录（对齐 _topology_records：面/边的身份指纹 + stable_id） ----

    private static (List<Dictionary<string, object?>> faces, List<Dictionary<string, object?>> edges)
        TopologyRecords(Body body)
    {
        var uf = NXOpen.UF.UFSession.GetUFSession();
        var faces = new List<Dictionary<string, object?>>();
        var faceByTag = new Dictionary<int, string>();
        int faceIndex = 0;
        foreach (var face in body.GetFaces())
        {
            // UF 包装的 double[] 参数是 [Out] 数组（非 out 关键字）：预分配后传入，封送时回填。
            var point = new double[3];
            var dir = new double[3];
            var box = new double[6];
            uf.Modl.AskFaceData(face.Tag, out int ufType, point, dir, box,
                out double radius, out double minorRadius, out int normDir);
            var actualNormal = UnitVector(dir.Select(v => v * normDir).ToArray());
            var bounds = box.ToArray();
            var spans = Enumerable.Range(0, 3).Select(a => Math.Abs(bounds[a + 3] - bounds[a])).OrderByDescending(v => v).ToArray();
            double areaProxy = spans[0] * spans[1];
            double planeOffset = actualNormal.Select((v, i) => v * point[i]).Sum();
            var identity = FaceIdentityJson(ufType, actualNormal, planeOffset, radius, minorRadius, bounds);
            var stableId = StableTopologyId("face", identity);
            faceByTag[(int)face.Tag] = stableId;
            faces.Add(new Dictionary<string, object?>
            {
                ["index"] = faceIndex,
                ["tag"] = (int)face.Tag,
                ["journal_id"] = face.JournalIdentifier,
                ["stable_id"] = stableId,
                ["type_value"] = (int)face.SolidFaceType,
                ["uf_type"] = ufType,
                ["point"] = point.Select(v => (double)v).ToList(),
                ["direction"] = dir.Select(v => (double)v).ToList(),
                ["normal"] = actualNormal.ToList(),
                ["plane_offset"] = planeOffset,
                ["bounds"] = bounds.ToList(),
                ["radius"] = radius,
                ["minor_radius"] = minorRadius,
                ["normal_direction"] = normDir,
                ["edge_count"] = face.GetEdges().Length,
                ["area_proxy"] = areaProxy,
                ["stable_ref"] = new Dictionary<string, object?>
                {
                    ["kind"] = "face",
                    ["stable_id"] = stableId,
                    ["uf_type"] = ufType,
                    ["normal"] = actualNormal.Select(Round6).ToList(),
                    ["plane_offset"] = Round6(planeOffset),
                    ["radius"] = Round6(radius),
                    ["near_point"] = point.Select(Round6).ToList(),
                    ["tolerance"] = 0.01,
                },
            });
            faceIndex++;
        }

        var edges = new List<Dictionary<string, object?>>();
        int edgeIndex = 0;
        foreach (var edge in body.GetEdges())
        {
            edge.GetVertices(out NXOpen.Point3d s, out NXOpen.Point3d e);
            var start = new[] { s.X, s.Y, s.Z };
            var end = new[] { e.X, e.Y, e.Z };
            var ordered = new[] { start.Select(Round6).ToArray(), end.Select(Round6).ToArray() }
                .OrderBy(v => v[0]).ThenBy(v => v[1]).ThenBy(v => v[2]).ToList();
            var delta = new[] { end[0] - start[0], end[1] - start[1], end[2] - start[2] };
            double length = Math.Sqrt(delta.Sum(v => v * v));
            var direction = UnitVector(delta);
            var midpoint = new[] { (start[0] + end[0]) * 0.5, (start[1] + end[1]) * 0.5, (start[2] + end[2]) * 0.5 };
            var adjacentFaces = edge.GetFaces();
            var adjacentStable = adjacentFaces
                .Select(f => faceByTag.TryGetValue((int)f.Tag, out var sid) ? sid : string.Empty)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var identity = EdgeIdentityJson((int)edge.SolidEdgeType, ordered, adjacentStable);
            var stableId = StableTopologyId("edge", identity);
            edges.Add(new Dictionary<string, object?>
            {
                ["index"] = edgeIndex,
                ["tag"] = (int)edge.Tag,
                ["journal_id"] = edge.JournalIdentifier,
                ["stable_id"] = stableId,
                ["type_value"] = (int)edge.SolidEdgeType,
                ["start"] = start.ToList(),
                ["end"] = end.ToList(),
                ["midpoint"] = midpoint.ToList(),
                ["direction"] = direction.ToList(),
                ["length"] = length,
                ["adjacent_face_count"] = adjacentFaces.Length,
                ["adjacent_face_stable_ids"] = adjacentStable,
                ["stable_ref"] = new Dictionary<string, object?>
                {
                    ["kind"] = "edge",
                    ["stable_id"] = stableId,
                    ["type_value"] = (int)edge.SolidEdgeType,
                    ["midpoint"] = midpoint.Select(Round6).ToList(),
                    ["direction"] = direction.Select(Round6).ToList(),
                    ["length"] = Round6(length),
                    ["tolerance"] = 0.01,
                },
            });
            edgeIndex++;
        }
        return (faces, edges);
    }

    // ---- stable_id 规范化指纹：字段名升序，double 统一 Round6 + InvariantCulture ----

    private static string FaceIdentityJson(int ufType, double[] normal, double planeOffset,
        double radius, double minorRadius, double[] bounds)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"bounds\":[").Append(JoinNums(bounds))
          .Append("],\"minor_radius\":").Append(Fmt(minorRadius))
          .Append(",\"normal\":[").Append(JoinNums(normal))
          .Append("],\"plane_offset\":").Append(Fmt(planeOffset))
          .Append(",\"radius\":").Append(Fmt(radius))
          .Append(",\"uf_type\":").Append(ufType.ToString(CultureInfo.InvariantCulture))
          .Append('}');
        return sb.ToString();
    }

    private static string EdgeIdentityJson(int typeValue, List<double[]> vertices, List<string> adjacentFaces)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"adjacent_faces\":[");
        sb.Append(string.Join(",", adjacentFaces.Select(s => "\"" + s + "\"")));
        sb.Append("],\"type_value\":").Append(typeValue.ToString(CultureInfo.InvariantCulture))
          .Append(",\"vertices\":[");
        sb.Append(string.Join(",", vertices.Select(v => "[" + JoinNums(v) + "]")));
        sb.Append("]}");
        return sb.ToString();
    }

    private static string JoinNums(IEnumerable<double> values) =>
        string.Join(",", values.Select(Fmt));

    private static string Fmt(double v) =>
        Round6(v).ToString("0.#####", CultureInfo.InvariantCulture);

    private static double Round6(double v) => Math.Round(v, 6, MidpointRounding.ToEven);

    private static string StableTopologyId(string kind, string canonicalJson)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(canonicalJson));
        var sb = new StringBuilder(kind.Length + 21);
        sb.Append(kind).Append(':');
        for (int i = 0; i < 10; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ---- selector 匹配（对齐 _topology_record_matches / _resolve_topology_object） ----

    private static bool TopologyRecordMatches(Dictionary<string, object?> record, JsonElement sel, string kind)
    {
        double tolerance = SelectorNum(sel, "tolerance", 0.01);
        double angularTolerance = SelectorNum(sel, "angular_tolerance_deg", 1.0);
        if (sel.TryGetProperty("type_value", out _) && RecNum(record, "type_value") != SelectorNum(sel, "type_value", double.NaN))
            return false;
        if (kind == "face")
        {
            if (sel.TryGetProperty("uf_type", out _) && RecNum(record, "uf_type") != SelectorNum(sel, "uf_type", double.NaN))
                return false;
            var normal = SelectorVecOrNull(sel, "normal");
            if (normal != null && !DirectionMatches(RecVec(record, "normal"), normal, angularTolerance, true))
                return false;
            if (sel.TryGetProperty("plane_offset", out _)
                && Math.Abs(RecNum(record, "plane_offset") - SelectorNum(sel, "plane_offset", 0)) > tolerance)
                return false;
            var onPlane = TrySelectorVec(sel, "point_on_plane");
            if (onPlane != null)
            {
                double offset = 0;
                var n = RecVec(record, "normal");
                for (int i = 0; i < 3; i++) offset += n[i] * onPlane[i];
                if (Math.Abs(offset - RecNum(record, "plane_offset")) > tolerance) return false;
            }
            if (sel.TryGetProperty("radius", out _)
                && Math.Abs(RecNum(record, "radius") - SelectorNum(sel, "radius", 0)) > tolerance)
                return false;
            if (sel.TryGetProperty("edge_count", out _) && (int)RecNum(record, "edge_count") != SelectorInt(sel, "edge_count", -1))
                return false;
        }
        else
        {
            var dir = SelectorVecOrNull(sel, "direction");
            if (dir != null && !DirectionMatches(RecVec(record, "direction"), dir, angularTolerance,
                    sel.TryGetProperty("oriented", out var o) && o.ValueKind == JsonValueKind.True))
                return false;
            if (sel.TryGetProperty("length", out _)
                && Math.Abs(RecNum(record, "length") - SelectorNum(sel, "length", 0)) > tolerance)
                return false;
            if (sel.TryGetProperty("adjacent_face_count", out _)
                && (int)RecNum(record, "adjacent_face_count") != SelectorInt(sel, "adjacent_face_count", -1))
                return false;
        }
        var near = TrySelectorVec(sel, "near_point");
        if (near != null)
        {
            double maxDistance = SelectorNum(sel, "max_distance", tolerance);
            if (PointDistance(Representative(record, kind), near) > maxDistance) return false;
        }
        return true;
    }

    private static double[] Representative(Dictionary<string, object?> record, string kind) =>
        RecVec(record, kind == "face" ? "point" : "midpoint");

    private static double[] RecVec(Dictionary<string, object?> record, string key) =>
        ((List<double>)record[key]!).ToArray();

    private static double RecNum(Dictionary<string, object?> record, string key) => Convert.ToDouble(record[key], CultureInfo.InvariantCulture);

    private static double PointDistance(double[] a, double[] b) =>
        Math.Sqrt(Enumerable.Range(0, 3).Sum(i => (a[i] - b[i]) * (a[i] - b[i])));

    private static double[] UnitVector(double[] v)
    {
        double length = Math.Sqrt(v.Sum(x => x * x));
        return length <= 1e-12 ? new double[3] : v.Select(x => x / length).ToArray();
    }

    private static bool DirectionMatches(double[] actual, double[] expected, double angularToleranceDeg, bool oriented)
    {
        var a = UnitVector(actual);
        var b = UnitVector(expected);
        double dot = a.Select((x, i) => x * b[i]).Sum();
        if (!oriented) dot = Math.Abs(dot);
        return dot >= Math.Cos(angularToleranceDeg * Math.PI / 180.0);
    }

    // ---- selector JsonElement 取值 ----

    private static string? SelectorString(JsonElement sel, string name) =>
        sel.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double SelectorNum(JsonElement sel, string name, double dflt)
    {
        if (!sel.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return dflt;
        return v.GetDouble();
    }

    private static int SelectorInt(JsonElement sel, string name, int dflt)
    {
        if (!sel.TryGetProperty(name, out var v)) return dflt;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32()
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s
            : throw new ArgumentException($"{name} must be an integer");
    }

    private static double[]? SelectorVecOrNull(JsonElement sel, string name) => TrySelectorVec(sel, name);

    private static double[]? TrySelectorVec(JsonElement sel, string name)
    {
        if (!sel.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var values = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (values.Length != 3) throw new ArgumentException($"{name} must be a 3-number vector");
        return values;
    }
}
