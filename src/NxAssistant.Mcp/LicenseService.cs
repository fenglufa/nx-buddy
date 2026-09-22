using System.Text.Json;
using NxAssistant.Licensing;

namespace NxAssistant.Mcp;

/// <summary>
/// 宿主侧授权门面（PRD §8）：离线验签 → 验期 → 验指纹（首激活锁机）。
/// license_status / ping 永远可读；create / review 类工具必须先过 EnsureValid()，
/// 未授权时以 LICENSE_INVALID 拒绝。
/// </summary>
public sealed class LicenseService
{
    private readonly object _gate = new();
    private LicenseStatus? _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>结果缓存 10 分钟，避免每次工具调用都读文件+验签；闸门与状态共用。</summary>
    private LicenseStatus Current()
    {
        lock (_gate)
        {
            if (_cached != null && DateTime.Now - _cachedAt < TimeSpan.FromMinutes(10))
                return _cached;
            _cached = LicenseManager.Check();
            _cachedAt = DateTime.Now;
            return _cached;
        }
    }

    public void InvalidateCache()
    {
        lock (_gate) { _cached = null; }
    }

    /// <summary>create/review 工具闸门：未授权抛 LICENSE_INVALID。</summary>
    public void EnsureValid(string toolName)
    {
        var s = Current();
        if (!s.IsValid)
            throw new LicenseGateException(toolName, s);
    }

    public JsonElement Status()
    {
        var s = Current();
        var payload = new Dictionary<string, object?>
        {
            ["licensed"] = s.IsValid,
            ["state"] = LicenseManager.StateToCode(s.State),
            ["reason"] = s.Message,
            ["license_path"] = s.LicensePath,
            ["machine_hash"] = s.MachineHash,
            ["standard_pack"] = s.Payload?.StandardPack ?? "company_v3+huaheng_logistics_robot_v1",
        };
        if (s.Payload != null)
        {
            payload["lic_id"] = s.Payload.LicId;
            payload["customer"] = s.Payload.Customer;
            payload["not_before"] = s.Payload.NotBefore;
            payload["not_after"] = s.Payload.NotAfter;
            payload["features"] = s.Payload.Features;
            payload["bound_machine_hash"] = s.Payload.MachineHash;
        }
        if (s.DaysLeft != null) payload["days_left"] = s.DaysLeft;
        return JsonSerializer.SerializeToElement(payload, Core.Protocol.NxJson.Options);
    }
}

/// <summary>未授权闸门异常。宿主工具层把它转成 MCP 错误，错误码 LICENSE_INVALID（PRD §8.3）。</summary>
public sealed class LicenseGateException : Exception
{
    public LicenseGateException(string toolName, LicenseStatus s)
        : base($"LICENSE_INVALID: 工具 {toolName} 需要有效授权（{LicenseManager.StateToCode(s.State)}：{s.Message}）。" +
               "请用 license_status 查看详情或联系产品方签发 Key。")
    {
    }
}
