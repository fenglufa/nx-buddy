using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NXOpen;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 助手写操作的具名 undo mark 账本 + undo_last_assistant_change（迁移文档 §5 批 5）。
/// 每个成功的写 op 在 SetUndoMarkName 之后 Record 一条：mark 句柄 + 提交后的模型指纹。
/// 撤销兑现"绝不误撤用户操作"红线，双保险缺一不可：
///  1) 会话里最新可见 undo mark 必须就是本条记录的 mark（或助手自己的 no-op 标记，如 rebuild），
///     用户任何 UI 提交都会留下新的可见 mark 把它顶掉 → 拒绝；
///  2) 当前模型指纹（特征名/抑制态/表达式值/体清单）与提交时快照一致 → 否则拒绝。
/// mark 因用户"清空撤销历史"/关件重开而失效时按陈旧条目丢弃并报错，不盲撤。
/// 账本是进程内静态状态：NX 重启即清空（宁可报"无可撤销"也不猜测历史）。
/// </summary>
internal static class AssistantMarks
{
    private sealed class Entry
    {
        public Session.UndoMarkId Mark;
        public string Name = "";
        public string Fingerprint = "";
    }

    /// <summary>助手自己写的、不改变模型语义的可见 mark（重建/移动后再生），不视为用户操作。</summary>
    private static readonly HashSet<string> NoOpMarks = new(StringComparer.Ordinal)
    {
        "NXA rebuild work part",
        "NXA move object regen",
    };

    private const int MaxDepth = 8;
    private static readonly Dictionary<string, Stack<Entry>> ByPart =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Record(Part work, Session.UndoMarkId mark, string name)
    {
        string key;
        try
        {
            key = work.FullPath;
            if (string.IsNullOrEmpty(key)) return; // 未落盘的临时部件不进账本
        }
        catch { return; }

        if (!ByPart.TryGetValue(key, out var stack))
            stack = ByPart[key] = new Stack<Entry>();
        stack.Push(new Entry { Mark = mark, Name = name, Fingerprint = Fingerprint(work) });
        if (stack.Count > MaxDepth)
            ByPart[key] = new Stack<Entry>(stack.Take(MaxDepth)); // 丢最旧的
    }

    /// <summary>回退当前工作部件上助手最近一次成功写入；拒绝路径全部抛 InvalidOperationException（Guard 转 ok:false）。</summary>
    public static object UndoLast(Part work)
    {
        var session = Session.GetSession();
        var path = work.FullPath;
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                "undo_last_assistant_change 需要已落盘的工作部件（当前部件没有文件路径，无法对账）");

        if (!ByPart.TryGetValue(path, out var stack)) stack = new Stack<Entry>();
        int stale = 0;
        while (stack.Count > 0)
        {
            var top = stack.Peek();
            bool exists;
            try { exists = session.DoesUndoMarkExist(top.Mark, top.Name); }
            catch { exists = false; }
            if (!exists)
            {
                stack.Pop();
                stale++;
                continue;
            }

            string newestName = string.Empty;
            try
            {
                newestName = session.GetUndoMarkName(session.NewestVisibleUndoMark)
                             ?? string.Empty;
            }
            catch { /* 无可见 mark 时按不满足处理 */ }
            if (newestName != top.Name && !NoOpMarks.Contains(newestName))
                throw new InvalidOperationException(
                    $"助手上一次写入（{top.Name}）之后撤销栈出现了新的可见标记" +
                    $"（最新: {(string.IsNullOrEmpty(newestName) ? "<未命名>" : newestName)}），" +
                    "其中可能包含用户手工操作。为避免误撤已拒绝，请在 NX 中用 编辑>撤销 逐级回退。");

            if (Fingerprint(work) != top.Fingerprint)
                throw new InvalidOperationException(
                    $"助手上一次写入（{top.Name}）之后模型内容又发生了变化（指纹不一致），" +
                    "可能包含用户手工操作。为避免误撤已拒绝。");

            int featuresBefore = ToolService.CountFeatures(work);
            int bodiesBefore = ToolService.CountBodies(work);
            session.UndoToMark(top.Mark, top.Name);
            stack.Pop();
            return new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["undone_change"] = top.Name,
                ["feature_count_before"] = featuresBefore,
                ["feature_count_after"] = ToolService.CountFeatures(work),
                ["body_count_before"] = bodiesBefore,
                ["body_count_after"] = ToolService.CountBodies(work),
                ["note"] = "仅会话内回退；磁盘文件未动，如需固化请再 save_work_part。",
            };
        }

        throw new InvalidOperationException(
            "没有可撤销的助手变更：账本为空" +
            (stale > 0 ? $"（另丢弃 {stale} 条 mark 已失效的记录——NX 撤销历史被清空或部件被重开）" : "") +
            "。插件/NX 重启后账本不保留。");
    }

    /// <summary>模型指纹：特征名+抑制态+全部表达式 RHS，体名清单。任何建模改动都会翻指纹。</summary>
    public static string Fingerprint(Part part)
    {
        var sb = new StringBuilder();
        foreach (NXOpen.Features.Feature f in part.Features)
        {
            try
            {
                sb.Append(f.Name).Append('|');
                sb.Append(f.Suppressed ? 'S' : '-').Append('|');
                try
                {
                    foreach (var ex in f.GetExpressions())
                        sb.Append(ex.Value).Append(';');
                }
                catch { /* 部分特征无表达式 */ }
                sb.Append('\n');
            }
            catch { sb.Append("?\n"); }
        }
        sb.Append("B:");
        foreach (Body b in part.Bodies)
        {
            try { sb.Append(b.Name).Append(','); }
            catch { sb.Append("?,"); }
        }
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
    }
}
