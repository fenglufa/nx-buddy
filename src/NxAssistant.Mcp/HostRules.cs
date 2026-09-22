using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NxAssistant.Rules;

namespace NxAssistant.Mcp;

/// <summary>
/// 宿主侧写前规则守卫（PRD：commit 前拦截）。构造"候选证据"交给 RuleEngine 判定，
/// 命中阻断项则拒绝把写操作转发给 NX。规则包目录解析顺序：
/// NXA_RULES_DIR 环境变量 → 宿主同目录 company_v3\ → 逐级父目录找 docs\company_v3（开发态）。
/// 找不到规则包时 **fail-closed**：拒绝受守卫保护的写，绝不静默放行。
/// </summary>
internal static class HostRules
{
    private static RulePack? _pack;
    private static string? _packError;

    /// <summary>孔直径守卫（HOLE-DIA-001 主战场）。返回 null=放行；否则为 RULE_BLOCKED/RULE_PACK_UNAVAILABLE 载荷。</summary>
    public static JsonElement? CheckHoleWrite(double diameter, string kind)
    {
        var pack = LoadPack();
        if (pack == null)
            return Error("RULE_PACK_UNAVAILABLE",
                $"规则包不可用，已拒绝写入（fail-closed）：{_packError}");

        var ev = new Evidence
        {
            Part = new PartEvidence
            {
                Holes = { new HoleEvidence { Diameter = diameter, Kind = kind, Source = "model" } },
            },
        };
        var blocking = RuleEngine.Blocking(pack, ev);
        if (blocking.Count == 0) return null;
        var first = blocking[0];
        var hints = first.Suggestions.Count > 0 ? "，建议值：" + string.Join(" / ", first.Suggestions) : "";
        return Error("RULE_BLOCKED",
            $"写前规则拦截 [{first.RuleId} {first.Name}]：{first.Message}{hints}", blocking);
    }

    /// <summary>圆角半径守卫（FEAT-FILLET-002，warn 级不阻断）：返回应随响应附带的告警。</summary>
    public static IReadOnlyList<Finding> FilletWarnings(double radius)
    {
        var pack = LoadPack();
        if (pack == null) return Array.Empty<Finding>(); // warn 级：缺包不拦截成孔，仅无告警
        var ev = new Evidence
        {
            Part = new PartEvidence { Fillets = { new FilletEvidence { Radius = radius } } },
        };
        return RuleEngine.Evaluate(pack, ev)
            .Where(f => f.RuleId == "FEAT-FILLET-002" && !f.IsBlocking)
            .ToList();
    }

    private static RulePack? LoadPack()
    {
        if (_pack != null || _packError != null) return _pack;
        try
        {
            var dir = FindPackDir()
                ?? throw new DirectoryNotFoundException(
                    "找不到 company_v3 规则包目录（可设 NXA_RULES_DIR 指向含 pack.json 的目录）");
            _pack = RulePack.Load(dir);
        }
        catch (Exception ex)
        {
            _packError = ex.Message;
        }
        return _pack;
    }

    /// <summary>审图长任务用：加载规则包；失败时给出 fail-closed 原因。</summary>
    public static RulePack? LoadPackForReview(out string? error)
    {
        var pack = LoadPack();
        error = pack == null ? _packError ?? "规则包不可用" : null;
        return pack;
    }

    private static string? FindPackDir()
    {
        var env = Environment.GetEnvironmentVariable("NXA_RULES_DIR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(Path.Combine(env, "pack.json"))) return env;
        var local = Path.Combine(AppContext.BaseDirectory, "company_v3");
        if (File.Exists(Path.Combine(local, "pack.json"))) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "docs", "company_v3");
            if (File.Exists(Path.Combine(cand, "pack.json"))) return cand;
            dir = dir.Parent;
        }
        return null;
    }

    private static JsonElement Error(string type, string message, IReadOnlyList<Finding>? findings = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = message,
            ["error_type"] = type,
        };
        if (findings != null)
            payload["findings"] = findings.Select(f => new Dictionary<string, object?>
            {
                ["rule_id"] = f.RuleId, ["standard"] = f.Standard, ["name"] = f.Name,
                ["severity"] = f.Severity, ["enforcement"] = f.Enforcement,
                ["subject"] = f.Subject, ["value"] = f.Value,
                ["message"] = f.Message, ["suggestions"] = f.Suggestions,
                ["placeholder"] = f.Placeholder,
            }).ToList();
        return JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement;
    }
}
