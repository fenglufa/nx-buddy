using System.Text.Json;

namespace NxAssistant.Mcp;

/// <summary>
/// 授权状态（占位实现）。完整离线验签 / 一 Key 一机 / 绑机在"授权批次"接入（PRD §8）。
/// 骨架阶段返回一个可读的未授权状态，且暂不把 ping/review 闸门关闭，以便联调链路。
/// </summary>
public sealed class LicenseService
{
    public JsonElement Status()
    {
        var payload = new Dictionary<string, object?>
        {
            ["licensed"] = false,
            ["state"] = "not_activated",
            ["reason"] = "授权验签尚未接入（骨架占位）；license/ 批次实现签发与离线校验",
            ["standard_pack"] = "company_v3+huaheng_logistics_robot_v1",
        };
        return JsonSerializer.SerializeToElement(payload, Core.Protocol.NxJson.Options);
    }
}
