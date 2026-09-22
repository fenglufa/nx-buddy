using System.Security.Cryptography;
using System.Text;

namespace NxAssistant.Licensing;

/// <summary>
/// .lic 文件格式：3 行文本。
///   nxa-license v1
///   payload &lt;base64(规范化 JSON)&gt;
///   signature &lt;base64(ECDSA-P256-SHA256, IEEE P1363)&gt;
/// </summary>
public static class LicenseFormat
{
    public const string Header = "nxa-license v1";

    public static string Sign(LicensePayload payload, ECDsa privateKey)
    {
        var bytes = payload.CanonicalJson();
        var sig = privateKey.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return string.Join('\n',
            Header,
            "payload " + Convert.ToBase64String(bytes),
            "signature " + Convert.ToBase64String(sig)) + "\n";
    }

    /// <summary>解析文件并验签。返回 false 时 message 说明原因。</summary>
    public static bool TryRead(string fileText, ECDsa publicKey, out LicensePayload payload, out string message)
    {
        payload = new LicensePayload();
        message = "";
        var lines = fileText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3 || lines[0].Trim() != Header)
        {
            message = "不是有效的 nxa-license v1 文件";
            return false;
        }
        if (!lines[1].Trim().StartsWith("payload ", StringComparison.Ordinal) ||
            !lines[2].Trim().StartsWith("signature ", StringComparison.Ordinal))
        {
            message = "缺少 payload/signature 行";
            return false;
        }
        byte[] bytes, sig;
        try
        {
            bytes = Convert.FromBase64String(lines[1].Trim()["payload ".Length..]);
            sig = Convert.FromBase64String(lines[2].Trim()["signature ".Length..]);
        }
        catch (FormatException)
        {
            message = "payload/signature 不是合法 base64";
            return false;
        }
        if (!publicKey.VerifyData(bytes, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            message = "签名校验失败（文件被改动或非本产品私钥签发）";
            return false;
        }
        try
        {
            payload = LicensePayload.FromCanonicalJson(bytes);
        }
        catch (Exception e)
        {
            message = "载荷解析失败: " + e.Message;
            return false;
        }
        return true;
    }
}
