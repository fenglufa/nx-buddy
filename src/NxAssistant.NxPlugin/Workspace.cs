using System;
using System.IO;
using System.Linq;

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

    /// <summary>镜像 _workspace_exchange_path：交换格式导出文件（如 .stp）解析到工作区；
    /// 已存在且 overwrite=false 时报错。</summary>
    public static string ResolveExchange(string fileName, string[] allowedExtensions, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("file_name must be a non-empty string");
        fileName = fileName.Trim();
        if (Path.GetFileName(fileName) != fileName || fileName is "." or "..")
            throw new ArgumentException("file_name must be a plain file name, not a path");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
            throw new ArgumentException(
                "file_name extension must be one of: " + string.Join(", ", allowedExtensions.OrderBy(x => x)));
        var root = Root;
        Directory.CreateDirectory(root);
        var full = Path.GetFullPath(Path.Combine(root, fileName));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("exchange path escapes workspace root (FILE-001)");
        if (!overwrite && File.Exists(full))
            throw new IOException("exchange file already exists: " + full);
        return full;
    }

    /// <summary>审图批处理用：把宿主传来的 .prt 路径（绝对或工作区相对）钉在根目录内并要求存在（FILE-001 纵深防御）。</summary>
    public static string ResolveExisting(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("file_path must be a non-empty string");
        var root = Root;
        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path.Trim()));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("file path escapes workspace root (FILE-001)");
        if (!File.Exists(full))
            throw new FileNotFoundException("part file does not exist: " + full);
        return full;
    }
}
