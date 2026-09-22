using NxAssistant.Core;

namespace NxAssistant.Tray;

/// <summary>
/// 设置窗（PRD §7.1 分页：连接、授权、规则、工作区与报告路径、日志）。
/// 可编辑项只有四类路径（workspace/rules_dir/license_path/mcp_exe），写入 settings.json；
/// 解析优先级"环境变量 > settings.json > 默认"在表单里明示，避免用户设了 env 又来改文件。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly TextBox _workspace = NewPathBox();
    private readonly TextBox _rules = NewPathBox();
    private readonly TextBox _license = NewPathBox();
    private readonly TextBox _mcpExe = NewPathBox();

    public SettingsForm(TrayStatus s)
    {
        Text = "NX 小助手 · 设置";
        Width = 640;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var conn = TabsPage("连接", ConnectionText(s));
        AddPathRow(conn, "宿主 exe：", _mcpExe, s.McpExe, "供“复制 MCP 配置片段”与状态探测使用");
        var lic = TabsPage("授权", LicenseText(s));
        AddPathRow(lic, "授权文件：", _license, s.LicensePath, "导入 Key 也会写到这个位置");
        var rules = TabsPage("规则", "环境变量 NXA_RULES_DIR > settings.json[rules_dir] > 宿主同目录 company_v3\n\n");
        AddPathRow(rules, "规则包目录：", _rules, s.RulesDir, $"当前解析：{s.RulesDir}（pack.json {(s.RulesPackFound ? "已找到" : "未找到")}）");
        var ws = TabsPage("工作区与报告", "环境变量 NXA_WORKSPACE > settings.json[workspace] > %LOCALAPPDATA%\\NXAssistant\\workspace\n\n");
        AddPathRow(ws, "工作区：", _workspace, s.WorkspaceRoot, $"审图 runs：{s.RunsRoot}");
        var log = TabsPage("日志",
            $"插件日志：{s.PluginLogPath}\n共享配置：{s.SettingsPath}\n\n" +
            "托盘状态每 3 秒只读刷新（授权文件 + 命名管道命名空间 + 进程表），不经 IPC 打扰 NX。");
        log.Text = "日志";

        // TabPage.Text 在 Add 之后仍可直接赋值；上面 TabsPage 已按顺序挂好。
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "保存", DialogResult = DialogResult.None };
        save.Click += (_, _) => Save();
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(close);

        Controls.Add(tabs);
        Controls.Add(buttons);
        CancelButton = close;

        void TabsPageAdd(TabPage p) => tabs.TabPages.Add(p);
        TabPage TabsPage(string title, string info)
        {
            var p = new TabPage(title) { Padding = new Padding(14) };
            var l = new Label { Text = info, AutoSize = false, Dock = DockStyle.Top, Height = 70 };
            p.Controls.Add(l);
            TabsPageAdd(p);
            return p;
        }
    }

    private static TextBox NewPathBox() => new() { Width = 430, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };

    private static void AddPathRow(TabPage page, string label, TextBox box, string value, string note)
    {
        box.Text = value;
        var rowY = 90 + page.Controls.Count * 40;
        page.Controls.Add(new Label { Text = label, Location = new Point(14, rowY + 4), AutoSize = true });
        box.Location = new Point(120, rowY);
        page.Controls.Add(box);
        page.Controls.Add(new Label { Text = note, Location = new Point(120, rowY + 26), AutoSize = true });
    }

    private void Save()
    {
        try
        {
            NxaSettings.Set("workspace", NullIfBlank(_workspace.Text));
            NxaSettings.Set("rules_dir", NullIfBlank(_rules.Text));
            NxaSettings.Set("license_path", NullIfBlank(_license.Text));
            NxaSettings.Set("mcp_exe", NullIfBlank(_mcpExe.Text));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show("保存失败：" + ex.Message, "NX 小助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string ConnectionText(TrayStatus s) =>
        $"命名管道：\\\\.\\pipe\\{NxIpc.PipeName}　{(s.PluginOnline ? "在线（NX 插件已加载）" : "不可见（NX 未启动或未加载插件）")}\n" +
        $"MCP 宿主：{s.McpExe}　{(s.McpRunning ? "进程在跑" : "未在跑")}\n" +
        $"令牌：环境变量 {NxIpc.TokenEnvVar}（非空时 IPC 必须带 token）\n\n" +
        "Agent 客户端把 mcpServers.command 指向宿主 exe 即可；托盘与宿主是分离进程（PRD §6：禁止同进程既弹窗又占 stdio）。";

    private static string LicenseText(TrayStatus s) =>
        TrayAppContext.SummaryLine(s) + "\n" +
        $"状态码：{s.LicenseState}\n" +
        $"Key：{(string.IsNullOrEmpty(s.LicId) ? "—" : s.LicId)}　客户：{(string.IsNullOrEmpty(s.Customer) ? "—" : s.Customer)}　有效至：{(string.IsNullOrEmpty(s.NotAfter) ? "—" : s.NotAfter)}\n" +
        $"机器指纹：{s.MachineHash}\n" +
        $"授权文件：{s.LicensePath}\n\n" +
        "换机/过期请联系产品方重签（离线 Key，一 Key 一机，首次激活锁机）。";
}
