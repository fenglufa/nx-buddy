using NxAssistant.Core;

namespace NxAssistant.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 无头模式：托盘逻辑与 UI 解耦，供脚本冒烟与技术支持一键采集状态。
        if (args.Contains("--status-json"))
        {
            Console.Out.Write(StatusProbe.ToJson(StatusProbe.Read()));
            return 0;
        }
        if (args.Contains("--review-probe"))
            return ReviewProbe.RunAsync().GetAwaiter().GetResult();
        if (args.Contains("--ui-probe"))
            return UiProbe.Run();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var app = new TrayAppContext();
        Application.Run(app);
        return 0;
    }
}

/// <summary>
/// 托盘常驻（PRD §7.1）：状态区分未授权/已授权、NX 未连接/已连接、MCP 空闲/忙碌；
/// 右键提供导入 Key、复制 mcp 配置片段、设置分页、打开日志、退出。不提供建模（那是 NX 内的事）。
/// 状态每 3 秒刷新一次——探针全程只读（LicenseManager.Check + 管道命名空间枚举 + 进程表），绝不下 IPC 请求。
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private TrayStatus _status = StatusProbe.Read();
    private MainWindow? _main;

    public TrayAppContext()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "NX 小助手",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        // 左键单击=打开主页（用户反馈"只能右键体验不好"）；右键仍是快捷菜单
        _icon.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMain(); };
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
        // 启动即展示主页（用户反馈：只在托盘冒个图标不知道装没装上）；关主页=隐藏回托盘。
        ShowMain();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("状态（只读）", null, (s, e) => { }) { Tag = "status" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("主页…（审图工作台 / 规则管理）", null, (_, _) => ShowMain()));
        menu.Items.Add(new ToolStripMenuItem("导入授权 Key…", null, (_, _) => TrayActions.ImportKey(_icon.ContextMenuStrip!)));
        menu.Items.Add(new ToolStripMenuItem("复制 MCP 配置片段", null, (_, _) => TrayActions.CopyMcpConfig(_icon.ContextMenuStrip!, _status)));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("打开插件日志", null, (_, _) => TrayActions.OpenPath(_icon.ContextMenuStrip!, _status.PluginLogPath, true)));
        menu.Items.Add(new ToolStripMenuItem("打开审图报告目录", null, (_, _) => TrayActions.OpenPath(_icon.ContextMenuStrip!, _status.RunsRoot, false)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        return menu;
    }

    private void ShowMain()
    {
        try
        {
            if (_main is { IsDisposed: false })
            {
                if (!_main.Visible) _main.Show();
                if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
                _main.Activate();
                return;
            }
            _main = new MainWindow(_icon);
            _main.FormClosed += (_, _) => _main = null;
            _main.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开主页失败：" + ex.Message, "NX 小助手",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExitApp()
    {
        _main?.RealClose();
        ExitThread();
    }

    private void Refresh()
    {
        _status = StatusProbe.Read();
        var icon = _icon.ContextMenuStrip;
        if (icon != null && icon.Items.Count > 0 && icon.Items[0].Tag is string)
            icon.Items[0].Text = SummaryLine(_status);
        _icon.Text = "NX 小助手 · " + (_status.Licensed ? "已授权" : "未授权")
                     + " · NX " + (_status.PluginOnline ? "已连接" : "未连接");
    }

    public static string SummaryLine(TrayStatus s)
    {
        var lic = s.Licensed ? $"已授权（剩 {s.DaysLeft} 天）" : $"未授权：{s.LicenseReason}";
        var nx = s.PluginOnline ? "NX 已连接" : "NX 未连接";
        var mcp = s.ReviewBusy ? "MCP 忙碌（审图中）" : s.McpRunning ? "MCP 在线" : "MCP 未运行";
        return $"{lic} · {nx} · {mcp}";
    }

    private void OpenSettings()
    {
        using var f = new SettingsForm(_status);
        if (f.ShowDialog() == DialogResult.OK)
        {
            Refresh();
            balloon("设置已保存到 " + _status.SettingsPath);
        }
    }

    private void balloon(string text) =>
        _icon.ShowBalloonTip(4000, "NX 小助手", text, ToolTipIcon.Info);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _main?.RealClose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
