using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace NxAssistant.Licensing;

public enum LicenseState
{
    Valid,
    NoLicense,
    MalformedFile,
    InvalidSignature,
    SeatsInvalid,
    NotYetValid,
    Expired,
    MachineMismatch,
}

public sealed class LicenseStatus
{
    public LicenseState State { get; init; }
    public string Message { get; init; } = "";
    public LicensePayload? Payload { get; init; }
    public string LicensePath { get; init; } = "";
    public string MachineHash { get; init; } = "";
    /// <summary>距到期天数；未授权时为 null。</summary>
    public int? DaysLeft { get; init; }

    public bool IsValid => State == LicenseState.Valid;
}

/// <summary>
/// 客户端离线校验（PRD §8.3）：验签 → 验 seats → 验期 → 验指纹（含首次激活锁机）。
/// 授权文件默认位置 %LOCALAPPDATA%\NXAssistant\license.lic，可用环境变量 NXA_LICENSE_PATH 覆盖。
/// </summary>
public static class LicenseManager
{
    public static string DefaultLicensePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXAssistant", "license.lic");

    public static string ActivationStorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NXAssistant", "activation.json");

    public static LicenseStatus Check()
    {
        var path = Environment.GetEnvironmentVariable("NXA_LICENSE_PATH") ?? DefaultLicensePath;
        return Check(path);
    }

    /// <summary>校验指定路径的授权文件（调用方决定路径来源：env/settings.json/默认）。</summary>
    public static LicenseStatus Check(string path)
    {
        var machine = MachineFingerprint.Current();
        if (!File.Exists(path))
            return new LicenseStatus { State = LicenseState.NoLicense, Message = $"未找到授权文件：{path}", LicensePath = path, MachineHash = machine };

        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException e)
        {
            return new LicenseStatus { State = LicenseState.MalformedFile, Message = "授权文件不可读: " + e.Message, LicensePath = path, MachineHash = machine };
        }

        using var pub = TrustedKey.LoadPublic();
        if (!LicenseFormat.TryRead(text, pub, out var payload, out var sigError))
        {
            var state = sigError.Contains("签名") ? LicenseState.InvalidSignature : LicenseState.MalformedFile;
            return new LicenseStatus { State = state, Message = sigError, LicensePath = path, MachineHash = machine };
        }

        if (payload.Seats != 1)
            return new LicenseStatus { State = LicenseState.SeatsInvalid, Message = $"seats={payload.Seats}，一 Key 一机固定为 1", Payload = payload, LicensePath = path, MachineHash = machine };

        var today = DateTime.Today;
        if (!TryDate(payload.NotBefore, out var notBefore) || !TryDate(payload.NotAfter, out var notAfter))
            return new LicenseStatus { State = LicenseState.MalformedFile, Message = "日期字段格式非法", Payload = payload, LicensePath = path, MachineHash = machine };
        if (today < notBefore)
            return new LicenseStatus { State = LicenseState.NotYetValid, Message = $"未到生效期（{payload.NotBefore}）", Payload = payload, LicensePath = path, MachineHash = machine };
        if (today > notAfter)
            return new LicenseStatus { State = LicenseState.Expired, Message = $"已过期（{payload.NotAfter}），请联系产品方续期", Payload = payload, LicensePath = path, MachineHash = machine };

        if (!string.IsNullOrEmpty(payload.MachineHash))
        {
            if (!SignatureEquals(payload.MachineHash, machine))
                return new LicenseStatus { State = LicenseState.MachineMismatch, Message = "Key 已绑定其它机器", Payload = payload, LicensePath = path, MachineHash = machine };
        }
        else
        {
            var activation = ActivationStore.TryLock(payload.LicId, machine, ActivationStorePath);
            if (activation != null)
                return new LicenseStatus { State = LicenseState.MachineMismatch, Message = activation, Payload = payload, LicensePath = path, MachineHash = machine };
        }

        return new LicenseStatus
        {
            State = LicenseState.Valid,
            Message = "授权有效",
            Payload = payload,
            LicensePath = path,
            MachineHash = machine,
            DaysLeft = (int)(notAfter - today).TotalDays,
        };
    }

    public static string StateToCode(LicenseState s) => s switch
    {
        LicenseState.Valid => "valid",
        LicenseState.NoLicense => "not_activated",
        LicenseState.MalformedFile => "license_malformed",
        LicenseState.InvalidSignature => "license_invalid_signature",
        LicenseState.SeatsInvalid => "license_seats_invalid",
        LicenseState.NotYetValid => "license_not_yet_valid",
        LicenseState.Expired => "license_expired",
        LicenseState.MachineMismatch => "license_machine_mismatch",
        _ => "license_invalid",
    };

    private static bool TryDate(string s, out DateTime date) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool SignatureEquals(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 首次激活锁机记录（方案 B）：lic_id → 本机指纹。发未绑机 Key 后，
/// 第一次校验通过即写入；此后同 lic_id 只在该指纹的机器上可用。换机需产品方作废旧证重签。
/// </summary>
internal static class ActivationStore
{
    /// <summary>成功绑定/已绑定返回 null；绑定到其它机器返回原因文本。</summary>
    public static string? TryLock(string licId, string machineHash, string storePath)
    {
        var map = Load(storePath);
        if (map.TryGetValue(licId, out var rec) && rec.MachineHash != machineHash)
            return $"该 Key 已在其它机器激活（锁定时指纹 {rec.MachineHash}，本机 {machineHash}），需产品方作废重签";
        if (rec == null)
        {
            map[licId] = new Record { MachineHash = machineHash, ActivatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
            Save(storePath, map);
        }
        return null;
    }

    private sealed class Record
    {
        public string MachineHash { get; set; } = "";
        public string ActivatedAt { get; set; } = "";
    }

    private static Dictionary<string, Record> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception) { return new(); }
    }

    private static void Save(string path, Dictionary<string, Record> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }
}
