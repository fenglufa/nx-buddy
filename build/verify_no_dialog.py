#!/usr/bin/env python3
"""验证"主线程 marshal 异常不再弹 WinForms JIT 对话框"（MainThread RemoteBox 修复）。

用法: python build/verify_no_dialog.py <path-to-NxAssistant.Mcp.exe>
前置：NX 2412 已启动且插件就绪（日志 pipe server ready），初始无工作部件。
步骤：无件 get_part_summary（必抛）→ 开金样板 → 失配 stable_id resolve_topology（必抛）
→ 每步后枚举 ugraf 可见窗口，出现 .NET 异常对话框标题即失败。
"""
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from smoke_live import Host, payload

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLATE = os.path.join(ROOT, "test", "drawings", "NXA_TEST_OK_plate.prt")
PLUGIN_LOG = os.path.join(os.environ.get("TEMP", "."), "nxa_plugin.log")
DIALOG_MARKERS = [".NET Framework", "异常", "Microsoft Visual", "jit_debug", "Unhandled"]


def ugraf_pid():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq ugraf.exe", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, encoding="gbk", errors="replace").stdout
    for line in out.splitlines():
        cols = line.strip('"').split('","')
        if len(cols) >= 2 and cols[0].lower() == "ugraf.exe":
            return int(cols[1])
    return None


def visible_titles(pid):
    ps1 = os.path.join(os.path.dirname(os.path.abspath(__file__)), "list_windows.ps1")
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                        "-File", ps1, "-TargetPid", str(pid)],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return [t for t in r.stdout.splitlines() if t.strip()]


def no_dialog(stage):
    pid = ugraf_pid()
    assert pid, "ugraf.exe 未在运行"
    titles = visible_titles(pid)
    bad = [t for t in titles if any(m in t for m in DIALOG_MARKERS)]
    assert not bad, f"{stage}: ugraf 出现异常对话框窗口: {bad}"
    print(f"   [窗口] {stage}: ugraf pid={pid}, 可见窗口 {len(titles)} 个，无异常弹窗")


def log_lines():
    try:
        with open(PLUGIN_LOG, "r", encoding="utf-8", errors="replace") as f:
            return sum(1 for _ in f)
    except FileNotFoundError:
        return 0


def main():
    exe = sys.argv[1]
    base = log_lines()
    h = Host(exe)
    assert payload(h.call("ping")).get("ok"), "ping 失败"
    print("0 ping ok (日志基线 {} 行)".format(base))

    # 1) 无工作部件 → handler 在主线程抛 InvalidOperationException（修复前必弹对话框）
    r = payload(h.call("get_part_summary"))
    assert r.get("ok") is False and "没有打开的工作部件" in str(r.get("error", "")), r
    print("1 无件 get_part_summary -> 按设计返回错误信封:", r["error"])
    time.sleep(1.0)
    no_dialog("无件报错后")

    # 2) 打开金样板（文件关联 → 注入运行中的 NX 会话）
    os.startfile(PLATE)
    for i in range(120):
        time.sleep(1.0)
        r = payload(h.call("get_part_summary"))
        if r.get("ok"):
            print(f"2 金样板已打开 (t+{i+1}s):", r.get("name"))
            break
    else:
        raise AssertionError("120s 内金样板未打开")

    # 3) 失配 stable_id 且无回退 → ArgumentException（用户报的正是这条弹的窗）
    bad = payload(h.call("resolve_topology",
                         {"kind": "face", "selector": {"stable_id": "face:" + "0" * 20}}))
    assert bad.get("ok") is False and "no geometry fallback" in str(bad.get("error", "")), bad
    print("3 失配 stable_id resolve_topology -> 按设计返回错误信封:", bad["error"])
    time.sleep(1.0)
    no_dialog("stable_id 报错后")

    # 4) 主线程仍响应（若模态弹窗未被处理，后续调用会超时/挂住）
    assert payload(h.call("ping")).get("ok"), "ping 失败：主线程疑似被卡"
    print("4 报错后 ping 仍 ok —— NX 主线程消息泵未被异常弹窗劫持")
    h.close()
    print("VERIFY NO-DIALOG PASS")


if __name__ == "__main__":
    main()
