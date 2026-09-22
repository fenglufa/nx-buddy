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

    public TrayAppContext()
    {
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "NX 小助手",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _timer = new System.Windows.Forms.Timer { Interval = 3000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("状态（只读）", null, (s, e) => { }) { Tag = "status" });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("导入授权 Key…", null, (_, _) => ImportKey()));
        menu.Items.Add(new ToolStripMenuItem("复制 MCP 配置片段", null, (_, _) => CopySnippet()));
        menu.Items.Add(new ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("打开插件日志", null, (_, _) => OpenPath(_status.PluginLogPath, true)));
        menu.Items.Add(new ToolStripMenuItem("打开审图报告目录", null, (_, _) => OpenPath(_status.RunsRoot, false)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));
        return menu;
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

    private void ImportKey()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择授权 Key（.lic）",
            Filter = "NX 小助手 Key (*.lic)|*.lic|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        try
        {
            var target = StatusProbe.Read().LicensePath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dlg.FileName, target, true);
            var after = StatusProbe.Read();
            MessageBox.Show(
                after.Licensed
                    ? $"Key 已导入并激活：{after.Customer}，有效至 {after.NotAfter}（剩 {after.DaysLeft} 天）。"
                    : $"Key 已复制到 {target}，但校验未通过：{after.LicenseReason}",
                "NX 小助手授权",
                MessageBoxButtons.OK,
                after.Licensed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("导入失败：" + ex.Message, "NX 小助手授权",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CopySnippet()
    {
        try
        {
            Clipboard.SetText(StatusProbe.McpConfigSnippet(_status));
            balloon("MCP 配置片段已复制到剪贴板（command 指向 " + _status.McpExe + "）。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("复制失败：" + ex.Message);
        }
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

    private static void OpenPath(string path, bool isFile)
    {
        try
        {
            if (isFile && !File.Exists(path)) { MessageBox.Show("文件不存在：" + path); return; }
            if (!isFile && !Directory.Exists(path)) Directory.CreateDirectory(path);
            var psi = isFile
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
