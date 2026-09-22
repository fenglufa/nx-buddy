using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using NxAssistant.Core;
using NxAssistant.Rules;

namespace NxAssistant.Tray;

/// <summary>
/// 托盘"规则管理"分页（第十三片）：基准包只读展示 + 用户态覆盖层编辑。
/// 勾选=禁用/启用规则；"改参数"=对该规则的数据文件做整文件替换；两者都写入
/// rules_state.json 并留时间戳日志（审图报告要能回答"这条规则是谁关的"）。
/// 宿主守卫按文件 mtime 热加载——保存后下一次受守卫写/审图即生效，无需重启。
/// </summary>
internal sealed class RulesTab : UserControl
{
    private readonly string _statePath;
    private readonly string _packDir;
    private readonly RulePack _base;          // 无状态：完整规则清单（UI 要显示被禁用项）
    private readonly RulesState _state;
    private readonly DataGridView _grid;
    private readonly Label _status;
    private readonly TextBox _logBox;
    private bool _dirty;

    private const string ColEnabled = "enabled";
    private const string ColEdit = "edit";

    public RulesTab(TrayStatus s)
    {
        _statePath = NxaSettings.Resolve("NXA_RULES_STATE", "rules_state_path", RulesState.DefaultPath);
        Dock = DockStyle.Fill;

        if (!s.RulesPackFound || !File.Exists(Path.Combine(s.RulesDir, "pack.json")))
        {
            Controls.Add(new Label
            {
                Text = "未找到规则包目录（宿主当前也处于 fail-closed：拒绝一切受守卫写）。\n" +
                       "请在\"规则\"分页修正规则包目录后重新打开本窗口。",
                AutoSize = true, Location = new Point(10, 10),
            });
            return;
        }

        _packDir = s.RulesDir;
        _base = RulePack.Load(_packDir);
        _state = RulesState.Load(_statePath);

        _grid = new DataGridView
        {
            Dock = DockStyle.Top,
            Height = 300,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoGenerateColumns = false,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        };
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", DataPropertyName = ColEnabled, Width = 44 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "编号", DataPropertyName = "id", Width = 130, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "组", DataPropertyName = "group", Width = 60, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "名称", DataPropertyName = "name", Width = 240, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "级别", DataPropertyName = "sev", Width = 50, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "用户改动", DataPropertyName = "user", Width = 120, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewButtonColumn { HeaderText = "参数", Text = "改参数", UseColumnTextForButtonValue = true, DataPropertyName = ColEdit, Width = 70 });
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellClick += OnCellClick;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40 };
        var save = new Button { Text = "保存覆盖层", AutoSize = true };
        save.Click += (_, _) => Save();
        var reset = new Button { Text = "清空全部改动", AutoSize = true };
        reset.Click += (_, _) => ResetAll();
        buttons.Controls.AddRange(new Control[] { save, reset });

        _status = new Label { Dock = DockStyle.Top, Height = 22, Text = StateSummary() };
        var logTitle = new Label { Dock = DockStyle.Top, Height = 20, Text = "变更日志（最近 15 条）：" };
        _logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5f),
            Text = LogText(),
        };

        Controls.Add(_logBox);
        Controls.Add(logTitle);
        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(_grid);
        Bind();
    }

    // ---------- 数据绑定 ----------

    private sealed class Row
    {
        public bool enabled { get; set; }
        public string id { get; set; } = "";
        public string group { get; set; } = "";
        public string name { get; set; } = "";
        public string sev { get; set; } = "";
        public string user { get; set; } = "";
    }

    private void Bind()
    {
        _grid.DataSource = _base.Rules.Select(r => new Row
        {
            enabled = !_state.IsDisabled(r.Id),
            id = r.Id,
            group = r.Group,
            name = r.Name + (HasPlaceholder(r) ? "（含占位数据）" : ""),
            sev = r.Severity == "fail" ? "阻断" : "警告",
            user = UserMark(r),
        }).ToList();
        _status.Text = StateSummary();
        _logBox.Text = LogText();
    }

    private string UserMark(RuleDef r)
    {
        var marks = new List<string>();
        if (_state.IsDisabled(r.Id)) marks.Add("已禁用");
        foreach (var f in RuleDataFiles.For(r.Id))
            if (_state.HasDataOverride(_base.RelPathFor(r, f))) marks.Add("已改参数");
        return string.Join("、", marks);
    }

    private bool HasPlaceholder(RuleDef r) =>
        RuleDataFiles.For(r.Id).Any(f =>
        {
            try
            {
                var d = CurrentFile(r, f);
                return d.TryGetProperty("placeholder", out var p) && p.ValueKind == JsonValueKind.True;
            }
            catch { return false; }
        });

    /// <summary>该规则某数据文件的当前生效内容（用户覆盖 > 基准包）。</summary>
    private JsonElement CurrentFile(RuleDef r, string fileBaseName)
    {
        var rel = _base.RelPathFor(r, fileBaseName);
        if (_state.TryDataOverride(rel, out var ov)) return ov;
        return _base.Data(r, fileBaseName);
    }

    private string StateSummary() =>
        $"覆盖层：{_statePath}　禁用 {_state.Disabled.Count} 条　参数替换 {_state.Data.Count} 处" +
        (_dirty ? "　【未保存】" : "");

    private string LogText() => string.Join("\n",
        _state.Log.AsEnumerable().Reverse().Take(15)
            .Select(e => $"{e.T}  {e.Action}  {e.Detail}"));

    // ---------- 交互 ----------

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != ColEnabled) return;
        var row = _grid.Rows[e.RowIndex].DataBoundItem as Row;
        if (row == null) return;
        bool nowEnabled = (bool)(_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].EditedFormattedValue ?? true);
        _state.SetDisabled(row.id, row.name, disabled: !nowEnabled);
        _dirty = true;
        Bind();
    }

    private void OnCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        if (_grid.Columns[e.ColumnIndex].Name != ColEdit) return;
        var row = _grid.Rows[e.RowIndex].DataBoundItem as Row;
        var def = row == null ? null : _base.Rules.FirstOrDefault(r => r.Id == row.id);
        if (def == null) return;
        if (RuleDataFiles.For(def.Id).Count == 0)
        {
            MessageBox.Show("该规则无独立数据参数（判定逻辑内建），只能整体禁用/启用。", "NX 小助手");
            return;
        }
        using var dlg = new RuleParamsDialog(_base, _state, def);
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _dirty = true;
            Bind();
        }
    }

    private void Save()
    {
        try
        {
            _state.Save(_statePath);
            _dirty = false;
            Bind();
            MessageBox.Show("已保存。宿主按下一次受守卫写/审图时自动读取新规则态（无需重启）。",
                "NX 小助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "NX 小助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetAll()
    {
        if (MessageBox.Show("清空所有禁用与参数改动、回落基准包？（日志保留）", "NX 小助手",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _state.Disabled.Clear();
        _state.Data.Clear();
        _state.AddLog("reset_all", "清空全部用户改动");
        _dirty = true;
        Bind();
    }
}

/// <summary>
/// 参数编辑对话框：对该规则涉及的每个数据文件，编辑其中"数字/字符串数组"属性
/// （其余属性原样保留，对象/表结构暂不支持 GUI——写日志时说明）。
/// 保存 = 生成整文件替换写入覆盖层；"恢复默认" = 撤销该文件的替换。
/// </summary>
internal sealed class RuleParamsDialog : Form
{
    private readonly RulePack _base;
    private readonly RulesState _state;
    private readonly RuleDef _rule;
    // 文件 → (属性 → 编辑框)；以及文件的当前完整 JSON 字符串
    private readonly Dictionary<string, Dictionary<string, TextBox>> _boxes = new();
    private readonly Dictionary<string, JsonNode> _docs = new();

    public RuleParamsDialog(RulePack pack, RulesState state, RuleDef rule)
    {
        _base = pack;
        _state = state;
        _rule = rule;
        Text = $"参数 · {rule.Id}（{rule.Name}）";
        Width = 620;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        foreach (var fileBase in RuleDataFiles.For(rule.Id))
        {
            JsonElement data;
            try { data = EffectiveFile(fileBase); }
            catch (Exception ex)
            {
                tabs.TabPages.Add(new TabPage(fileBase)
                {
                    Controls = { new Label { Text = "数据文件不可读：" + ex.Message, AutoSize = true, Padding = new Padding(10) } },
                });
                continue;
            }
            var page = new TabPage(fileBase + (_state.HasDataOverride(Rel(fileBase)) ? "（已改动）" : ""))
            { Padding = new Padding(10) };
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var boxMap = new Dictionary<string, TextBox>();
            var doc = JsonNode.Parse(data.GetRawText())!.AsObject();
            _docs[fileBase] = doc;
            int row = 0;
            foreach (var prop in doc)
            {
                var v = prop.Value;
                if (v is JsonArray arr && arr.All(x => x is JsonValue jv &&
                    (jv.GetValueKind() == JsonValueKind.Number || jv.GetValueKind() == JsonValueKind.String)))
                {
                    var vals = arr.Select(x => x!.GetValue<JsonElement>().ToString()).ToList();
                    panel.RowCount++;
                    panel.Controls.Add(new Label
                    {
                        Text = prop.Key,
                        AutoSize = true,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                    }, 0, row);
                    var box = new TextBox
                    {
                        Dock = DockStyle.Fill,
                        Text = string.Join(", ", vals),
                    };
                    panel.Controls.Add(box, 1, row);
                    boxMap[prop.Key] = box;
                    row++;
                }
                else
                {
                    panel.RowCount++;
                    panel.Controls.Add(new Label { Text = prop.Key, AutoSize = true, Dock = DockStyle.Fill }, 0, row);
                    panel.Controls.Add(new Label
                    {
                        Text = v is JsonArray ? "（混合类型数组，GUI 暂不编辑）" : "（非系列表，GUI 暂不编辑，原样保留）",
                        AutoSize = true,
                        ForeColor = Color.Gray,
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleLeft,
                    }, 1, row);
                    row++;
                }
            }
            var resetBtn = new Button { Text = "恢复此文件为基准包默认", AutoSize = true };
            var captured = fileBase;
            resetBtn.Click += (_, _) =>
            {
                _state.ClearDataOverride(Rel(captured), $"{rule.Id} 恢复 {captured} 默认");
                DialogResult = DialogResult.OK;
                Close();
            };
            page.Controls.Add(panel);
            page.Controls.Add(resetBtn);
            resetBtn.BringToFront();
            _boxes[fileBase] = boxMap;
            tabs.TabPages.Add(page);
        }

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.None };
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        bottom.Controls.Add(ok);
        bottom.Controls.Add(cancel);
        Controls.Add(tabs);
        Controls.Add(bottom);
        CancelButton = cancel;
    }

    private string Rel(string fileBase) => _base.RelPathFor(_rule, fileBase);

    private JsonElement EffectiveFile(string fileBase)
    {
        var rel = Rel(fileBase);
        if (_state.TryDataOverride(rel, out var ov)) return ov;
        return _base.Data(_rule, fileBase);
    }

    private void Apply()
    {
        foreach (var (fileBase, boxMap) in _boxes)
        {
            if (!_docs.TryGetValue(fileBase, out var doc)) continue;
            var changed = new List<string>();
            foreach (var (prop, box) in boxMap)
            {
                var text = box.Text.Trim();
                var parts = text.Length == 0
                    ? Array.Empty<string>()
                    : text.Split(new[] { ',', '，', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var arr = new JsonArray();
                var orig = doc[prop]!.AsArray();
                bool numeric = orig.Count > 0 && orig[0]!.GetValue<JsonElement>().ValueKind == JsonValueKind.Number;
                try
                {
                    foreach (var p in parts)
                    {
                        if (numeric) arr.Add(double.Parse(p, System.Globalization.CultureInfo.InvariantCulture));
                        else arr.Add(p);
                    }
                }
                catch
                {
                    MessageBox.Show($"属性 {prop} 需要逗号分隔的数字列表（该系列为数值型）。",
                        "NX 小助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                doc[prop] = arr;
                changed.Add(prop);
            }
            if (changed.Count > 0)
            {
                var replacement = JsonSerializer.Deserialize<JsonElement>(doc.ToJsonString());
                _state.SetDataOverride(Rel(fileBase),
                    $"{_rule.Id} {Rel(fileBase)}：改 {string.Join("/", changed)}", replacement);
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
