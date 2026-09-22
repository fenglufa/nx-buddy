using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClosedXML.Excel;
using NxAssistant.Core.Protocol;

namespace NxAssistant.Mcp;

/// <summary>
/// 审图报告输出（PRD §7.3 第 5 步）：审图报告.xlsx（摘要 + 明细两页，列对齐 §7.5 验收金样）
/// 与 summary.json（Agent 直接读计数，不解析 xlsx）。xlsx 生成库 = 客户确认的 ClosedXML（MIT）。
/// </summary>
internal static class ReviewReport
{
    private static readonly string[] Columns =
    {
        "图号", "版本", "文件", "规则ID", "规则名", "力度", "严重级", "对象", "实际值", "说明", "建议", "引用占位表", "标准",
    };

    /// <summary>写 审图报告.xlsx + summary.json，返回 summary.json 路径。</summary>
    public static string Write(string runDir, ReviewOrchestrator.RunState run,
        List<Dictionary<string, object?>> findings)
    {
        var xlsxPath = Path.Combine(runDir, "审图报告.xlsx");
        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var sum = wb.AddWorksheet("摘要");
            sum.Cell(1, 1).Value = "NX 小助手审图报告";
            sum.Cell(1, 1).Style.Font.Bold = true;
            string[][] rows =
            {
                new[] { "run_id", run.RunId },
                new[] { "状态", run.Status },
                new[] { "目录", run.Path },
                new[] { "开始", run.StartedAt },
                new[] { "结束", run.UpdatedAt },
                new[] { "总张数", run.Total.ToString() },
                new[] { "已审", run.Done.ToString() },
                new[] { "阻断项", Count(findings, "block").ToString() },
                new[] { "警告项", Count(findings, "warn").ToString() },
                new[] { "打开/抽取失败", run.FailedFiles.Count.ToString() },
                new[] { "备注", run.Note ?? run.ErrorMessage ?? "" },
            };
            for (int i = 0; i < rows.Length; i++)
            {
                sum.Cell(i + 3, 1).Value = rows[i][0];
                sum.Cell(i + 3, 2).Value = rows[i][1];
            }
            sum.Column(1).Width = 18;
            sum.Column(2).Width = 60;
            if (run.FailedFiles.Count > 0)
            {
                int r = rows.Length + 4;
                sum.Cell(r, 1).Value = "失败文件清单";
                sum.Cell(r, 1).Style.Font.Bold = true;
                foreach (var f in run.FailedFiles)
                    sum.Cell(++r, 2).Value = $"{f.File}：{f.Error}";
            }

            var det = wb.AddWorksheet("明细");
            for (int c = 0; c < Columns.Length; c++)
            {
                det.Cell(1, c + 1).Value = Columns[c];
                det.Cell(1, c + 1).Style.Font.Bold = true;
            }
            int row = 2;
            foreach (var f in findings)
            {
                det.Cell(row, 1).Value = Str(f, "drawing_no");
                det.Cell(row, 2).Value = Str(f, "version");
                det.Cell(row, 3).Value = Path.GetFileName(Str(f, "file"));
                det.Cell(row, 4).Value = Str(f, "rule_id");
                det.Cell(row, 5).Value = Str(f, "name");
                det.Cell(row, 6).Value = Str(f, "enforcement");
                det.Cell(row, 7).Value = Str(f, "severity");
                det.Cell(row, 8).Value = Str(f, "object");
                det.Cell(row, 9).Value = Str(f, "actual");
                det.Cell(row, 10).Value = Str(f, "message");
                det.Cell(row, 11).Value = List(f, "suggestions");
                det.Cell(row, 12).Value = Bool(f, "placeholder") ? "是" : "";
                det.Cell(row, 13).Value = Str(f, "standard");
                if (Str(f, "enforcement") == "block")
                    det.Row(row).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE7E6");
                row++;
            }
            det.Columns().AdjustToContents(1, Math.Min(row, 200));
            wb.SaveAs(ms);
        }
        // MemoryStream.ToArray() 在流被关闭后仍可用，故 SaveAs 关掉它也不影响落盘。
        File.WriteAllBytes(xlsxPath, ms.ToArray());

        var summaryPath = Path.Combine(runDir, "summary.json");
        var payload = new Dictionary<string, object?>
        {
            ["run_id"] = run.RunId,
            ["status"] = run.Status,
            ["path"] = run.Path,
            ["total"] = run.Total,
            ["done"] = run.Done,
            ["blocking_total"] = Count(findings, "block"),
            ["warn_total"] = Count(findings, "warn"),
            ["files_with_findings"] = findings.Select(f => Str(f, "file")).Distinct().Count(),
            ["failed_files"] = run.FailedFiles,
            ["placeholder_rules"] = findings.Where(f => Bool(f, "placeholder"))
                .Select(f => Str(f, "rule_id")).Distinct().OrderBy(x => x).ToList(),
            ["report_path"] = xlsxPath,
        };
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(payload, NxJson.Options));
        return summaryPath;
    }

    private static int Count(List<Dictionary<string, object?>> findings, string enforcement) =>
        findings.Count(f => Str(f, "enforcement") == enforcement);

    private static string Str(Dictionary<string, object?> f, string key) =>
        f.TryGetValue(key, out var v) && v != null ? CellText(v) : "";

    private static bool Bool(Dictionary<string, object?> f, string key) =>
        f.TryGetValue(key, out var v) && v is JsonElement je && je.ValueKind == JsonValueKind.True;

    private static string List(Dictionary<string, object?> f, string key)
    {
        if (!f.TryGetValue(key, out var v) || v is not JsonElement je || je.ValueKind != JsonValueKind.Array)
            return "";
        return string.Join(" / ", je.EnumerateArray().Select(e => e.ToString()));
    }

    private static string CellText(object v) => v switch
    {
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : je.ToString(),
        _ => Convert.ToString(v) ?? "",
    };
}
