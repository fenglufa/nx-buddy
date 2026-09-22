using System.Collections.Generic;

namespace NxAssistant.Rules;

/// <summary>一次规则判定结果。宿主审图与插件写前拦截共用同一结构。</summary>
public sealed class Finding
{
    public string RuleId { get; init; } = "";
    public string Standard { get; init; } = "";
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>规则声明的严重级：fail | warn。</summary>
    public string Severity { get; init; } = "warn";
    /// <summary>解析后的强制级别：block（拦截）| warn（提示）| off（忽略）。</summary>
    public string Enforcement { get; init; } = "warn";
    /// <summary>被检查对象的可读标识（孔特征名/图幅/字段名等）。</summary>
    public string Subject { get; init; } = "";
    /// <summary>实测/实际值（如直径、板厚）。</summary>
    public string? Value { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Suggestions { get; init; } = new List<string>();
    /// <summary>该规则引用的数据文件是否为客户待替换的占位表。</summary>
    public bool Placeholder { get; init; }

    public bool IsBlocking => Enforcement == "block";
}
