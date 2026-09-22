using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NxAssistant.Tray;

/// <summary>
/// 审图工作台（第十四片）：托盘经自己的 MCP stdio 宿主调 review_folder/review_status/review_findings，
/// 与 Agent 走完全同一套工具、授权闸门与 run 状态机——不存在第二套判定逻辑。
/// 关键护栏：review_status 会把"磁盘说在跑、但本宿主不认识"的 run 判成 interrupted，
/// 所以只有**本托盘宿主发起的 run** 才走 MCP 轮询/取消；他方在跑的 run 只读 run.json 展示。
/// </summary>
internal sealed class ReviewTab : UserControl
{
    private readonly McpHostClient _client;
    private readonly string _runsRoot;
    private readonly TextBox _dirBox;
    private readonly ComboBox _runsCombo;
    private readonly Label _progress;
    private readonly DataGridView _grid;
    private readonly System.Windows.Forms.Timer _poll;
    private readonly List<string> _runIds = new();

    private string? _ownRunId; // 仅本托盘宿主发起的 run 允许 MCP 轮询/取消
    private bool _busy;

    public ReviewTab(TrayStatus s, McpHostClient client)
    {
        _client = client;
        _runsRoot = s.RunsRoot;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 4, 6, 0) };
        top.Controls.Add(new Label { Text = "审图目录（须在 workspace 根内）：", AutoSize = true, Margin = new Padding(3, 7, 0, 0) });
        _dirBox = new TextBox { Width = 430, Text = s.WorkspaceRoot, Margin = new Padding(3, 5, 0, 0) };
        top.Controls.Add(_dirBox);
        var browse = new Button { Text = "浏览…", AutoSize = true };
        browse.Click += (_, _) => Browse();
        top.Controls.Add(browse);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6) };
        var start = new Button { Text = "发起审图", AutoSize = true };
        start.Click += async (_, _) => await StartReviewAsync(resume: null);
        var resume = new Button { Text = "从选中 run 续跑", AutoSize = true };
        resume.Click += async (_, _) => await StartReviewAsync(resume: SelectedRunId());
        var cancel = new Button { Text = "取消（当前张完成后生效）", AutoSize = true };
        cancel.Click += async (_, _) => await CancelAsync();
        var fetch = new Button { Text = "拉取明细", AutoSize = true };
        fetch.Click += async (_, _) => await LoadFindingsAsync(SelectedRunId());
        var report = new Button { Text = "打开审图报告", AutoSize = true };
        report.Click += (_, _) => OpenReport();
        var refresh = new Button { Text = "刷新 run 列表", AutoSize = true };
        refresh.Click += (_, _) => ReloadRuns();
        actions.Controls.AddRange(new Control[] { start, resume, cancel, fetch, report, refresh });

        _progress = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 0),
            Text = "就绪。把 *.prt 放进审图目录（可含子目录）→“发起审图”；进度每 2 秒自动刷新，与 Agent 同一套判定。",
        };

        _runsCombo = new ComboBox
        {
            Dock = DockStyle.Top,
            Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _runsCombo.SelectedIndexChanged += (_, _) => { _ = LoadFindingsIfFinished(SelectedRunId()); };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            RowHeadersVisible = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        AddCol("drawing_no", "图号", 110);
        AddCol("version", "版本", 50);
        AddCol("file", "文件", 170);
        AddCol("rule_id", "规则", 120);
        AddCol("name", "名称", 180);
        AddCol("enforcement", "级别", 60);
        AddCol("severity", "严重度", 60);
        AddCol("object", "对象", 160);
        AddCol("actual", "实测", 90);
        AddCol("message", "说明", 320);
        AddCol("suggestions", "建议", 140);
        AddCol("placeholder", "占位", 60);

        // 后加的 Top 控件紧贴上一条；Fill 放最前保证不被挤掉
        Controls.Add(_grid);
        Controls.Add(_runsCombo);
        Controls.Add(_progress);
        Controls.Add(actions);
        Controls.Add(top);

        _poll = new System.Windows.Forms.Timer { Interval = 2000 };
        _poll.Tick += async (_, _) => await PollAsync();

        ReloadRuns();
    }

    private void AddCol(string key, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = header, DataPropertyName = key, Width = width, Name = key });

    // ---------------- 目录与 run 列表（run.json 只读，不走 review_status，避免把他人 run 判成 interrupted） ----------------

    private void Browse()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择审图目录（FILE-001：必须位于工作区根内）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_dirBox.Text.Trim()) ? _dirBox.Text.Trim() : "",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _dirBox.Text = dlg.SelectedPath;
    }

    private void ReloadRuns()
    {
        var prev = SelectedRunId();
        _runsCombo.Items.Clear();
        _runIds.Clear();
        try
        {
            if (Directory.Exists(_runsRoot))
                foreach (var dir in Directory.EnumerateDirectories(_runsRoot)
                             .OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    var file = Path.Combine(dir, "run.json");
                    if (!File.Exists(file)) continue;
                    JsonElement r;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        r = doc.RootElement.Clone();
                    }
                    catch (JsonException) { continue; } // 撕裂的 run.json 跳过
                    var mine = Path.GetFileName(dir) == _ownRunId ? "（本窗口发起）" : "";
                    _runIds.Add(Path.GetFileName(dir));
                    _runsCombo.Items.Add($"{Path.GetFileName(dir)}  {Str(r, "status")}  " +
                                         $"{Num(r, "done")}/{Num(r, "total")}{mine}");
                }
        }
        catch (Exception) { /* 探针式只读，永不因读盘崩 UI */ }
        if (_runsCombo.Items.Count == 0) return;
        var at = prev == null ? -1 : _runIds.IndexOf(prev);
        _runsCombo.SelectedIndex = Math.Max(0, at);
    }

    private string? SelectedRunId()
    {
        var at = _runsCombo.SelectedIndex;
        return at >= 0 && at < _runIds.Count ? _runIds[at] : null;
    }

    private static string Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    // ---------------- MCP 动作 ----------------

    private async Task StartReviewAsync(string? resume)
    {
        if (_busy) return;
        var dir = _dirBox.Text.Trim();
        if (dir.Length == 0) { Say("请先填写审图目录。"); return; }

        // 跨宿主防呆：Agent 的宿主有 running run 时，两边同时发起会抢同一个 NX 插件队列
        if (resume == null && _ownRunId == null && StatusProbe.Read().ReviewBusy)
            if (MessageBox.Show(FindForm(),
                    "检测到已有审图任务在跑（可能来自 Agent 的宿主进程）。再发起会共用同一个 NX 插件队列，" +
                    "同名部件互踩会导致个别文件记失败。仍要继续吗？",
                    "NX 小助手审图", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

        _busy = true;
        try
        {
            var args = new Dictionary<string, object?> { ["path"] = dir };
            if (resume != null) args["resume_run_id"] = resume;
            var p = await _client.CallToolAsync("review_folder", args);
            if (!IsOk(p)) { Say("发起失败：" + Err(p)); ReloadRuns(); return; }
            _ownRunId = Str(p, "run_id");
            Say($"run {_ownRunId} 已发起：{Str(p, "status")} {Num(p, "done")}/{Num(p, "total")}（后台串行逐张判定）");
            ReloadRuns();
            _poll.Start();
            await PollAsync();
        }
        catch (Exception ex) { Say("发起异常：" + ex.Message); }
        finally { _busy = false; }
    }

    private async Task CancelAsync()
    {
        var runId = SelectedRunId();
        if (runId == null) { Say("请先选中一个 run。"); return; }
        if (runId != _ownRunId)
        {
            Say("该 run 不是本窗口发起的——请到发起方（如 Agent 对话）取消。" +
                "托盘替他方在跑的 run 调 review_status 会被宿主判成 interrupted，反而毁掉它的实时状态。");
            return;
        }
        if (_busy) return;
        _busy = true;
        try
        {
            var p = await _client.CallToolAsync("review_status",
                new Dictionary<string, object?> { ["run_id"] = runId, ["cancel"] = true });
            Say(IsOk(p)
                ? $"已请求停止 run {runId}：当前这张审完后生效，报告只含已完成部分。状态：{Str(p, "status")}"
                : "取消请求失败：" + Err(p));
        }
        finally { _busy = false; }
    }

    private async Task PollAsync()
    {
        if (_ownRunId == null || _busy) return;
        _busy = true;
        try
        {
            var p = await _client.CallToolAsync("review_status",
                new Dictionary<string, object?> { ["run_id"] = _ownRunId });
            if (!IsOk(p))
            {
                _poll.Stop();
                Say($"轮询失败：{Err(p)}");
                return;
            }
            var status = Str(p, "status");
            var line = $"run {_ownRunId}　{status}　{Num(p, "done")}/{Num(p, "total")}";
            var current = Str(p, "current");
            if (current.Length > 0) line += $"　当前：{current}";
            if (Str(p, "error_message").Length > 0)
                line += $"　{Str(p, "error_code")}：{Str(p, "error_message")}";
            if (Str(p, "note").Length > 0) line += $"　备注：{Str(p, "note")}";
            if (p.TryGetProperty("failed_files", out var ff) && ff.ValueKind == JsonValueKind.Array &&
                ff.GetArrayLength() > 0) line += $"　失败文件 {ff.GetArrayLength()} 个";
            Say(line);

            if (status is "completed" or "completed_with_errors" or "failed" or "cancelled" or "interrupted")
            {
                _poll.Stop();
                _ownRunId = null; // 结束后转历史：明细/报告走只读路径即可
                if (status is "completed" or "completed_with_errors")
                {
                    await LoadFindingsCoreAsync(Str(p, "run_id"));
                    var rp = Str(p, "report_path");
                    if (rp.Length > 0) Say(line + $"　→ 报告已生成：{rp}");
                }
                ReloadRuns();
            }
        }
        catch (Exception ex)
        {
            _poll.Stop();
            Say("轮询异常：" + ex.Message);
        }
        finally { _busy = false; }
    }

    private async Task LoadFindingsIfFinished(string? runId)
    {
        if (runId == null) return;
        if (_poll.Enabled && runId == _ownRunId) return; // 自己还在跑：等轮询收尾自动拉
        await LoadFindingsAsync(runId);
    }

    private async Task LoadFindingsAsync(string? runId)
    {
        if (runId == null) { Say("请先选中一个 run。"); return; }
        if (_busy) return;
        _busy = true;
        try { await LoadFindingsCoreAsync(runId); }
        catch (Exception ex) { Say("拉取明细异常：" + ex.Message); }
        finally { _busy = false; }
    }

    private async Task LoadFindingsCoreAsync(string runId)
    {
        _grid.Rows.Clear();
        var offset = 0;
        var total = -1;
        while (true)
        {
            var p = await _client.CallToolAsync("review_findings", new Dictionary<string, object?>
                { ["run_id"] = runId, ["offset"] = offset, ["limit"] = 500 });
            if (!IsOk(p)) { Say("拉取明细失败：" + Err(p)); return; }
            total = Num(p, "total");
            if (p.TryGetProperty("findings", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var f in items.EnumerateArray()) AddFindingRow(f);
            var returned = Num(p, "returned");
            offset += returned;
            if (returned == 0 || offset >= total) break;
        }
        Say($"run {runId}：共 {total} 条 finding（表格已载入 {Math.Min(offset, Math.Max(total, 0))} 条）。" +
            "汇总统计见“打开审图报告”。");
    }

    private void AddFindingRow(JsonElement f)
    {
        var suggestions = "";
        if (f.TryGetProperty("suggestions", out var sg) && sg.ValueKind == JsonValueKind.Array)
            suggestions = string.Join("、", sg.EnumerateArray()
                .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()));
        var placeholder = f.TryGetProperty("placeholder", out var ph) &&
                          ph.ValueKind == JsonValueKind.True ? "待校准" : "";
        _grid.Rows.Add(
            Str(f, "drawing_no"), Str(f, "version"),
            Path.GetFileName(Str(f, "file")), Str(f, "rule_id"), Str(f, "name"),
            Str(f, "enforcement") == "block" ? "阻断" : "警告",
            Str(f, "severity"), Str(f, "object"), Str(f, "actual"), Str(f, "message"),
            suggestions, placeholder);
    }

    private void OpenReport()
    {
        var runId = SelectedRunId();
        if (runId == null) { Say("请先选中一个 run。"); return; }
        try
        {
            var file = Path.Combine(_runsRoot, runId, "审图报告.xlsx");
            if (!File.Exists(file))
            {
                Say($"报告尚未生成：{file}（run 结束后才有）");
                return;
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) { Say("打开失败：" + ex.Message); }
    }

    private void Say(string text) => _progress.Text = text;

    private static bool IsOk(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    private static string Err(JsonElement p) =>
        Str(p, "error").Length > 0 ? Str(p, "error") : Str(p, "error_message");
}
