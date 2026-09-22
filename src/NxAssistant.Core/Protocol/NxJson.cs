using System.Text.Json;
using System.Text.Json.Serialization;

namespace NxAssistant.Core.Protocol;

/// <summary>跨进程共用的 JSON 约定：camelCase、忽略空值、不转义中文。</summary>
public static class NxJson
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
