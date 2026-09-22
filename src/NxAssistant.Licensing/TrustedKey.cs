using System.Reflection;
using System.Security.Cryptography;

namespace NxAssistant.Licensing;

/// <summary>
/// 随产品分发的信任公钥（内嵌资源 Keys/trusted_public.pem）。
/// 私钥只在产品方 KeyGen 工具手中，永不进入客户安装包（PRD §8.3）。
/// </summary>
public static class TrustedKey
{
    public const string ResourceName = "NxAssistant.Licensing.Keys.trusted_public.pem";

    public static string PublicPem()
    {
        var asm = typeof(TrustedKey).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺少内嵌公钥资源 {ResourceName}，请先用 KeyGen genkey 生成并编译。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static ECDsa LoadPublic()
    {
        var ec = ECDsa.Create();
        ec.ImportFromPem(PublicPem());
        return ec;
    }
}

/// <summary>机器指纹：MachineName + 系统 MachineGuid 的 SHA-256 前 16 位十六进制。</summary>
public static class MachineFingerprint
{
    public static string Current()
    {
        var guid = OperatingSystem.IsWindows() ? ReadMachineGuid() : "no-guid";
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "|" + guid));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadMachineGuid()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString() ?? "no-guid";
    }
}
