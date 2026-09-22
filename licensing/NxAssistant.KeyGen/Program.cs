using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NxAssistant.Licensing;

// 产品方离线签发工具（PRD §8.3）。此程序及其私钥永不进入客户安装包。
// 用法：
//   nxa-keygen genkey  --out private.pem                  生成口令保护的 P-256 私钥并打印公钥 PEM
//   nxa-keygen issue   --private private.pem --customer 长沙华恒 --months 12 [--features a,b]
//                      [--machine-hash H] [--standard-pack company_v3+huaheng_logistics_robot_v1]
//                      --out license.lic [--ledger ledger.jsonl] [--issuer fenglufa]
//   nxa-keygen verify  --lic license.lic --public pub.pem [--ledger ledger.jsonl]
//   nxa-keygen revoke  --lic-id L-... --reason 换机 [--ledger ledger.jsonl]

var cmd = args.Length > 0 ? args[0] : "";
var opt = ParseOpts(args.Skip(1));

switch (cmd)
{
    case "genkey": return GenKey(opt);
    case "issue": return Issue(opt);
    case "verify": return Verify(opt);
    case "revoke": return Revoke(opt);
    default:
        Console.Error.WriteLine("未知命令。可用命令：genkey / issue / verify / revoke");
        return 2;
}

static int GenKey(Dictionary<string, string> o)
{
    var outPath = Req(o, "out");
    if (File.Exists(outPath))
    {
        Console.Error.WriteLine($"私钥已存在，拒绝覆盖：{outPath}（换机/轮换请另存新文件并登记台账）");
        return 1;
    }
    var password = ReadSecret("私钥口令：");
    using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000);
    File.WriteAllText(outPath, ec.ExportEncryptedPkcs8PrivateKeyPem(password, pbe));
    try
    {
        File.SetUnixFileMode(outPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    catch (PlatformNotSupportedException) { /* Windows 用 ACL，不处理 */ }
    Console.Write(ec.ExportSubjectPublicKeyInfoPem());
    Console.Error.WriteLine($"私钥 -> {outPath}；把上面的公钥写入 NxAssistant.Licensing/Keys/trusted_public.pem 后重新构建。");
    return 0;
}

static int Issue(Dictionary<string, string> o)
{
    var privatePath = Req(o, "private");
    var outPath = Req(o, "out");
    var customer = Req(o, "customer");
    var password = ReadSecret("私钥口令：");

    using var ec = ImportEncryptedPkcs8(privatePath, password);
    var today = DateTime.UtcNow.Date;
    var notBefore = DateTime.TryParse(o.GetValueOrDefault("not-before", ""), out var nb) ? nb.Date : today;
    DateTime notAfter;
    if (DateTime.TryParse(o.GetValueOrDefault("not-after", ""), out var na)) notAfter = na.Date;
    else if (int.TryParse(o.GetValueOrDefault("months", ""), out var m)) notAfter = today.AddMonths(m);
    else if (int.TryParse(o.GetValueOrDefault("days", ""), out var d)) notAfter = today.AddDays(d);
    else { Console.Error.WriteLine("必须给 --months / --days / --not-after 之一"); return 2; }

    var payload = new LicensePayload
    {
        LicId = "L-" + notBefore.ToString("yyyyMMdd") + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
        Customer = customer,
        Seats = 1,
        NotBefore = notBefore.ToString("yyyy-MM-dd"),
        NotAfter = notAfter.ToString("yyyy-MM-dd"),
        Features = o.GetValueOrDefault("features", "build,review,draw").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        MachineHash = o.GetValueOrDefault("machine-hash", "").Trim(),
        StandardPack = o.GetValueOrDefault("standard-pack", "company_v3+huaheng_logistics_robot_v1"),
        IssuedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Issuer = o.GetValueOrDefault("issuer", Environment.UserName),
    };

    File.WriteAllText(outPath, LicenseFormat.Sign(payload, ec));
    AppendLedger(o.GetValueOrDefault("ledger", "ledger.jsonl"), new JsonObject
    {
        ["event"] = "issue", ["lic_id"] = payload.LicId, ["customer"] = payload.Customer,
        ["not_before"] = payload.NotBefore, ["not_after"] = payload.NotAfter,
        ["features"] = string.Join(',', payload.Features), ["machine_hash"] = payload.MachineHash,
        ["issued_at"] = payload.IssuedAt, ["issuer"] = payload.Issuer,
    });
    Console.WriteLine($"已签发 {payload.LicId} -> {outPath}（{payload.NotBefore} ~ {payload.NotAfter}，features={string.Join(',', payload.Features)}，绑机={(payload.MachineHash == "" ? "否（首激活锁定）" : payload.MachineHash)}）");
    return 0;
}

static int Verify(Dictionary<string, string> o)
{
    var licPath = Req(o, "lic");
    var pubPem = File.ReadAllText(Req(o, "public"));
    using var pub = ECDsa.Create();
    pub.ImportFromPem(pubPem);
    if (!LicenseFormat.TryRead(File.ReadAllText(licPath), pub, out var p, out var err))
    {
        Console.Error.WriteLine($"验签失败：{err}");
        return 1;
    }
    Console.WriteLine($"载荷合法：{p.LicId} 客户={p.Customer} 期={p.NotBefore}~{p.NotAfter} 绑机={(p.MachineHash == "" ? "未绑（首激活锁定）" : p.MachineHash)} features={string.Join(',', p.Features)} pack={p.StandardPack}");
    var today = DateTime.Today;
    if (today < DateTime.Parse(p.NotBefore)) { Console.Error.WriteLine("状态：未到生效期"); return 3; }
    if (today > DateTime.Parse(p.NotAfter)) { Console.Error.WriteLine("状态：已过期"); return 3; }
    if (IsRevoked(o.GetValueOrDefault("ledger", "ledger.jsonl"), p.LicId, out var reason))
    {
        Console.Error.WriteLine($"状态：已作废（{reason}）");
        return 4;
    }
    Console.WriteLine("状态：有效");
    return 0;
}

static int Revoke(Dictionary<string, string> o)
{
    var licId = Req(o, "lic-id");
    var reason = o.GetValueOrDefault("reason", "");
    AppendLedger(o.GetValueOrDefault("ledger", "ledger.jsonl"), new JsonObject
    {
        ["event"] = "revoke", ["lic_id"] = licId, ["reason"] = reason,
        ["at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), ["operator"] = Environment.UserName,
    });
    Console.WriteLine($"已在台账登记作废：{licId}");
    return 0;
}

static Dictionary<string, string> ParseOpts(IEnumerable<string> rest)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? key = null;
    foreach (var a in rest)
    {
        if (a.StartsWith("--")) { key = a[2..]; d[key] = ""; }
        else if (key != null) { d[key] = a; key = null; }
    }
    return d;
}

static string Req(Dictionary<string, string> o, string name) =>
    o.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new InvalidOperationException($"缺少参数 --{name}");

static string ReadSecret(string prompt)
{
    var fromEnv = Environment.GetEnvironmentVariable("NXA_KEYGEN_PASSWORD");
    if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
    Console.Error.Write(prompt);
    var sb = new StringBuilder();
    try
    {
        while (true)
        {
            var ki = Console.ReadKey(true);
            if (ki.Key == ConsoleKey.Enter) break;
            if (ki.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
            else sb.Append(ki.KeyChar);
        }
        Console.Error.WriteLine();
    }
    catch (InvalidOperationException)
    {
        return Console.ReadLine()?.TrimEnd() ?? throw new InvalidOperationException("无控制台且未设置 NXA_KEYGEN_PASSWORD");
    }
    return sb.ToString();
}

static void AppendLedger(string path, JsonObject entry)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
    if (dir != null) Directory.CreateDirectory(dir);
    File.AppendAllText(path, entry.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) + "\n");
}

static bool IsRevoked(string ledgerPath, string licId, out string reason)
{
    reason = "";
    if (!File.Exists(ledgerPath)) return false;
    foreach (var line in File.ReadLines(ledgerPath))
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
        if (node?["event"]?.GetValue<string>() != "revoke" || node["lic_id"]?.GetValue<string>() != licId) continue;
        reason = node["reason"]?.GetValue<string>() ?? "";
        return true;
    }
    return false;
}

static ECDsa ImportEncryptedPkcs8(string path, string password)
{
    var ec = ECDsa.Create();
    try { ec.ImportFromEncryptedPem(File.ReadAllText(path), password); }
    catch
    {
        ec.Dispose();
        throw new InvalidOperationException("私钥不可用：口令错误或文件不是加密 PKCS#8 PEM");
    }
    return ec;
}
