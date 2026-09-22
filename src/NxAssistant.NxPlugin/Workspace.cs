using System;
using System.IO;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 文件沙箱（规则 FILE-001）：新建/导出只允许落在工作区根下。
/// 根目录：环境变量 NXA_WORKSPACE，缺省 %LOCALAPPDATA%\NXAssistant\workspace。
/// </summary>
internal static class Workspace
{
    public static string Root
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("NXA_WORKSPACE");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NXAssistant", "workspace");
        }
    }

    /// <summary>把"纯文件名"解析到工作区内，拒绝路径逃逸；existsFail=true 时已存在则报错。</summary>
    public static string Resolve(string fileName, bool existsFail)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("file_name 不能为空");
        fileName = fileName.Trim();
        if (Path.GetFileName(fileName) != fileName || fileName is "." or "..")
            throw new ArgumentException("file_name 必须是纯文件名，不允许携带路径");
        if (!fileName.EndsWith(".prt", StringComparison.OrdinalIgnoreCase))
            fileName += ".prt";
        var root = Root;
        Directory.CreateDirectory(root);
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("路径逃逸出工作区根目录（FILE-001）");
        if (existsFail && File.Exists(full))
            throw new IOException("文件已存在: " + full);
        return full;
    }
}
