#!/usr/bin/env python3
"""MCP 宿主 stdio 冒烟：initialize -> initialized -> tools/list，断言 V1 工具已注册。

用法: python build/smoke_stdio.py <path-to-NxAssistant.Mcp.exe>
不需要 NX 在跑（ping 会因插件未连而失败，但 tools/list 不依赖插件）。
"""
import json
import subprocess
import sys
import threading

def main():
    exe = sys.argv[1] if len(sys.argv) > 1 else None
    if not exe:
        print("usage: smoke_stdio.py <NxAssistant.Mcp.exe>")
        return 2

    proc = subprocess.Popen(
        [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", bufsize=1)

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
    expected = {"ping", "license_status"}
    missing = expected - set(names)
    assert not missing, f"missing tools: {missing}"
    for t in tools:
        assert t.get("inputSchema") is not None, f"tool {t['name']} has no schema"
        assert t.get("description"), f"tool {t['name']} has no description"
    print("SMOKE PASS")
    proc.terminate()
    return 0

if __name__ == "__main__":
    sys.exit(main())
