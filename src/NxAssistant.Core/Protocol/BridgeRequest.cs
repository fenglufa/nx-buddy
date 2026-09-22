namespace NxAssistant.Core.Protocol;

/// <summary>
/// 一次工具调用的请求。与 NX-MCP 参考实现的 handle(request_json) 同形：
/// {"id":"...","method":"ping","params":{...}}。
/// </summary>
public sealed class BridgeRequest
{
    public string Id { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public System.Text.Json.JsonElement Params { get; set; }
}
