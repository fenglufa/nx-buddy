using System.Text.Json;

namespace NxAssistant.Core.Protocol;

/// <summary>
/// 插件返回给宿主的响应信封。沿用 NX-MCP 约定：op 内 result.ok 恒 true，
/// 失败时 ok=false 且带 error；"未生效"这类必须靠字段显式表达，不能靠异常类型猜。
/// </summary>
public sealed class BridgeResponse
{
    public string Id { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public JsonElement? Result { get; set; }
    public ErrorPayload? Error { get; set; }

    public static BridgeResponse Success(string id, object result, JsonSerializerOptions options)
    {
        var element = JsonSerializer.SerializeToElement(result, options);
        return new BridgeResponse { Id = id, Ok = true, Result = element };
    }

    public static BridgeResponse Failure(string id, string message, string type, string? traceback)
    {
        return new BridgeResponse
        {
            Id = id,
            Ok = false,
            Error = new ErrorPayload { Message = message, Type = type, Traceback = traceback },
        };
    }
}

public sealed class ErrorPayload
{
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Traceback { get; set; }
}
