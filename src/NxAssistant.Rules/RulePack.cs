using System;
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
    /// <summary>包根目录（主包目录；子包规则数据在其子目录）。覆盖层按相对此根的路径命中。</summary>
    public string RootDir { get; private set; } = "";

    private readonly List<RuleDef> _rules = new();
    private readonly Dictionary<string, JsonElement> _dataCache = new();
    private RulesState? _state;

    public static RulePack Load(string rootDir, RulesState? state = null)
    {
        var pack = new RulePack { RootDir = System.IO.Path.GetFullPath(rootDir), _state = state };
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
        if (state != null)
            pack._rules.RemoveAll(r => state.IsDisabled(r.Id));
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

    /// <summary>按数据文件名（如 "hole_series"）加载该规则所在包目录下的 JSON；带缓存。
    /// 用户覆盖层（rules_state.json）按"相对包根路径"整文件替换基准内容。</summary>
    public JsonElement Data(RuleDef rule, string fileName)
    {
        var key = rule.DataDir + "|" + fileName;
        if (_dataCache.TryGetValue(key, out var cached)) return cached;
        if (_state != null &&
            _state.TryDataOverride(RelToRoot(rule.DataDir, fileName + ".json"), out var ov))
        {
            _dataCache[key] = ov;
            return ov;
        }
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

    /// <summary>该规则某数据文件（不含扩展名）相对包根的路径，如 "huaheng_logistics_robot_v1/pin_bore.json"。
    /// 覆盖层键即按此匹配（托盘 UI 用）。</summary>
    public string RelPathFor(RuleDef rule, string fileName) => RelToRoot(rule.DataDir, fileName + ".json");

    /// <summary>netstandard2.0 无 Path.GetRelativePath：前缀截取（大小写不敏感的 Windows 路径语义）。</summary>
    private string RelToRoot(string absDir, string fileName)
    {
        var abs = System.IO.Path.GetFullPath(System.IO.Path.Combine(absDir, fileName))
            .Replace('\\', '/');
        var root = RootDir.Replace('\\', '/').TrimEnd('/');
        return abs.Length > root.Length && abs.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
            ? abs.Substring(root.Length + 1)
            : fileName;
    }

    private static JsonElement ReadJsonFile(string path)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
}
