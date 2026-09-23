using System;
using Microsoft.Win32;

namespace NxAssistant.Tray;

/// <summary>
/// 开机自启 = HKCU Run 值（与安装器写的是同一个值名 NXAssistant，用户级、免管理员）。
/// 主页勾选框直接读写它：开启时写入当前运行的托盘 exe 路径，升级/换目录后勾一次即自动纠正。
/// </summary>
internal static class AutoStart
{
    public const string ValueName = "NXAssistant";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return run?.GetValue(ValueName) is string s && s.Trim().Length > 0;
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定托盘 exe 路径。");
        using var run = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 HKCU Run 键。");
        run.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开 HKCU Run 键。");
        run.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
