using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NxAssistant.Core;
using NxAssistant.Core.Protocol;
using NxAssistant.Rules;

namespace NxAssistant.Mcp;

/// <summary>
/// 审图长任务编排（PRD §7.3 + prd-02 状态契约）。
/// review_folder 立即返回 run_id，后台串行逐张调插件 extract_evidence → RuleEngine 判定；
/// 状态与 findings 一律落磁盘（runs\&lt;run_id&gt;\run.json + findings.jsonl），进程重启后仍能查；
/// run.json 里 running 但宿主已不认识该 run 时判 interrupted（can_resume）。
/// </summary>
internal static class ReviewOrchestrator
{
    private static readonly object _gate = new();
    private static readonly Dictionary<string, Task> _active = new();

    public static string WorkspaceRoot => Path.GetFullPath(NxaSettings.Resolve(
        "NXA_WORKSPACE", "workspace",
        () => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXAssistant", "workspace")));

    private static string RunsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NXAssistant", "runs");

    // ---------------- review_folder ----------------

    public static JsonElement Start(NxPluginClient plugin, string path, string? resumeRunId)
    {
        var dir = ResolveSandboxDir(path);
        var files = Directory.GetFiles(dir, "*.prt", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        RunState? resume = null;
        if (!string.IsNullOrWhiteSpace(resumeRunId))
        {
            resume = ReadRun(resumeRunId.Trim())
                ?? throw new FileNotFoundException($"找不到可续跑的 run: {resumeRunId}");
            if (resume.Status != "interrupted" && resume.Status != "cancelled" && resume.Status != "failed")
                throw new InvalidOperationException($"run {resumeRunId} 状态为 {resume.Status}，不支持续跑");
            if (!string.Equals(Path.GetFullPath(resume.Path), dir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("resume_run_id 的目录与本次 path 不一致");
            files = files.Where(f => !resume.Processed.Contains(Path.GetFullPath(f), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        lock (_gate)
        {
            if (_active.Count > 0)
            {
                var busy = new Dictionary<string, object?>
                {
                    ["ok"] = false,
                    ["error"] = $"已有审图任务在跑：run_id={_active.Keys.First()}。请先用 review_status 查询，或等其结束。",
                    ["error_type"] = "REVIEW_BUSY",
                    ["run_id"] = _active.Keys.First(),
                };
                return JsonDocument.Parse(JsonSerializer.Serialize(busy)).RootElement;
            }

            var run = new RunState
            {
                RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" +
                        Guid.NewGuid().ToString("N").Substring(0, 3),
                Status = files.Count == 0 ? "completed" : "running",
                Path = dir,
                Total = files.Count,
                Done = resume?.Done ?? 0,
                OwnerPid = Environment.ProcessId,
                StartedAt = resume?.StartedAt ?? Now(),
                UpdatedAt = Now(),
                CanResume = true,
            };
            if (resume != null)
            {
                run.Processed.AddRange(resume.Processed);
                run.FailedFiles.AddRange(resume.FailedFiles);
            }
            var runDir = RunDir(run.RunId);
            Directory.CreateDirectory(runDir);
            WriteRun(run);

            if (files.Count == 0 && resume == null)
            {
                Finalize(run, plugin, "completed", "目录内没有 *.prt 可审");
                return ToJson(run, accepted: true);
            }

            var task = Task.Run(() => WorkerAsync(plugin, run, files));
            lock (_gate) { _active[run.RunId] = task; }
            return ToJson(run, accepted: true);
        }
    }

    private static async Task WorkerAsync(NxPluginClient plugin, RunState run, List<string> files)
    {
        var pack = HostRules.LoadPackForReview(out var packError);
        if (pack == null)
        {
            lock (_gate) { _active.Remove(run.RunId); }
            run.ErrorCode = "RULE_PACK_UNAVAILABLE";
            run.ErrorMessage = packError ?? "规则包不可用";
            Finalize(run, plugin, "failed", run.ErrorMessage);
            return;
        }

        var findingsPath = Path.Combine(RunDir(run.RunId), "findings.jsonl");
        foreach (var file in files)
        {
            run.Current = Path.GetFileName(file);
            if (File.Exists(CancelFlag(run.RunId)))
            {
                Finalize(run, plugin, "cancelled", "用户请求停止审核，报告只含已完成部分");
                break;
            }
            WriteRun(run);
            try
            {
                var result = await plugin.CallAsync(
                    NxAssistant.Core.Protocol.MethodNames.ExtractEvidence,
                    new Dictionary<string, object?> { ["file_path"] = file }).ConfigureAwait(false);
                var ev = ParseEvidence(result);
                string drawingNo = Path.GetFileNameWithoutExtension(file), revision = "";
                if (ev.Drawing != null && ev.Drawing.Sheets.Count > 0)
                {
                    // 有工程图证据后报告行改读真实图号/版本（title_block_fields 别名表）
                    var s0 = ev.Drawing.Sheets[0];
                    var dn = RuleEngine.TitleField(pack, s0, "drawing_no");
                    if (!string.IsNullOrEmpty(dn)) drawingNo = dn;
                    revision = RuleEngine.TitleField(pack, s0, "revision");
                }
                foreach (var f in RuleEngine.Evaluate(pack, ev))
                    AppendFinding(findingsPath, run, file, f, drawingNo, revision);
            }
            catch (NxNotConnectedException ex)
            {
                // NX/插件没了：整批无法继续（prd-02 failed→中断可续跑）
                run.ErrorCode = "NX_NOT_CONNECTED";
                run.ErrorMessage = ex.Message;
                Finalize(run, plugin, "interrupted", ex.Message);
                break;
            }
            catch (Exception ex)
            {
                run.FailedFiles.Add(new FailedFile { File = file, Error = ex.Message });
            }
            run.Processed.Add(Path.GetFullPath(file));
            run.Done++;
        }

        if (run.Status == "running")
            Finalize(run, plugin,
                run.FailedFiles.Count > 0 ? "completed_with_errors" : "completed", null);
        lock (_gate) { _active.Remove(run.RunId); }
    }

    private static void Finalize(RunState run, NxPluginClient plugin, string status, string? note)
    {
        run.Status = status;
        run.Current = "";
        run.Note = note;
        var runDir = RunDir(run.RunId);
        try
        {
            var findings = ReadFindings(run.RunId, 0, int.MaxValue).items;
            var summaryPath = ReviewReport.Write(runDir, run, findings);
            run.ReportPath = Path.Combine(runDir, "审图报告.xlsx");
            run.SummaryPath = summaryPath;
        }
        catch (Exception ex)
        {
            run.ErrorCode ??= "REPORT_WRITE_FAILED";
            run.ErrorMessage ??= ex.Message;
        }
        run.UpdatedAt = Now();
        WriteRun(run);
    }

    // ---------------- review_status ----------------

    public static JsonElement Status(string runId, bool cancel)
    {
        var run = ReadRun(runId.Trim())
            ?? throw new FileNotFoundException($"找不到 run_id: {runId}");
        bool tracked;
        lock (_gate) { tracked = _active.ContainsKey(run.RunId); }
        if (run.Status == "running" && !tracked)
        {
            // 磁盘说在跑，但本宿主进程不认识它：宿主重启/NX 崩溃过 → 中断态
            run.Status = "interrupted";
            run.ErrorCode ??= "HOST_OR_NX_LOST";
            run.ErrorMessage ??= "运行状态滞留：宿主进程或 NX 已不在此任务，可从下一张续跑";
            WriteRun(run);
        }
        if (cancel && run.Status == "running")
            File.WriteAllText(CancelFlag(run.RunId), "cancel");
        return ToJson(run, accepted: false);
    }

    // ---------------- review_findings ----------------

    public static JsonElement Findings(string runId, int offset, int limit)
    {
        var run = ReadRun(runId.Trim())
            ?? throw new FileNotFoundException($"找不到 run_id: {runId}");
        var (items, total) = ReadFindings(run.RunId, offset, Math.Clamp(limit, 1, 500));
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["run_id"] = run.RunId,
            ["status"] = run.Status,
            ["total"] = total,
            ["offset"] = offset,
            ["returned"] = items.Count,
            ["findings"] = items,
            ["blocking_total"] = items.Count(i => Str(i, "enforcement") == "block"),
        };
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }

    private static string Str(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.String
            ? je.GetString() ?? "" : "";

    private static (List<Dictionary<string, object?>> items, int total) ReadFindings(
        string runId, int offset, int limit)
    {
        var path = Path.Combine(RunDir(runId), "findings.jsonl");
        var all = new List<Dictionary<string, object?>>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(line);
                    if (d != null) all.Add(d);
                }
                catch { /* 末行撕裂容错 */ }
            }
        }
        return (all.Skip(Math.Max(0, offset)).Take(limit).ToList(), all.Count);
    }

    // ---------------- run.json 读写 ----------------

    private static string RunDir(string runId) => Path.Combine(RunsRoot, runId);
    private static string CancelFlag(string runId) => Path.Combine(RunDir(runId), "cancel");
    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static void WriteRun(RunState run)
    {
        var dir = RunDir(run.RunId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "run.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(run, NxJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static RunState? ReadRun(string runId)
    {
        var safe = string.Concat(runId.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(RunsRoot, safe, "run.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), NxJson.Options);
        }
        catch { return null; }
    }

    private static string ResolveSandboxDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path must be a non-empty directory");
        var root = Path.GetFullPath(WorkspaceRoot);
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path.Trim()));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"审图目录必须位于工作区根目录内（FILE-001）：{root}");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("目录不存在: " + full);
        return full;
    }

    private static JsonElement ToJson(RunState run, bool accepted)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["run_id"] = run.RunId,
            ["status"] = run.Status,
            ["total"] = run.Total,
            ["done"] = run.Done,
            ["current"] = run.Current,
            ["path"] = run.Path,
            ["started_at"] = run.StartedAt,
            ["updated_at"] = run.UpdatedAt,
            ["failed_files"] = run.FailedFiles,
            ["report_path"] = run.ReportPath,
            ["summary_path"] = run.SummaryPath,
            ["error_code"] = run.ErrorCode,
            ["error_message"] = run.ErrorMessage,
            ["can_resume"] = run.Status is "interrupted" or "cancelled" or "failed",
            ["note"] = run.Note,
        };
        _ = accepted;
        return JsonDocument.Parse(JsonSerializer.Serialize(payload, NxJson.Options)).RootElement;
    }

    // ---------------- 插件证据 JSON → Evidence ----------------

    private static Evidence ParseEvidence(JsonElement result)
    {
        var part = new PartEvidence();
        if (!result.TryGetProperty("evidence", out var ev) ||
            !ev.TryGetProperty("part", out var pj))
            return new Evidence { Part = part };

        if (pj.TryGetProperty("solid_body_count", out var sbc) && sbc.ValueKind == JsonValueKind.Number)
            part.SolidBodyCount = sbc.GetInt32();
        if (pj.TryGetProperty("sheet_bodies", out var sheets))
            foreach (var s in sheets.EnumerateArray())
                part.SheetBodies.Add(new SheetBodyEvidence
                {
                    Name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    IsProcessAux = s.TryGetProperty("is_process_aux", out var a) && a.GetBoolean(),
                });
        if (pj.TryGetProperty("holes", out var holes))
            foreach (var h in holes.EnumerateArray())
                part.Holes.Add(new HoleEvidence
                {
                    Diameter = h.TryGetProperty("diameter", out var dia) ? dia.GetDouble() : 0,
                    Kind = h.TryGetProperty("kind", out var k) ? k.GetString() ?? "simple_through" : "simple_through",
                    Source = h.TryGetProperty("source", out var src) ? src.GetString() ?? "model" : "model",
                    FromArrayOrHoleSet = h.TryGetProperty("from_array_or_hole_set", out var fa) && fa.GetBoolean(),
                    FeatureName = h.TryGetProperty("feature_name", out var fn) ? fn.GetString() : null,
                });
        if (pj.TryGetProperty("fillets", out var fillets))
            foreach (var fl in fillets.EnumerateArray())
                if (fl.TryGetProperty("radius", out var r) && r.ValueKind == JsonValueKind.Number)
                    part.Fillets.Add(new FilletEvidence { Radius = r.GetDouble() });
        if (pj.TryGetProperty("plate_thickness", out var pt) && pt.ValueKind == JsonValueKind.Number)
            part.PlateThickness = pt.GetDouble();
        if (pj.TryGetProperty("hole_patterns", out var patterns))
            foreach (var hp in patterns.EnumerateArray())
                part.HolePatterns.Add(new HolePatternEvidence
                {
                    Name = hp.TryGetProperty("name", out var hn) ? hn.GetString() ?? "" : "",
                    Pcd = hp.TryGetProperty("pcd", out var pcd) && pcd.ValueKind == JsonValueKind.Number
                        ? pcd.GetDouble() : null,
                    HoleCount = hp.TryGetProperty("hole_count", out var hc) && hc.ValueKind == JsonValueKind.Number
                        ? hc.GetInt32() : null,
                    HoleDiameter = hp.TryGetProperty("hole_diameter", out var hd) &&
                                   hd.ValueKind == JsonValueKind.Number ? hd.GetDouble() : null,
                });

        DrawingEvidence? drawing = null;
        if (ev.TryGetProperty("drawing", out var dj) && dj.ValueKind == JsonValueKind.Object)
        {
            drawing = new DrawingEvidence();
            if (dj.TryGetProperty("has_weld_symbol", out var ws) && ws.ValueKind == JsonValueKind.True)
                drawing.HasWeldSymbol = true;
            if (dj.TryGetProperty("sheets", out var dss))
                foreach (var s in dss.EnumerateArray())
                {
                    var se = new SheetEvidence
                    {
                        FrameId = s.TryGetProperty("frame_id", out var fid) ? fid.GetString() ?? "" : "",
                        TemplateName = s.TryGetProperty("template_name", out var tn)
                            ? tn.GetString() ?? "" : "",
                        ViewCount = s.TryGetProperty("view_count", out var vc) &&
                                    vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : null,
                    };
                    if (s.TryGetProperty("title_block_raw", out var tbr) &&
                        tbr.ValueKind == JsonValueKind.Object)
                        foreach (var kv in tbr.EnumerateObject())
                            se.TitleBlock[kv.Name] = kv.Value.GetString() ?? "";
                    if (s.TryGetProperty("layers_used", out var lus))
                        foreach (var lu in lus.EnumerateArray())
                            if (lu.ValueKind == JsonValueKind.String)
                                se.LayersUsed.Add(lu.GetString()!);
                    if (s.TryGetProperty("dimensions", out var ds))
                        foreach (var d in ds.EnumerateArray())
                            se.Dimensions.Add(new DimensionEvidence
                            {
                                Layer = d.TryGetProperty("layer", out var dl)
                                    ? dl.GetString() ?? "" : "",
                            });
                    drawing.Sheets.Add(se);
                }
        }
        return new Evidence { Part = part, Drawing = drawing };
    }

    private static void AppendFinding(string findingsPath, RunState run, string file, Finding f,
        string drawingNo, string revision)
    {
        var row = new Dictionary<string, object?>
        {
            ["run_id"] = run.RunId,
            ["file"] = file,
            ["drawing_no"] = drawingNo, // 无工程图证据时以文件名代指；TITLE 批次后有真实图号
            ["version"] = revision,
            ["rule_id"] = f.RuleId,
            ["standard"] = f.Standard,
            ["group"] = f.Group,
            ["name"] = f.Name,
            ["severity"] = f.Severity,
            ["enforcement"] = f.Enforcement,
            ["object"] = f.Subject,
            ["actual"] = f.Value,
            ["message"] = f.Message,
            ["suggestions"] = f.Suggestions,
            ["placeholder"] = f.Placeholder,
        };
        File.AppendAllText(findingsPath, JsonSerializer.Serialize(row, NxJson.Options) + "\n");
    }

    // ---------------- 状态模型 ----------------

    public sealed class FailedFile
    {
        public string File { get; set; } = "";
        public string Error { get; set; } = "";
    }

    public sealed class RunState
    {
        public string RunId { get; set; } = "";
        public string Status { get; set; } = "queued";
        public string Path { get; set; } = "";
        public int Total { get; set; }
        public int Done { get; set; }
        public string Current { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public int OwnerPid { get; set; }
        public List<string> Processed { get; set; } = new();
        public List<FailedFile> FailedFiles { get; set; } = new();
        public string? ReportPath { get; set; }
        public string? SummaryPath { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public bool CanResume { get; set; } = true;
        public string? Note { get; set; }
    }
}
