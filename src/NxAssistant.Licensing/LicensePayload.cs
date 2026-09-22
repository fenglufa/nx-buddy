using System.Text.Json;

namespace NxAssistant.Licensing;

/// <summary>
/// 授权载荷（PRD §8.2）。签名与验签都只作用于 CanonicalJson() 的字节，
/// 因此序列化必须确定性：固定字段顺序、不转义非 ASCII、日期用固定格式。
/// </summary>
public sealed class LicensePayload
{
    public string LicId { get; set; } = "";
    public string Customer { get; set; } = "";
    public int Seats { get; set; } = 1;
    /// <summary>生效日 yyyy-MM-dd（含当日）。</summary>
    public string NotBefore { get; set; } = "";
    /// <summary>到期日 yyyy-MM-dd（含当日，按本地时区 23:59:59 前有效）。</summary>
    public string NotAfter { get; set; } = "";
    public List<string> Features { get; set; } = new();
    /// <summary>空串 = 未绑机 Key，首次激活时由本地锁机记录绑定（方案 B）。</summary>
    public string MachineHash { get; set; } = "";
    public string StandardPack { get; set; } = "";
    /// <summary>签发时刻，ISO-8601 UTC，如 2026-09-22T08:00:00Z。</summary>
    public string IssuedAt { get; set; } = "";
    public string Issuer { get; set; } = "";

    public static LicensePayload FromCanonicalJson(byte[] bytes) =>
        ParseCanonical(JsonDocument.Parse(bytes).RootElement);

    public byte[] CanonicalJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            w.WriteStartObject();
            w.WriteString("lic_id", LicId);
            w.WriteString("customer", Customer);
            w.WriteNumber("seats", Seats);
            w.WriteString("not_before", NotBefore);
            w.WriteString("not_after", NotAfter);
            w.WritePropertyName("features");
            w.WriteStartArray();
            foreach (var f in Features) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteString("machine_hash", MachineHash);
            w.WriteString("standard_pack", StandardPack);
            w.WriteString("issued_at", IssuedAt);
            w.WriteString("issuer", Issuer);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static LicensePayload ParseCanonical(JsonElement r) => new()
    {
        LicId = r.GetProperty("lic_id").GetString() ?? "",
        Customer = r.GetProperty("customer").GetString() ?? "",
        Seats = r.GetProperty("seats").GetInt32(),
        NotBefore = r.GetProperty("not_before").GetString() ?? "",
        NotAfter = r.GetProperty("not_after").GetString() ?? "",
        Features = r.GetProperty("features").EnumerateArray().Select(e => e.GetString() ?? "").ToList(),
        MachineHash = r.GetProperty("machine_hash").GetString() ?? "",
        StandardPack = r.GetProperty("standard_pack").GetString() ?? "",
        IssuedAt = r.GetProperty("issued_at").GetString() ?? "",
        Issuer = r.GetProperty("issuer").GetString() ?? "",
    };
}
