using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace NxAssistant.Rules;

/// <summary>单条规则定义（rules.json 里的一个对象）+ 归属信息。</summary>
public sealed class RuleDef
{
    public string Id { get; set; } = "";
    public string Standard { get; set; } = "";
    /// <summary>fail | warn</summary>
    public string Severity { get; set; } = "warn";
    public string Group { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> AppliesTo { get; set; } = new();
    /// <summary>规则数据文件所在目录（主包=根目录，子包=子目录）。</summary>
    public string DataDir { get; set; } = "";
}

/// <summary>规则包（PRD §7）：company_v3 主包 + 启用的子包合并加载。</summary>
public sealed class RulePack
{
    public string Id { get; private set; } = "";
    public string Version { get; private set; } = "";
    /// <summary>默认强制级别（PRD：block / warn / off）。fail 级规则未单独配置时按此执行。</summary>
    public string DefaultEnforcement { get; private set; } = "block";
    public IReadOnlyList<RuleDef> Rules => _rules;

    private readonly List<RuleDef> _rules = new();
    private readonly Dictionary<string, JsonElement> _dataCache = new();

    public static RulePack Load(string rootDir)
    {
        var pack = new RulePack();
        var packJson = ReadJsonFile(System.IO.Path.Combine(rootDir, "pack.json"));
        pack.Id = packJson.GetProperty("id").GetString() ?? "";
        pack.Version = packJson.GetProperty("version").GetString() ?? "";
        pack.DefaultEnforcement = packJson.TryGetProperty("default_enforcement", out var de)
            ? de.GetString() ?? "block" : "block";

        pack.AddRulesFile(rootDir);
        if (packJson.TryGetProperty("subpacks", out var subs))
            foreach (var s in subs.EnumerateArray())
            {
                var enabled = !s.TryGetProperty("enabled_by_default", out var eb) || eb.GetBoolean();
                if (!enabled) continue;
                var rel = s.GetProperty("path").GetString() ?? "";
                pack.AddRulesFile(System.IO.Path.Combine(rootDir, rel));
            }
        return pack;
    }

    private void AddRulesFile(string dir)
    {
        var doc = ReadJsonFile(System.IO.Path.Combine(dir, "rules.json"));
        var standard = doc.GetProperty("standard").GetString() ?? "";
        foreach (var r in doc.GetProperty("rules").EnumerateArray())
        {
            _rules.Add(new RuleDef
            {
                Id = r.GetProperty("id").GetString() ?? "",
                Standard = standard,
                Severity = r.GetProperty("severity").GetString() ?? "warn",
                Group = r.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "",
                Name = r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                AppliesTo = r.TryGetProperty("applies_to", out var a)
                    ? a.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                    : new List<string>(),
                DataDir = dir,
            });
        }
    }

    /// <summary>按数据文件名（如 "hole_series"）加载该规则所在包目录下的 JSON；带缓存。</summary>
    public JsonElement Data(RuleDef rule, string fileName)
    {
        var key = rule.DataDir + "|" + fileName;
        if (_dataCache.TryGetValue(key, out var cached)) return cached;
        var loaded = ReadJsonFile(System.IO.Path.Combine(rule.DataDir, fileName + ".json"));
        _dataCache[key] = loaded;
        return loaded;
    }

    /// <summary>可选数据文件（如 title_block_fields 别名表）：缺失返回 false，不抛。</summary>
    public bool TryData(RuleDef rule, string fileName, out JsonElement data)
    {
        try { data = Data(rule, fileName); return true; }
        catch (System.IO.FileNotFoundException) { data = default; return false; }
    }

    private static JsonElement ReadJsonFile(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
}
