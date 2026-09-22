using System;
using System.Drawing;
using System.Windows.Forms;

namespace NxAssistant.Tray;

/// <summary>
/// 托盘左键主页（第十四片，形态 A：纯 WinForms，不起任何 HTTP 端口）：
/// 概览（四态+授权详情+常用动作按钮）、审图工作台、规则管理三页收进一个窗口；
/// 关闭=隐藏（审图轮询继续跑），真正退出只走托盘菜单"退出"（同时带走自拉的宿主进程）。
/// </summary>
internal sealed class MainWindow : Form
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Label _overview;
    private readonly TabControl _tabs;
    private readonly McpHostClient _hostClient;
    private TrayStatus _status;
    private bool _realClose;

    public MainWindow(NotifyIcon icon)
    {
        _icon = icon;
        _status = StatusProbe.Read();
        Text = "NX 小助手";
        Width = 980;
        Height = 680;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _tabs = new TabControl { Dock = DockStyle.Fill };

        var home = new TabPage("状态与操作") { Padding = new Padding(14) };
        _overview = new Label { Dock = DockStyle.Top, Height = 300, Text = OverviewText(_status) };
        var ops = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 4, 0, 0) };
        ops.Controls.Add(OpButton("导入授权 Key…", () => TrayActions.ImportKey(this, RefreshNow)));
        ops.Controls.Add(OpButton("复制 MCP 配置片段", () => TrayActions.CopyMcpConfig(this, _status)));
        ops.Controls.Add(OpButton("打开插件日志", () => TrayActions.OpenPath(this, _status.PluginLogPath, true)));
        ops.Controls.Add(OpButton("打开审图报告目录", () => TrayActions.OpenPath(this, _status.RunsRoot, false)));
        ops.Controls.Add(OpButton("路径设置…", OpenSettings));
        var homeBody = new Panel { Dock = DockStyle.Fill };
        homeBody.Controls.Add(ops);
        homeBody.Controls.Add(_overview);
        home.Controls.Add(homeBody);

        _hostClient = new McpHostClient(_status.McpExe);

        var review = new TabPage("审图工作台") { Padding = new Padding(6) };
        review.Controls.Add(new ReviewTab(_status, _hostClient) { Dock = DockStyle.Fill });

        var rules = new TabPage("规则管理") { Padding = new Padding(6) };
        rules.Controls.Add(new RulesTab(_status) { Dock = DockStyle.Fill });

        _tabs.TabPages.Add(home);
        _tabs.TabPages.Add(review);
        _tabs.TabPages.Add(rules);
        Controls.Add(_tabs);

        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => RefreshNow();
        _timer.Start();
    }

    private Button OpButton(string text, Action act)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => act();
        return b;
    }

    private void RefreshNow()
    {
        _status = StatusProbe.Read();
        _overview.Text = OverviewText(_status);
    }

    private void OpenSettings()
    {
        using var f = new SettingsForm(_status);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            RefreshNow();
            MessageBox.Show(this, "设置已保存到 " + _status.SettingsPath, "NX 小助手");
        }
    }

    private static string OverviewText(TrayStatus s)
    {
        var lic = s.Licensed
            ? $"已授权（剩 {s.DaysLeft} 天，有效至 {s.NotAfter}）　Key：{s.LicId}　客户：{s.Customer}"
            : $"未授权：{s.LicenseReason}（状态码 {s.LicenseState}）";
        return
            TrayAppContext.SummaryLine(s) + "\n\n" +
            $"授权：{lic}\n" +
            $"授权文件：{s.LicensePath}\n" +
            $"机器指纹：{s.MachineHash}\n\n" +
            $"NX 插件：命名管道 \\\\.\\pipe\\{NxAssistant.Core.NxIpc.PipeName}　" +
            (s.PluginOnline ? "在线（NX 已加载插件）" : "不可见（NX 未启动或未加载插件）") + "\n" +
            $"MCP 宿主：{s.McpExe}　" + (s.McpRunning ? "有宿主进程在跑" : "无") +
            "（审图工作台会另拉一个属于托盘的宿主，与 Agent 各用各的，互不干扰）\n" +
            $"审图：{(s.ReviewBusy ? "有 running 态 run" : "空闲")}　runs 目录：{s.RunsRoot}\n\n" +
            $"规则包：{s.RulesDir}（pack.json {(s.RulesPackFound ? "已找到" : "未找到——宿主 fail-closed")}）\n" +
            $"用户覆盖层：{s.RulesStatePath}　禁用 {s.RulesDisabledCount} 条　改参 {s.RulesOverridesCount} 处\n" +
            (string.IsNullOrEmpty(s.RulesStateLatest) ? "" : $"最近一次规则改动：{s.RulesStateLatest}\n") +
            "\n工作区（审图目录须在其内）：" + s.WorkspaceRoot +
            "　共享配置：" + s.SettingsPath;
    }

    /// <summary>关闭=隐藏：审图 run 与宿主进程都还活着，从托盘左键可回到同一窗口。</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_realClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _icon.ShowBalloonTip(2500, "NX 小助手", "已最小回托盘：左键图标可再次打开；“退出”才会终止托盘与其宿主进程。",
                ToolTipIcon.Info);
            return;
        }
        _timer.Dispose();
        _hostClient.Dispose(); // 托盘宿主退出；未完成的 run 下次查询会判 interrupted（可续跑）
        base.OnFormClosing(e);
    }

    /// <summary>托盘"退出"路径：不拦 Close，真关（顺带 dispose 宿主）。</summary>
    public void RealClose()
    {
        _realClose = true;
        Close();
    }
}
