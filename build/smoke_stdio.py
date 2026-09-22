#!/usr/bin/env python3
"""MCP 宿主 stdio 冒烟：initialize -> initialized -> tools/list，断言 V1 工具已注册。

用法: python build/smoke_stdio.py <path-to-NxAssistant.Mcp.exe>
不需要 NX 在跑（ping 会因插件未连而失败，但 tools/list 不依赖插件）。
"""
import json
import os
import subprocess
import sys
import tempfile
import threading

def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else None
    if not exe:
        print("usage: smoke_stdio.py <NxAssistant.Mcp.exe>")
        return 2

    # 第十三片守卫测试用临时规则态（绝不碰真机 %LOCALAPPDATA% 的用户覆盖层）
    state_fd, state_path = tempfile.mkstemp(prefix="nxa_state_", suffix=".json")
    os.close(state_fd)
    os.unlink(state_path)  # 起点=不存在该文件（宿主按"无覆盖层"解析）
    env = dict(os.environ, NXA_RULES_STATE=state_path)

    proc = subprocess.Popen(
        [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", bufsize=1, env=env)

    def send(obj):
        proc.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    def readline():
        line = proc.stdout.readline()
        return line

    # initialize
    send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2024-11-05", "capabilities": {},
        "clientInfo": {"name": "smoke", "version": "0"}}})
    resp = json.loads(readline())
    assert resp.get("id") == 1, resp
    print("initialize OK; server:", resp["result"].get("serverInfo", {}))

    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    tools = json.loads(readline())["result"]["tools"]
    names = sorted(t["name"] for t in tools)
    print("tools/list:", names)
    expected = {"ping", "license_status", "get_part_summary",
                "inspect_work_part_geometry", "save_work_part", "create_part",
                "inspect_body_topology", "resolve_topology", "inspect_feature",
                "rebuild_work_part", "create_block", "create_parametric_sketch",
                "inspect_sketch", "extrude_sketch", "set_feature_expression",
                "create_cylindrical_hole", "fillet_edges", "chamfer_edges", "move_object",
                "export_exchange", "import_exchange",
                "inspect_layers", "inspect_drawing_annotations", "inspect_title_block",
                "inspect_drawing_sheet", "inspect_weld_annotations", "inspect_parts_list",
                "inspect_sheet_thickness", "inspect_hole_pattern",
                "undo_last_assistant_change",
                "review_folder", "review_status", "review_findings"}
    missing = expected - set(names)
    assert not missing, f"missing tools: {missing}"
    for t in tools:
        assert t.get("inputSchema") is not None, f"tool {t['name']} has no schema"
        assert t.get("description"), f"tool {t['name']} has no description"

    # ---- T4 第十三片：写前守卫 + rules_state 覆盖层热加载（同进程免重启即生效）----
    def call_tool(name, args, rid):
        send({"jsonrpc": "2.0", "id": rid, "method": "tools/call",
              "params": {"name": name, "arguments": args}})
        msg = json.loads(readline())
        return "".join(c.get("text", "") for c in msg["result"]["content"]
                        if c.get("type") == "text")

    txt3 = call_tool("create_cylindrical_hole", {"diameter": 13.2, "depth": 5}, 3)
    assert "RULE_BLOCKED" in txt3 and "13" in txt3 and "14" in txt3, \
        f"φ13.2 未被 HOLE-DIA-001 拦截（或本机授权无效）：{txt3[:400]}"
    print("T4a 写前守卫：φ13.2 被拦并建议邻近值 13/14")

    # target_body_index=999：即使真机 NX 在跑且有打开部件，也只会在校 body 索引时报错，绝不会真钻孔
    with open(state_path, "w", encoding="utf-8") as f:
        json.dump({"disabled": ["HOLE-DIA-001"], "data": {}, "log": []}, f)
    txt4 = call_tool("create_cylindrical_hole",
                     {"diameter": 13.2, "depth": 5, "target_body_index": 999}, 4)
    assert "RULE_BLOCKED" not in txt4, f"rules_state 禁用未热加载生效：{txt4[:400]}"
    assert '"ok": false' in txt4 or '"ok":false' in txt4, \
        f"守卫放行后应由 IPC/插件阶段报错（离线=未连接，在线=body 索引越界）：{txt4[:400]}"
    print("T4b 用户态禁用规则即时生效：守卫放行，请求进入插件阶段（body 索引越界/未连接报错属预期）")
    try:
        os.unlink(state_path)
    except OSError:
        pass

    print("SMOKE PASS")
    proc.terminate()
    return 0

if __name__ == "__main__":
    sys.exit(main())
