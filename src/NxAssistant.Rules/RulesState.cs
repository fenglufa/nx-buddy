using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NxAssistant.Rules;

/// <summary>变更日志一条。</summary>
public sealed class StateLogEntry
{
    public string T { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// 用户态规则覆盖层（第十三片）：基准包 company_v3 只读、升级整目录替换；
/// 用户的"禁用某规则 / 替换某数据文件"写进独立的 rules_state.json，并逐次留痕
/// （审图报告要能回答"这条规则是谁关的"）。
/// 注意：覆盖层不改变 fail-closed——基准包缺失仍是不可用，禁用≠删除。
/// 结构：{version, disabled:[rule_id], data:{"相对包根路径.json": 整文件替换}, log:[...]}
/// </summary>
public sealed class RulesState
{
    private const int MaxLog = 500;

    public static readonly JsonSerializerOptions IoOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null, // data 的键是文件相对路径，不能重命名
    };

    public List<string> Disabled { get; set; } = new();
    public Dictionary<string, JsonElement> Data { get; set; } = new();
    public List<StateLogEntry> Log { get; set; } = new();

    public bool IsDisabled(string ruleId) => Disabled.Contains(ruleId);

    public bool HasDataOverride(string relPath) =>
        Data.ContainsKey(NormalizeRel(relPath));

    public bool TryDataOverride(string relPath, out JsonElement data) =>
        Data.TryGetValue(NormalizeRel(relPath), out data);

    /// <summary>相对包根路径统一成 "a/b.json"（正斜杠、无 ./），跨 Win/Linux 分隔符一致。</summary>
    public static string NormalizeRel(string relPath) =>
        relPath.Replace('\\', '/').TrimStart('/');

    public void SetDisabled(string ruleId, string ruleName, bool disabled)
    {
        if (disabled)
        {
            if (!Disabled.Contains(ruleId)) Disabled.Add(ruleId);
            AddLog("disable", $"{ruleId} {ruleName}");
        }
        else
        {
            if (Disabled.Remove(ruleId)) AddLog("enable", $"{ruleId} {ruleName}");
        }
    }

    /// <summary>整文件替换某数据文件（相对路径命中即覆盖基准包同名文件的内容）。</summary>
    public void SetDataOverride(string relPath, string detail, JsonElement replacement)
    {
        Data[NormalizeRel(relPath)] = replacement;
        AddLog("data_override", detail);
    }

    /// <summary>撤销某文件的用户替换，回落基准包。</summary>
    public void ClearDataOverride(string relPath, string detail)
    {
        if (Data.Remove(NormalizeRel(relPath))) AddLog("data_reset", detail);
    }

    public void AddLog(string action, string detail)
    {
        Log.Add(new StateLogEntry
        {
            T = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Action = action,
            Detail = detail,
        });
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
    }

    public static RulesState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<RulesState>(File.ReadAllText(path), IoOptions);
                if (s != null)
                {
                    s.Disabled ??= new();
                    s.Data ??= new();
                    s.Log ??= new();
                    return s;
                }
            }
        }
        catch
        {
            // 与 settings.json 同口径：损坏不炸，按空覆盖层继续（fail-closed 由基准包保证）
        }
        return new RulesState();
    }

    /// <summary>原子落盘：先写 .tmp 再替换，避免半截文件。</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, IoOptions));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NXAssistant", "rules_state.json");
}
