using System;
using System.IO;

namespace NxAssistant.NxPlugin;

/// <summary>插件侧日志：追加到 %TEMP%\nxa_plugin.log，与 NX-MCP 的桥日志同处，便于分层排查。</summary>
internal static class NxLog
{
    private static readonly object Sync = new object();

    public static string LogPath =>
        Path.Combine(Path.GetTempPath(), "nxa_plugin.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
        }
        catch { /* 日志失败不得影响主流程 */ }
    }
}
