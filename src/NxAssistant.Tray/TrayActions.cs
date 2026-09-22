using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace NxAssistant.Tray;

/// <summary>托盘动作的无窗体归属实现：右键菜单与主页共用同一份逻辑（第十四片）。</summary>
internal static class TrayActions
{
    public static string ImportKey(IWin32Window owner, Action? afterImport = null)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择授权 Key（.lic）",
            Filter = "NX 小助手 Key (*.lic)|*.lic|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return "";
        try
        {
            var target = StatusProbe.Read().LicensePath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(dlg.FileName, target, true);
            var after = StatusProbe.Read();
            MessageBox.Show(owner,
                after.Licensed
                    ? $"Key 已导入并激活：{after.Customer}，有效至 {after.NotAfter}（剩 {after.DaysLeft} 天）。"
                    : $"Key 已复制到 {target}，但校验未通过：{after.LicenseReason}",
                "NX 小助手授权", MessageBoxButtons.OK,
                after.Licensed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            afterImport?.Invoke();
            return target;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "导入失败：" + ex.Message, "NX 小助手授权",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return "";
        }
    }

    public static void CopyMcpConfig(IWin32Window owner, TrayStatus s)
    {
        try
        {
            Clipboard.SetText(StatusProbe.McpConfigSnippet(s));
            MessageBox.Show(owner,
                "MCP 配置片段已复制到剪贴板（command 指向 " + s.McpExe + "）。",
                "NX 小助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "复制失败：" + ex.Message);
        }
    }

    public static void OpenPath(IWin32Window owner, string path, bool isFile)
    {
        try
        {
            if (isFile && !File.Exists(path)) { MessageBox.Show(owner, "文件不存在：" + path); return; }
            if (!isFile && !Directory.Exists(path)) Directory.CreateDirectory(path);
            var psi = isFile
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo("explorer.exe", $"\"{path}\"");
            Process.Start(psi);
        }
        catch (Exception ex) { MessageBox.Show(owner, "打开失败：" + ex.Message); }
    }
}
