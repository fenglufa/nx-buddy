using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NXOpen;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 写操作切片：create_block / create_parametric_sketch / extrude_sketch /
/// set_feature_expression / inspect_sketch。对齐 nx_remote_ops.py 同名 _op_*：
/// 失败一律 UndoToMark 回滚 + 抛错，由宿主 Guard 转成 {ok:false,...} 载荷。
/// </summary>
internal static partial class ToolService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- create_block（§9.1 写：OriginAndEdgeLengths 布尔 Create） ----

    private static object CreateBlock(JsonElement p)
    {
        double length = PositiveNum(p, "length", 100.0);
        double width = PositiveNum(p, "width", 60.0);
        double height = PositiveNum(p, "height", 40.0);
        var origin = Vec3(p, "origin", new double[] { 0, 0, 0 });

        var work = Work();
        var session = Session.GetSession();
        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA create block");
        NXOpen.Features.BlockFeatureBuilder builder = null;
        try
        {
            builder = work.Features.CreateBlockFeatureBuilder(null!);
            builder.Type = NXOpen.Features.BlockFeatureBuilder.Types.OriginAndEdgeLengths;
            builder.SetOriginAndLengths(
                new NXOpen.Point3d(origin[0], origin[1], origin[2]),
                FmtNum(length), FmtNum(width), FmtNum(height));
            builder.SetBooleanOperationAndTarget(
                NXOpen.Features.Feature.BooleanType.Create, null!);
            var feature = builder.CommitFeature();
            session.SetUndoMarkName(mark, "NXA create block");
            AssistantMarks.Record(work, mark, "NXA create block");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["feature"] = feature.JournalIdentifier,
                ["name"] = feature.Name,
                ["part"] = work.Leaf,
                ["length"] = length,
                ["width"] = width,
                ["height"] = height,
                ["origin"] = origin.ToList(),
                ["body_count"] = CountBodies(work),
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    // ---- create_cylindrical_hole（§9.1 写：AxisDiameterAndHeight 减料圆柱） ----
    // 直径∈系列的写前守卫（HOLE-DIA-001）在宿主侧 commit 前拦截，这里保持与旧桥同语义。

    private static object CreateCylindricalHole(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var target = BodyByIndex(work, (int)FiniteNum(p, "target_body_index", 0));
        var origin = Vec3(p, "origin", new double[] { 0, 0, 0 });
        var direction = Vec3(p, "direction", new double[] { 0, 0, -1 });
        var magnitude = Math.Sqrt(direction.Sum(v => v * v));
        if (magnitude <= 1e-12) throw new ArgumentException("direction must not be the zero vector");
        direction = direction.Select(v => v / magnitude).ToArray();
        double diameter = PositiveNum(p, "diameter", null);
        double depth = PositiveNum(p, "depth", null);
        var featureName = SafeObjectName(OptString(p, "feature_name") ?? string.Empty, "MCP_HOLE");

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA cylindrical hole");
        NXOpen.Features.CylinderBuilder builder = null;
        try
        {
            builder = work.Features.CreateCylinderBuilder(null!);
            builder.Type = NXOpen.Features.CylinderBuilder.Types.AxisDiameterAndHeight;
            builder.Origin = new NXOpen.Point3d(origin[0], origin[1], origin[2]);
            builder.Direction = new NXOpen.Vector3d(direction[0], direction[1], direction[2]);
            builder.Diameter.RightHandSide = FmtNum(diameter);
            builder.Height.RightHandSide = FmtNum(depth);
            builder.BooleanOption.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Subtract;
            builder.BooleanOption.SetTargetBodies(new NXOpen.Body[] { target });
            var feature = builder.CommitFeature();
            feature.SetName(featureName);
            session.SetUndoMarkName(mark, "NXA cylindrical hole");
            AssistantMarks.Record(work, mark, "NXA cylindrical hole");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["name"] = feature.Name,
                ["journal_id"] = feature.JournalIdentifier,
                ["implementation"] = "subtractive_cylinder_feature",
                ["target_body_tag"] = (long)target.Tag,
                ["origin"] = origin.ToList(),
                ["direction"] = direction.ToList(),
                ["diameter"] = diameter,
                ["depth"] = depth,
                ["part_body_count"] = CountBodies(work),
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    // ---- fillet_edges / chamfer_edges（EdgeBlend/Chamfer builder；edge_indices 与
    //      inspect_body_topology 的 edge index 同源，均按 body.GetEdges() 顺序） ----

    private static object FilletEdges(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var body = BodyByIndex(work, (int)FiniteNum(p, "body_index", 0));
        var edges = EdgesByIndices(body, p);
        double radius = PositiveNum(p, "radius", null);
        var featureName = SafeObjectName(OptString(p, "feature_name") ?? string.Empty, "MCP_FILLET");
        var tags = edges.Select(e => (long)e.Tag).ToList();

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA fillet edges");
        NXOpen.Features.EdgeBlendBuilder builder = null;
        try
        {
            builder = work.Features.CreateEdgeBlendBuilder(null!);
            var collector = work.ScCollectors.CreateCollector();
            var rule = work.ScRuleFactory.CreateRuleEdgeDumb(edges);
            collector.ReplaceRules(new NXOpen.SelectionIntentRule[] { rule }, false);
            builder.AddChainset(collector, FmtNum(radius));
            var feature = builder.CommitFeature();
            feature.SetName(featureName);
            session.SetUndoMarkName(mark, "NXA fillet edges");
            AssistantMarks.Record(work, mark, "NXA fillet edges");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["name"] = feature.Name,
                ["journal_id"] = feature.JournalIdentifier,
                ["radius"] = radius,
                ["selected_edge_tags"] = tags,
                ["part_body_count"] = CountBodies(work),
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    private static object ChamferEdges(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var body = BodyByIndex(work, (int)FiniteNum(p, "body_index", 0));
        var edges = EdgesByIndices(body, p);
        double distance = PositiveNum(p, "distance", null);
        var featureName = SafeObjectName(OptString(p, "feature_name") ?? string.Empty, "MCP_CHAMFER");
        var tags = edges.Select(e => (long)e.Tag).ToList();

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA chamfer edges");
        NXOpen.Features.ChamferBuilder builder = null;
        try
        {
            builder = work.Features.CreateChamferBuilder(null!);
            var collector = work.ScCollectors.CreateCollector();
            var rule = work.ScRuleFactory.CreateRuleEdgeDumb(edges);
            collector.ReplaceRules(new NXOpen.SelectionIntentRule[] { rule }, false);
            builder.SmartCollector = collector;
            builder.Method = NXOpen.Features.ChamferBuilder.OffsetMethod.EdgesAlongFaces;
            builder.Option = NXOpen.Features.ChamferBuilder.ChamferOption.SymmetricOffsets;
            builder.FirstOffsetExp.RightHandSide = FmtNum(distance);
            builder.SecondOffsetExp.RightHandSide = FmtNum(distance);
            var feature = builder.CommitFeature();
            feature.SetName(featureName);
            session.SetUndoMarkName(mark, "NXA chamfer edges");
            AssistantMarks.Record(work, mark, "NXA chamfer edges");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["name"] = feature.Name,
                ["journal_id"] = feature.JournalIdentifier,
                ["distance"] = distance,
                ["selected_edge_tags"] = tags,
                ["part_body_count"] = CountBodies(work),
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    // ---- move_object（§5 #20 回读护栏教科书样板：提交后按包围盒位移回读，
    //      与请求 translation 不一致就 UndoToMark 回滚——Move Body 对特征驱动实体
    //      （extrude 等）可能"提交成功但没动"，静默假成功比报错更危险） ----

    private static object MoveObject(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var body = BodyByIndex(work, (int)FiniteNum(p, "body_index", 0));
        var translation = Vec3(p, "translation", null!)
            ?? throw new ArgumentException("translation is required");
        if (Math.Sqrt(translation.Sum(v => v * v)) <= 1e-9)
            throw new ArgumentException("translation must not be the zero vector");
        var featureName = SafeObjectName(OptString(p, "feature_name") ?? string.Empty, "MCP_MOVE_OBJECT");
        var boundsBefore = BodyBounds(body);

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA move object");
        NXOpen.Features.MoveBodyBuilder builder = null;
        try
        {
            builder = work.Features.CreateMoveBodyBuilder(null!);
            var rule = work.ScRuleFactory.CreateRuleBodyDumb(new NXOpen.Body[] { body });
            builder.BodyToMove.ReplaceRules(new NXOpen.SelectionIntentRule[] { rule }, false);
            builder.Motion.Option = NXOpen.GeometricUtilities.ModlMotion.Options.DeltaXyz;
            builder.Motion.DeltaXc.RightHandSide = FmtNum(translation[0]);
            builder.Motion.DeltaYc.RightHandSide = FmtNum(translation[1]);
            builder.Motion.DeltaZc.RightHandSide = FmtNum(translation[2]);
            if (!builder.Validate())
                throw new InvalidOperationException("NX rejected the move parameters");
            var feature = builder.CommitFeature();
            feature.SetName(featureName);
            session.UpdateManager.DoUpdate(
                session.SetUndoMark(Session.MarkVisibility.Visible, "NXA move object regen"));
            session.SetUndoMarkName(mark, "NXA move object");
            var name = feature.Name;
            var journalId = feature.JournalIdentifier;
            var boundsAfter = BodyBounds(body);
            if (boundsBefore == null || boundsAfter == null)
            {
                try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
                throw new InvalidOperationException("move object aborted: NX returned no bounding box");
            }
            var displacement = Enumerable.Range(0, 3)
                .Select(axis => boundsAfter[axis] - boundsBefore[axis]).ToArray();
            if (Enumerable.Range(0, 3).Any(axis => Math.Abs(displacement[axis] - translation[axis]) > 1e-3))
            {
                try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
                throw new InvalidOperationException(
                    $"move object rolled back: NX committed the feature but the body did not move " +
                    $"(displacement was [{FmtNum(displacement[0])}, {FmtNum(displacement[1])}, {FmtNum(displacement[2])}], " +
                    $"requested [{FmtNum(translation[0])}, {FmtNum(translation[1])}, {FmtNum(translation[2])}]). " +
                    "Move Body cannot reposition feature-driven bodies such as extrusions; " +
                    "edit the owning feature parameters instead.");
            }
            AssistantMarks.Record(work, mark, "NXA move object");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["name"] = name,
                ["journal_id"] = journalId,
                ["body_index"] = (int)FiniteNum(p, "body_index", 0),
                ["body_journal_id"] = body.JournalIdentifier,
                ["translation"] = translation.ToList(),
                ["bounds_before"] = BoundsDict(boundsBefore),
                ["bounds_after"] = BoundsDict(boundsAfter),
                ["part_body_count"] = CountBodies(work),
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    /// <summary>镜像 _body_bounding_box：UF 包围盒 [min3,max3]，取不到返回 null（触发回滚路径）。</summary>
    private static double[]? BodyBounds(Body body)
    {
        try { return AskBoundingBox(NXOpen.UF.UFSession.GetUFSession(), body); }
        catch { return null; }
    }

    private static Dictionary<string, object?> BoundsDict(double[] box) => new()
    {
        ["min"] = box.Take(3).ToList(),
        ["max"] = box.Skip(3).Take(3).ToList(),
    };

    // ---- export_exchange（§5 #18：DexManager STEP 导出，V1 仅 step——PRD §9.1 限定；
    //      提交后回读文件存在+size>0，同属回读护栏家族；导出件必须先在盘上存在） ----

    private static object ExportExchange(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var format = (OptString(p, "format") ?? "step").ToLowerInvariant();
        if (format != "step")
            throw new ArgumentException("V1 仅支持 format=step（Parasolid 导出待后续批次）");
        var protocol = (OptString(p, "application_protocol") ?? "ap242").ToLowerInvariant();
        var exportAs = protocol switch
        {
            "ap203" => NXOpen.StepCreator.ExportAsOption.Ap203,
            "ap214" => NXOpen.StepCreator.ExportAsOption.Ap214,
            "ap242" => NXOpen.StepCreator.ExportAsOption.Ap242,
            "ap242ed2" => NXOpen.StepCreator.ExportAsOption.Ap242ED2,
            _ => throw new ArgumentException(
                "application_protocol must be ap203, ap214, ap242, or ap242ed2"),
        };
        bool includeCurves = true;
        if (p.TryGetProperty("include_curves", out var ic))
        {
            if (ic.ValueKind != JsonValueKind.True && ic.ValueKind != JsonValueKind.False)
                throw new ArgumentException("include_curves must be a boolean");
            includeCurves = ic.GetBoolean();
        }
        var output = Workspace.ResolveExchange(
            GetString(p, "file_name"), new[] { ".stp", ".step" },
            p.TryGetProperty("overwrite", out var ow) && ow.ValueKind == JsonValueKind.True);
        if (string.IsNullOrEmpty(work.FullPath) || !File.Exists(work.FullPath))
            throw new InvalidOperationException("save the active NX part before STEP export");

        NXOpen.StepCreator builder = null;
        string? settingsFile = null;
        try
        {
            builder = session.DexManager.CreateStepCreator();
            builder.ExportAs = exportAs;
            builder.ExportFrom = NXOpen.StepCreator.ExportFromOption.ExistingPart;
            builder.InputFile = work.FullPath;
            builder.ExportSolidsAndSurfacesAs =
                NXOpen.StepCreator.ExportSolidsAndSurfacesAsOption.Precise;
            builder.FileSaveFlag = false;
            builder.LayerMask = "1-256";
            builder.ColorAndLayers = true;
            settingsFile = StepSettingsFile(protocol);
            if (settingsFile != null) builder.SettingsFile = settingsFile;
            builder.ObjectTypes.Solids = true;
            builder.ObjectTypes.Surfaces = true;
            builder.ObjectTypes.Curves = includeCurves;
            builder.ExportDestination = NXOpen.BaseCreator.ExportDestinationOption.NativeFileSystem;
            builder.OutputFile = output;
            builder.ProcessHoldFlag = true;
            builder.Commit();
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
        if (!File.Exists(output))
            throw new InvalidOperationException("NX export completed without creating: " + output);
        long size = new FileInfo(output).Length;
        if (size <= 0)
            throw new InvalidOperationException("NX export created an empty file: " + output);
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["format"] = format,
            ["application_protocol"] = protocol,
            ["part"] = work.Leaf,
            ["output_file"] = output,
            ["file_size"] = size,
            ["settings_file"] = settingsFile,
            ["include_curves"] = includeCurves,
            ["workspace"] = Workspace.Root,
        };
    }

    /// <summary>镜像 _step_settings_file：UGII_BASE_DIR 下的官方 translator 定义文件，缺省 null。</summary>
    private static string? StepSettingsFile(string protocol)
    {
        var baseDir = Environment.GetEnvironmentVariable("UGII_BASE_DIR");
        if (string.IsNullOrWhiteSpace(baseDir)) return null;
        var rel = protocol switch
        {
            "ap203" => Path.Combine("STEP203UG", "step203ug.def"),
            "ap214" => Path.Combine("STEP214UG", "step214ug.def"),
            _ => Path.Combine("TRANSLATORS", "step242", "step242ug.def"),
        };
        var path = Path.Combine(baseDir, rel);
        return File.Exists(path) ? path : null;
    }

    // ---- import_exchange（§5 追加行：客户 2026-09-22 确认进 V1；STEP→当前工作部件。
    //      回读护栏是"实体数必须真的变多 + update_error_count"双条件，0 增量即 ok:false；
    //      非特征实体的入口——move_object 正路径依赖它） ----

    private static object ImportExchange(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var fileName = GetString(p, "file_name");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var format = (OptString(p, "format") ?? "auto").ToLowerInvariant();
        if (format == "auto") format = ext is ".x_t" or ".x_b" ? "parasolid" : "step";
        if (format != "step")
            throw new ArgumentException("V1 仅支持 STEP 导入（.stp/.step；Parasolid 待后续批次）");
        var protocol = (OptString(p, "application_protocol") ?? "ap242").ToLowerInvariant();
        if (protocol is not ("ap203" or "ap214" or "ap242"))
            throw new ArgumentException("application_protocol must be ap203, ap214, or ap242");
        var input = Workspace.ResolveExchange(fileName, new[] { ".stp", ".step" }, overwrite: true);
        if (!File.Exists(input))
            throw new IOException("exchange file does not exist: " + input);
        bool OptBool(string name, bool dflt) =>
            !p.TryGetProperty(name, out var v) ? dflt
            : v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False ? v.GetBoolean()
            : throw new ArgumentException(name + " must be a boolean");
        bool sew = OptBool("sew_surfaces", true), simplify = OptBool("simplify_geometry", true);
        bool includeCurves = OptBool("include_curves", true);
        var settingsFile = StepSettingsFile(protocol);

        int bodiesBefore = CountBodies(work), featuresBefore = CountFeatures(work);
        void Configure(NXOpen.BaseImporter b, NXOpen.ObjectTypeSelector ot)
        {
            b.SetMode(NXOpen.BaseImporter.Mode.NativeFileSystem);
            b.InputFile = input;
            b.OutputFile = "";
            b.ProcessHoldFlag = true;
            ot.Solids = true;
            ot.Surfaces = true;
            ot.Curves = includeCurves;
        }
        switch (protocol)
        {
            case "ap203": {
                var b = session.DexManager.CreateStep203Importer();
                try
                {
                    b.ImportTo = NXOpen.Step203Importer.ImportToOption.WorkPart;
                    b.SewSurfaces = sew;
                    b.SimplifyGeometry = simplify;
                    if (settingsFile != null) b.SettingsFile = settingsFile;
                    Configure(b, b.ObjectTypes);
                    b.Commit();
                }
                finally { try { b.Destroy(); } catch { /* ignore */ } }
                break;
            }
            case "ap214": {
                var b = session.DexManager.CreateStep214Importer();
                try
                {
                    b.ImportTo = NXOpen.Step214Importer.ImportToOption.WorkPart;
                    b.SewSurfaces = sew;
                    b.SimplifyGeometry = simplify;
                    if (settingsFile != null) b.SettingsFile = settingsFile;
                    Configure(b, b.ObjectTypes);
                    b.Commit();
                }
                finally { try { b.Destroy(); } catch { /* ignore */ } }
                break;
            }
            default: {
                var b = session.DexManager.CreateStep242Importer();
                try
                {
                    b.ImportTo = NXOpen.Step242Importer.ImportToOption.WorkPart;
                    b.SewSurfaces = sew;
                    b.SimplifyGeometry = simplify;
                    if (settingsFile != null) b.SettingsFile = settingsFile;
                    Configure(b, b.ObjectTypes);
                    b.Commit();
                }
                finally { try { b.Destroy(); } catch { /* ignore */ } }
                break;
            }
        }
        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA import exchange");
        int updateErrors = session.UpdateManager.DoUpdate(mark);
        int bodiesAfter = CountBodies(work), featuresAfter = CountFeatures(work);
        bool importOk = updateErrors == 0 && bodiesAfter > bodiesBefore;
        if (importOk) AssistantMarks.Record(work, mark, "NXA import exchange");
        return new Dictionary<string, object?>
        {
            ["ok"] = importOk,
            ["format"] = format,
            ["application_protocol"] = protocol,
            ["part"] = work.Leaf,
            ["input_file"] = input,
            ["input_file_size"] = new FileInfo(input).Length,
            ["settings_file"] = settingsFile,
            ["body_count_before"] = bodiesBefore,
            ["body_count_after"] = bodiesAfter,
            ["body_count_added"] = bodiesAfter - bodiesBefore,
            ["feature_count_before"] = featuresBefore,
            ["feature_count_after"] = featuresAfter,
            ["feature_count_added"] = featuresAfter - featuresBefore,
            ["update_error_count"] = updateErrors,
        };
    }

    /// <summary>镜像 _items_by_indices：非空 int 数组、越界与重复都报错，按 body.GetEdges() 顺序取边。</summary>
    private static NXOpen.Edge[] EdgesByIndices(Body body, JsonElement p)
    {
        var all = body.GetEdges();
        if (!p.TryGetProperty("edge_indices", out var v) || v.ValueKind != JsonValueKind.Array ||
            v.GetArrayLength() == 0)
            throw new ArgumentException("edge_indices must be a non-empty list");
        var picked = new List<NXOpen.Edge>();
        var seen = new HashSet<long>();
        foreach (var raw in v.EnumerateArray())
        {
            if (raw.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("edge_indices must contain integers");
            var index = (int)raw.GetDouble();
            if (index < 0 || index >= all.Length)
                throw new ArgumentException($"edge index {index} is out of range for {all.Length} items");
            var edge = all[index];
            if (!seen.Add((long)edge.Tag))
                throw new ArgumentException("edge_indices must not contain duplicates");
            picked.Add(edge);
        }
        return picked.ToArray();
    }

    // ---- create_parametric_sketch（平面/轴向 + line/rect/circle/arc + 约束 + 尺寸表达式） ----

    private static readonly Dictionary<string, (double[] U, double[] V, double[] N)> PrincipalPlanes = new()
    {
        ["XY"] = (new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }),
        ["XZ"] = (new double[] { 1, 0, 0 }, new double[] { 0, 0, 1 }, new double[] { 0, -1, 0 }),
        ["YZ"] = (new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 }, new double[] { 1, 0, 0 }),
    };

    private static object CreateParametricSketch(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var name = SafeObjectName(OptString(p, "name") ?? string.Empty, "MCP_SKETCH");
        foreach (NXOpen.Sketch existing in work.Sketches)
            if (existing.Name == name)
                throw new ArgumentException("a sketch named " + name + " already exists");

        var planeName = (OptString(p, "plane") ?? "XY").Trim().ToUpperInvariant();
        if (!PrincipalPlanes.TryGetValue(planeName, out var axes))
            throw new ArgumentException("plane must be one of XY, XZ, or YZ");
        var origin = Vec3(p, "origin", new double[] { 0, 0, 0 });

        if (!p.TryGetProperty("geometry", out var geometrySpecs) || geometrySpecs.ValueKind != JsonValueKind.Array
            || geometrySpecs.GetArrayLength() == 0)
            throw new ArgumentException("geometry must be a non-empty list");
        var constraintSpecs = GetArray(p, "constraints");
        var dimensionSpecs = GetArray(p, "dimensions");

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA create parametric sketch");
        NXOpen.Sketch sketch = null;
        NXOpen.SketchInPlaceBuilder builder = null;
        try
        {
            var originPoint = new NXOpen.Point3d(origin[0], origin[1], origin[2]);
            var plane = work.Planes.CreatePlane(
                originPoint,
                new NXOpen.Vector3d(axes.N[0], axes.N[1], axes.N[2]),
                NXOpen.SmartObject.UpdateOption.WithinModeling);
            builder = work.Sketches.CreateSketchInPlaceBuilder2(null!);
            builder.PlaneReference = plane;
            var sketchAxis = work.Directions.CreateDirection(
                originPoint,
                new NXOpen.Vector3d(axes.U[0], axes.U[1], axes.U[2]),
                NXOpen.SmartObject.UpdateOption.WithinModeling);
            builder.AxisReference = sketchAxis;
            builder.SketchOrigin = work.Points.CreatePoint(originPoint);
            builder.PlaneOption = NXOpen.Sketch.PlaneOption.ExistingPlane;
            builder.OriginOption = NXOpen.OriginMethod.SpecifyPoint;
            builder.MakeOriginAssociative = true;
            sketch = (NXOpen.Sketch)builder.Commit();
            builder.Destroy();
            builder = null;
            sketch.SetName(name);
            sketch.Activate(NXOpen.Sketch.ViewReorient.False);

            var objects = new Dictionary<string, NXOpen.Curve>();
            var localRecords = new Dictionary<string, (double[] A, double[] B)>(); // line: start/end 局部坐标
            var autoConstraints = new List<JsonElement>();
            int sequence = 0;

            foreach (var spec in geometrySpecs.EnumerateArray())
            {
                if (spec.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("each geometry item must be an object");
                var kind = (OptString(spec, "type") ?? string.Empty).Trim().ToLowerInvariant();
                var baseName = SafeObjectName(OptString(spec, "name") ?? string.Empty, "G" + sequence);
                if (objects.ContainsKey(baseName))
                    throw new ArgumentException("duplicate geometry name: " + baseName);

                if (kind == "line")
                {
                    var startLocal = Point2(spec, "start");
                    var endLocal = Point2(spec, "end");
                    var curve = work.Curves.CreateLine(
                        LocalToWorld(origin, axes.U, axes.V, startLocal),
                        LocalToWorld(origin, axes.U, axes.V, endLocal));
                    curve.SetName(name + "_" + baseName);
                    sketch.AddGeometry(curve, NXOpen.Sketch.InferConstraintsOption.InferNoConstraints);
                    objects[baseName] = curve;
                    localRecords[baseName] = (startLocal, endLocal);
                    sequence += 1;
                }
                else if (kind == "rectangle")
                {
                    var corner = spec.TryGetProperty("origin", out _) ? Point2(spec, "origin") : new double[] { 0, 0 };
                    double w = PositiveNum(spec, "width", null);
                    double h = PositiveNum(spec, "height", null);
                    var pts = new[]
                    {
                        corner,
                        new[] { corner[0] + w, corner[1] },
                        new[] { corner[0] + w, corner[1] + h },
                        new[] { corner[0], corner[1] + h },
                    };
                    var lineNames = new List<string>();
                    for (int i = 0; i < 4; i++)
                    {
                        var lineName = baseName + "_" + i;
                        var curve = work.Curves.CreateLine(
                            LocalToWorld(origin, axes.U, axes.V, pts[i]),
                            LocalToWorld(origin, axes.U, axes.V, pts[(i + 1) % 4]));
                        curve.SetName(name + "_" + lineName);
                        sketch.AddGeometry(curve, NXOpen.Sketch.InferConstraintsOption.InferNoConstraints);
                        objects[lineName] = curve;
                        localRecords[lineName] = (pts[i], pts[(i + 1) % 4]);
                        lineNames.Add(lineName);
                    }
                    autoConstraints.Add(ConstraintJson("horizontal", lineNames[0]));
                    autoConstraints.Add(ConstraintJson("vertical", lineNames[1]));
                    autoConstraints.Add(ConstraintJson("horizontal", lineNames[2]));
                    autoConstraints.Add(ConstraintJson("vertical", lineNames[3]));
                    for (int i = 0; i < 4; i++)
                        autoConstraints.Add(CoincidentJson(lineNames[i], lineNames[(i + 1) % 4]));
                    sequence += 4;
                }
                else if (kind == "circle" || kind == "arc")
                {
                    var centerLocal = Point2(spec, "center");
                    double radius = PositiveNum(spec, "radius", null);
                    double startAngle = 0.0, endAngle = 2.0 * Math.PI;
                    if (kind == "arc")
                    {
                        startAngle = Math.PI / 180.0 * FiniteNum(spec, "start_angle_deg", 0.0);
                        endAngle = Math.PI / 180.0 * FiniteNum(spec, "end_angle_deg", 90.0);
                        if (endAngle <= startAngle)
                            throw new ArgumentException("arc.end_angle_deg must be greater than start_angle_deg");
                    }
                    var curve = work.Curves.CreateArc(
                        LocalToWorld(origin, axes.U, axes.V, centerLocal),
                        new NXOpen.Vector3d(axes.U[0], axes.U[1], axes.U[2]),
                        new NXOpen.Vector3d(axes.V[0], axes.V[1], axes.V[2]),
                        radius, startAngle, endAngle);
                    curve.SetName(name + "_" + baseName);
                    sketch.AddGeometry(curve, NXOpen.Sketch.InferConstraintsOption.InferNoConstraints);
                    objects[baseName] = curve;
                    sequence += 1;
                }
                else
                {
                    throw new ArgumentException("unsupported sketch geometry type: " + kind);
                }
            }

            var pointTypes = new Dictionary<string, NXOpen.Sketch.ConstraintPointType>
            {
                ["start"] = NXOpen.Sketch.ConstraintPointType.StartVertex,
                ["end"] = NXOpen.Sketch.ConstraintPointType.EndVertex,
                ["center"] = NXOpen.Sketch.ConstraintPointType.ArcCenter,
                ["none"] = NXOpen.Sketch.ConstraintPointType.None,
            };
            var constraintResults = new List<object?>();
            foreach (var spec in autoConstraints.Concat(constraintSpecs.EnumerateArray()))
            {
                if (spec.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("each constraint item must be an object");
                var kind = (OptString(spec, "type") ?? string.Empty).Trim().ToLowerInvariant();
                var firstName = OptString(spec, "geometry") ?? OptString(spec, "geometry1") ?? string.Empty;
                if (!objects.TryGetValue(firstName, out var firstObj))
                    throw new ArgumentException("constraint geometry was not found: " + firstName);
                var first = ConstraintGeometryFor(
                    firstObj,
                    pointTypes.TryGetValue((OptString(spec, "point1") ?? "none").ToLowerInvariant(),
                        out var pt1) ? pt1 : NXOpen.Sketch.ConstraintPointType.None);

                string createdJournal;
                if (kind == "horizontal") createdJournal = sketch.CreateHorizontalConstraint(first)?.JournalIdentifier;
                else if (kind == "vertical") createdJournal = sketch.CreateVerticalConstraint(first)?.JournalIdentifier;
                else if (kind == "fixed") createdJournal = sketch.CreateFixedConstraint(first)?.JournalIdentifier;
                else
                {
                    var secondName = OptString(spec, "geometry2") ?? string.Empty;
                    if (!objects.TryGetValue(secondName, out var secondObj))
                        throw new ArgumentException("constraint geometry2 was not found: " + secondName);
                    var second = ConstraintGeometryFor(
                        secondObj,
                        pointTypes.TryGetValue((OptString(spec, "point2") ?? "none").ToLowerInvariant(),
                            out var pt2) ? pt2 : NXOpen.Sketch.ConstraintPointType.None);
                    if (kind == "coincident") createdJournal = sketch.CreateCoincidentConstraint(first, second)?.JournalIdentifier;
                    else if (kind == "parallel") createdJournal = sketch.CreateParallelConstraint(first, second)?.JournalIdentifier;
                    else if (kind == "perpendicular") createdJournal = sketch.CreatePerpendicularConstraint(first, second)?.JournalIdentifier;
                    else if (kind == "equal_length") createdJournal = sketch.CreateEqualLengthConstraint(first, second)?.JournalIdentifier;
                    else if (kind == "concentric") createdJournal = sketch.CreateConcentricConstraint(first, second)?.JournalIdentifier;
                    else throw new ArgumentException("unsupported sketch constraint type: " + kind);
                }
                constraintResults.Add(new Dictionary<string, object?>
                {
                    ["type"] = kind,
                    ["journal_id"] = createdJournal,
                });
            }

            var dimensionResults = new List<object?>();
            var lengthUnit = FindLengthUnit(work);
            int dimIndex = 0;
            foreach (var spec in dimensionSpecs.EnumerateArray())
            {
                if (spec.ValueKind != JsonValueKind.Object)
                    throw new ArgumentException("each dimension item must be an object");
                var kind = (OptString(spec, "type") ?? string.Empty).Trim().ToLowerInvariant();
                var geometryName = OptString(spec, "geometry") ?? string.Empty;
                if (!objects.TryGetValue(geometryName, out var geometry))
                    throw new ArgumentException("dimension geometry was not found: " + geometryName);
                double value = PositiveNum(spec, "value", null);
                var dimensionName = SafeObjectName(OptString(spec, "name") ?? string.Empty, name + "_D" + dimIndex);
                var expressionName = SafeObjectName(name + "_" + dimensionName, "MCP_DIM_" + dimIndex);
                var expression = work.Expressions.CreateSystemExpressionWithUnits(
                    expressionName + " = " + value.ToString("G15", Inv), lengthUnit);

                double[] placementLocal;
                if (spec.TryGetProperty("origin", out _)) placementLocal = Point2(spec, "origin");
                else if (localRecords.TryGetValue(geometryName, out var rec))
                    placementLocal = new[] { (rec.A[0] + rec.B[0]) / 2.0 + 5.0, (rec.A[1] + rec.B[1]) / 2.0 + 5.0 };
                else placementLocal = new double[] { 5.0, 5.0 };
                var placement = LocalToWorld(origin, axes.U, axes.V, placementLocal);

                string createdDimJournal;
                if (kind == "horizontal" || kind == "vertical" || kind == "length")
                {
                    var first = DimensionGeometryFor(geometry, NXOpen.Sketch.AssocType.StartPoint);
                    var second = DimensionGeometryFor(geometry, NXOpen.Sketch.AssocType.EndPoint);
                    var constraintType = kind == "horizontal" ? NXOpen.Sketch.ConstraintType.HorizontalDim
                        : kind == "vertical" ? NXOpen.Sketch.ConstraintType.VerticalDim
                        : NXOpen.Sketch.ConstraintType.ParallelDim;
                    createdDimJournal = sketch.CreateDimension(constraintType, first, second, placement, expression)?.JournalIdentifier;
                }
                else if (kind == "diameter" || kind == "radius")
                {
                    var item = DimensionGeometryFor(geometry, NXOpen.Sketch.AssocType.None);
                    createdDimJournal = (kind == "diameter"
                        ? sketch.CreateDiameterDimension(item, placement, expression)
                        : sketch.CreateRadialDimension(item, placement, expression))?.JournalIdentifier;
                }
                else throw new ArgumentException("unsupported sketch dimension type: " + kind);

                dimensionResults.Add(new Dictionary<string, object?>
                {
                    ["type"] = kind,
                    ["geometry"] = geometryName,
                    ["expression"] = ExpressionRecord(expression),
                    ["journal_id"] = createdDimJournal,
                });
                dimIndex++;
            }

            sketch.Update();
            sketch.CalculateStatus();
            var status = sketch.GetStatus(out int _).ToString();
            sketch.Deactivate(NXOpen.Sketch.ViewReorient.False, NXOpen.Sketch.UpdateLevel.Model);
            session.SetUndoMarkName(mark, "NXA create parametric sketch");
            AssistantMarks.Record(work, mark, "NXA create parametric sketch");

            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["name"] = sketch.Name,
                ["journal_id"] = sketch.JournalIdentifier,
                ["feature_journal_id"] = sketch.Feature.JournalIdentifier,
                ["tag"] = (int)sketch.Tag,
                ["plane"] = planeName,
                ["origin"] = origin.ToList(),
                ["geometry"] = objects.Select(kv => SketchGeometryRecord(kv.Key, kv.Value)).ToList(),
                ["constraint_count"] = constraintResults.Count,
                ["constraints"] = constraintResults,
                ["dimension_count"] = dimensionResults.Count,
                ["dimensions"] = dimensionResults,
                ["status"] = status,
            };
        }
        catch
        {
            if (sketch != null)
            {
                try { if (sketch.IsActive) sketch.Deactivate(NXOpen.Sketch.ViewReorient.False, NXOpen.Sketch.UpdateLevel.SketchOnly); }
                catch { /* ignore */ }
            }
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
    }

    // ---- inspect_sketch ----

    private static object InspectSketch(JsonElement p)
    {
        var work = Work();
        var identifier = OptString(p, "sketch_id");
        var sketches = string.IsNullOrWhiteSpace(identifier)
            ? work.Sketches.Cast<NXOpen.Sketch>().ToList()
            : new List<NXOpen.Sketch> { FindSketch(work, identifier) };
        var results = new List<object?>();
        foreach (var sketch in sketches)
        {
            try { sketch.CalculateStatus(); } catch { /* 状态算不出不拦读取 */ }
            var geometry = sketch.GetAllGeometry();
            var expressions = sketch.GetAllExpressions();
            results.Add(new Dictionary<string, object?>
            {
                ["name"] = sketch.Name,
                ["journal_id"] = sketch.JournalIdentifier,
                ["feature_journal_id"] = sketch.Feature.JournalIdentifier,
                ["tag"] = (int)sketch.Tag,
                ["origin"] = new List<double> { sketch.Origin.X, sketch.Origin.Y, sketch.Origin.Z },
                ["status"] = sketch.GetStatus(out int _).ToString(),
                ["geometry_count"] = geometry.Length,
                ["geometry"] = geometry.Select(g => SketchGeometryRecord(null, g)).ToList(),
                ["expression_count"] = expressions.Length,
                ["expressions"] = expressions
                    .Select(e => e as NXOpen.Expression)
                    .Where(e => e != null)
                    .Select(ExpressionRecord)
                    .ToList(),
            });
        }
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["part"] = work.Leaf,
            ["sketch_count"] = results.Count,
            ["sketches"] = results,
        };
    }

    // ---- extrude_sketch（Section + Direction + 对称起止 + 实体 Create） ----

    private static object ExtrudeSketch(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var sketch = FindSketch(work, OptString(p, "sketch_id"));
        double distance = PositiveNum(p, "distance", null);
        double start = FiniteNum(p, "start", 0.0);
        var direction = Vec3(p, "direction", new double[] { 0, 0, 1 });
        double magnitude = Math.Sqrt(direction.Sum(v => v * v));
        if (magnitude <= 1e-12) throw new ArgumentException("direction must not be the zero vector");
        direction = direction.Select(v => v / magnitude).ToArray();
        var featureName = SafeObjectName(OptString(p, "feature_name") ?? string.Empty, "MCP_EXTRUDE");
        var geometry = sketch.GetAllGeometry();
        if (geometry.Length == 0) throw new ArgumentException("the sketch does not contain geometry");

        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA extrude sketch");
        NXOpen.Features.ExtrudeBuilder builder = null;
        int loopCount = 0;
        NXOpen.Features.Feature feature;
        try
        {
            var section = work.Sections.CreateSection(0.00095, 0.001, 0.5);
            builder = work.Features.CreateExtrudeBuilder(null!);
            section.AllowSelfIntersection(false);
            var rule = work.ScRuleFactory.CreateRuleCurveDumb(
                geometry.Select(g => (NXOpen.Curve)g).ToArray());
            section.AddToSection(
                new NXOpen.SelectionIntentRule[] { rule },
                geometry[0],
                null!,
                null!,
                new NXOpen.Point3d(0.0, 0.0, 0.0),
                NXOpen.Section.Mode.Create,
                false);
            loopCount = section.GetNumberOfLoops();
            if (loopCount < 1) throw new ArgumentException("the sketch does not contain a closed loop");
            builder.Section = section;
            builder.Direction = work.Directions.CreateDirection(
                new NXOpen.Point3d(0.0, 0.0, 0.0),
                new NXOpen.Vector3d(direction[0], direction[1], direction[2]),
                NXOpen.SmartObject.UpdateOption.WithinModeling);
            builder.Limits.StartExtend.Value.RightHandSide = FmtNum(start);
            builder.Limits.EndExtend.Value.RightHandSide = FmtNum(start + distance);
            builder.BooleanOperation.Type = NXOpen.GeometricUtilities.BooleanOperation.BooleanType.Create;
            feature = builder.CommitFeature();
            feature.SetName(featureName);
            sketch.Blank();
            session.SetUndoMarkName(mark, "NXA extrude sketch");
            AssistantMarks.Record(work, mark, "NXA extrude sketch");
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
        finally
        {
            try { builder?.Destroy(); } catch { /* ignore */ }
        }
        var bodies = feature.GetBodies();
        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["name"] = feature.Name,
            ["journal_id"] = feature.JournalIdentifier,
            ["sketch"] = sketch.Name,
            ["distance"] = distance,
            ["start"] = start,
            ["direction"] = direction.ToList(),
            ["section_loop_count"] = loopCount,
            ["feature_body_count"] = bodies.Length,
            ["part_body_count"] = CountBodies(work),
        };
    }

    // ---- set_feature_expression（改表达式→更新→失败回滚，镜像 _op_set_feature_expression） ----

    private static object SetFeatureExpression(JsonElement p)
    {
        var work = Work();
        var session = Session.GetSession();
        var feature = FindFeature(work, FeatureId(p));
        var expressions = feature.GetExpressions();
        var expressionId = OptString(p, "expression_id")
            ?? (p.TryGetProperty("expression_id", out var eid) && eid.ValueKind == JsonValueKind.Number
                ? eid.GetRawText() : null);
        var text = (expressionId ?? string.Empty).Trim();
        NXOpen.Expression expression = null;
        if (int.TryParse(text, NumberStyles.Integer, Inv, out int index))
        {
            if (index >= 0 && index < expressions.Length) expression = expressions[index];
        }
        if (expression == null)
            expression = expressions.FirstOrDefault(e =>
                text == e.Name || text == e.JournalIdentifier);
        if (expression == null)
            throw new ArgumentException("expression was not found on feature: " + text);

        var rhs = (OptString(p, "right_hand_side") ?? string.Empty).Trim();
        if (rhs.Length == 0) throw new ArgumentException("right_hand_side must be a non-empty NX expression");

        var oldRecord = ExpressionRecord(expression);
        var mark = session.SetUndoMark(Session.MarkVisibility.Visible, "NXA set feature expression");
        int updateErrors;
        try
        {
            expression.RightHandSide = rhs;
            updateErrors = session.UpdateManager.DoUpdate(mark);
            var newRecord = ExpressionRecord(expression);
            var errors = feature.GetFeatureErrorMessages().ToList();
            if (updateErrors != 0 || errors.Count > 0 || feature.IsOutOfDate())
                throw new InvalidOperationException(
                    $"feature update failed: update_errors={updateErrors} messages=[{string.Join("; ", errors)}] out_of_date={feature.IsOutOfDate()}");
            session.SetUndoMarkName(mark, "NXA set feature expression");
            AssistantMarks.Record(work, mark, "NXA set feature expression");
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["part"] = work.Leaf,
                ["feature_name"] = feature.Name,
                ["feature_journal_id"] = feature.JournalIdentifier,
                ["old_expression"] = oldRecord,
                ["new_expression"] = newRecord,
                ["update_error_count"] = updateErrors,
                ["is_out_of_date"] = feature.IsOutOfDate(),
                ["error_messages"] = errors,
            };
        }
        catch
        {
            try { session.UndoToMark(mark, null); } catch { /* 尽力回滚 */ }
            throw;
        }
    }

    // ---- 共享小工具 ----

    private static NXOpen.Sketch FindSketch(Part work, string? identifier)
    {
        var sketches = work.Sketches.Cast<NXOpen.Sketch>().ToList();
        if (string.IsNullOrWhiteSpace(identifier))
        {
            if (sketches.Count == 1) return sketches[0];
            throw new ArgumentException("sketch_id is required when the part does not contain exactly one sketch");
        }
        var wanted = identifier.Trim();
        if (int.TryParse(wanted, NumberStyles.Integer, Inv, out int index) && index >= 0 && index < sketches.Count)
            return sketches[index];
        foreach (var sketch in sketches)
        {
            var featureJournal = sketch.Feature == null ? string.Empty : sketch.Feature.JournalIdentifier;
            if (wanted == sketch.Name || wanted == sketch.JournalIdentifier || wanted == featureJournal)
                return sketch;
        }
        throw new ArgumentException("sketch was not found: " + wanted);
    }

    private static NXOpen.Sketch.ConstraintGeometry ConstraintGeometryFor(
        NXOpen.NXObject geometry, NXOpen.Sketch.ConstraintPointType pointType)
        => new NXOpen.Sketch.ConstraintGeometry
        {
            Geometry = geometry,
            PointType = pointType,
            SplineDefiningPointIndex = 0,
        };

    private static NXOpen.Sketch.DimensionGeometry DimensionGeometryFor(
        NXOpen.NXObject geometry, NXOpen.Sketch.AssocType assocType)
        => new NXOpen.Sketch.DimensionGeometry
        {
            Geometry = geometry,
            AssocType = assocType,
            AssocValue = 0,
        };

    private static NXOpen.Unit FindLengthUnit(Part work)
    {
        Exception last = null;
        foreach (var name in new[] { "MilliMeter", "Millimeter", "mm", "Inch" })
        {
            try
            {
                var unit = work.UnitCollection.FindObject(name) as NXOpen.Unit;
                if (unit != null) return unit;
            }
            catch (Exception ex) { last = ex; }
        }
        throw new InvalidOperationException("NX length unit could not be resolved: " + (last?.Message ?? "no match"));
    }

    private static object SketchGeometryRecord(string id, NXOpen.NXObject geometry)
    {
        var record = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = geometry?.Name,
            ["journal_id"] = geometry?.JournalIdentifier,
            ["tag"] = (int)(geometry?.Tag ?? NXOpen.Tag.Null),
            ["type"] = geometry?.GetType().Name,
        };
        if (geometry is NXOpen.Line line)
        {
            record["start"] = new List<double> { line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z };
            record["end"] = new List<double> { line.EndPoint.X, line.EndPoint.Y, line.EndPoint.Z };
        }
        if (geometry is NXOpen.Arc arc) record["radius"] = arc.Radius;
        return record;
    }

    private static NXOpen.Point3d LocalToWorld(double[] origin, double[] uAxis, double[] vAxis, double[] uv)
        => new NXOpen.Point3d(
            origin[0] + uv[0] * uAxis[0] + uv[1] * vAxis[0],
            origin[1] + uv[0] * uAxis[1] + uv[1] * vAxis[1],
            origin[2] + uv[0] * uAxis[2] + uv[1] * vAxis[2]);

    internal static int CountBodies(Part work)
    {
        int n = 0;
        foreach (Body unused in work.Bodies) n++;
        return n;
    }

    internal static int CountFeatures(Part work)
    {
        int n = 0;
        foreach (NXOpen.Features.Feature unused in work.Features) n++;
        return n;
    }

    private static string FmtNum(double v) => v.ToString("0.####################", Inv);

    /// <summary>镜像 _safe_object_name：仅字母数字下划线，数字开头补 MCP_，截断 120。</summary>
    private static string SafeObjectName(string value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) text = fallback;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        var cleaned = sb.ToString();
        if (cleaned.Length == 0) cleaned = fallback;
        if (char.IsDigit(cleaned[0])) cleaned = "MCP_" + cleaned;
        return cleaned.Length > 120 ? cleaned.Substring(0, 120) : cleaned;
    }

    private static string? OptString(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static JsonElement ConstraintJson(string type, string geometry)
        => JsonDocument.Parse("{\"type\":\"" + type + "\",\"geometry\":\"" + geometry + "\"}").RootElement;

    private static JsonElement CoincidentJson(string g1, string g2)
        => JsonDocument.Parse(
            "{\"type\":\"coincident\",\"geometry1\":\"" + g1 + "\",\"point1\":\"end\",\"geometry2\":\"" + g2 + "\",\"point2\":\"start\"}").RootElement;

    private static JsonElement GetArray(JsonElement p, string name)
        => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v
            : JsonDocument.Parse("[]").RootElement;

    private static double FiniteNum(JsonElement p, string name, double dflt)
    {
        if (!p.TryGetProperty(name, out var v)) return dflt;
        if (v.ValueKind != JsonValueKind.Number) throw new ArgumentException(name + " must be a number");
        var d = v.GetDouble();
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new ArgumentException(name + " must be finite");
        return d;
    }

    private static double PositiveNum(JsonElement p, string name, double? dflt)
    {
        var v = p.TryGetProperty(name, out _) ? FiniteNum(p, name, dflt ?? double.NaN) : (dflt ?? double.NaN);
        if (double.IsNaN(v)) throw new ArgumentException(name + " is required");
        if (v <= 0) throw new ArgumentException(name + " must be finite and greater than zero");
        return v;
    }

    private static double[] Vec3(JsonElement p, string name, double[] dflt)
    {
        if (!p.TryGetProperty(name, out var v)) return dflt;
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 3)
            throw new ArgumentException(name + " must contain exactly three coordinates");
        var values = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (values.Any(double.IsNaN) || values.Any(double.IsInfinity))
            throw new ArgumentException(name + " coordinates must be finite");
        return values;
    }

    private static double[] Point2(JsonElement p, string name)
    {
        if (!p.TryGetProperty(name, out var v))
            throw new ArgumentException(name + " is required");
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2)
            throw new ArgumentException(name + " must contain exactly two coordinates");
        var values = v.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (values.Any(double.IsNaN) || values.Any(double.IsInfinity))
            throw new ArgumentException(name + " coordinates must be finite");
        return values;
    }
}
