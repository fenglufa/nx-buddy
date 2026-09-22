#!/usr/bin/env python3
"""实机联调冒烟：Agent(stdio)→MCP 宿主→命名管道→NX 插件→NXOpen 主线程。

用法: python build/smoke_live.py <path-to-NxAssistant.Mcp.exe>
阶段 A（无授权）：ping / get_part_summary / create_part 应被 LICENSE_INVALID 拦截。
阶段 B（带授权 NXA_LICENSE_PATH）：create_part → save_work_part → inspect_work_part_geometry 闭环。
需要 NX 2412 正在运行且已加载 NxAssistant_NxPlugin。
"""
import json
import os
import subprocess
import sys


class Host:
    def __init__(self, exe, env=None):
        e = dict(os.environ)
        if env:
            e.update(env)
        self.proc = subprocess.Popen(
            [exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", errors="replace", bufsize=1, env=e)
        self.n = 0
        self.send({"jsonrpc": "2.0", "id": self._req(), "method": "initialize", "params": {
            "protocolVersion": "2024-11-05", "capabilities": {},
            "clientInfo": {"name": "smoke_live", "version": "0"}}})
        r = self.read()
        assert r.get("result", {}).get("serverInfo"), r
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def _req(self):
        self.n += 1
        return self.n

    def send(self, obj):
        self.proc.stdin.write(json.dumps(obj, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()

    def read(self):
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("host closed stdout:\n" + self.proc.stderr.read())
        return json.loads(line)

    def call(self, tool, args=None):
        rid = self._req()
        self.send({"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                   "params": {"name": tool, "arguments": args or {}}})
        while True:
            r = self.read()
            if r.get("id") == rid:
                return r

    def close(self):
        self.proc.terminate()


def payload(resp):
    """取 tools/call 的 JSON 载荷（structuredContent 或 text）。"""
    res = resp.get("result")
    if res is None:
        return {"_rpc_error": resp.get("error")}
    if "structuredContent" in res:
        return res["structuredContent"]
    txt = res.get("content", [{}])[0].get("text", "")
    try:
        return json.loads(txt)
    except Exception:
        return {"_text": txt}


def main():
    exe = sys.argv[1]
    lic = os.environ.get("NXA_LICENSE_PATH", "")

    # ---- 阶段 A：不带授权 ----
    h = Host(exe, env={"NXA_LICENSE_PATH": "__missing__"})
    p = payload(h.call("ping"))
    assert p.get("ok") or "pid" in json.dumps(p), p
    print("A1 ping ->", json.dumps(p, ensure_ascii=False))
    pid = p.get("pid")
    assert p.get("main_thread") is True or "main_thread" not in p, p

    g = payload(h.call("get_part_summary"))
    print("A2 get_part_summary ->", json.dumps(g, ensure_ascii=False)[:400])

    c = payload(h.call("create_part", {"file_name": "NXA_LIVE_should_be_blocked"}))
    txt = json.dumps(c, ensure_ascii=False)
    assert "LICENSE_INVALID" in txt, f"未授权时 create_part 应被闸门拦截：{txt}"
    print("A3 create_part 无授权被 LICENSE_INVALID 拦截 ✔")
    h.close()

    # ---- 阶段 B：带授权 ----
    if not lic or not os.path.exists(lic):
        print("B 阶段跳过：未设 NXA_LICENSE_PATH 或文件不存在（闸门行为已在 A3 验证）")
        print("SMOKE LIVE PASS (phase A)")
        return 0
    h2 = Host(exe, env={"NXA_LICENSE_PATH": lic})
    ls = payload(h2.call("license_status"))
    print("B0 license_status licensed =", ls.get("licensed"), "state =", ls.get("state"))
    assert ls.get("licensed"), ls

    name = os.environ.get("NXA_LIVE_PART", "NXA_LIVE_smoke1")
    c2 = payload(h2.call("create_part", {"file_name": name, "units": "millimeters"}))
    print("B1 create_part ->", json.dumps(c2, ensure_ascii=False))
    assert c2.get("ok"), c2
    assert c2.get("full_path", "").lower().endswith(name.lower() + ".prt"), c2

    g2 = payload(h2.call("get_part_summary"))
    print("B2 get_part_summary ->", json.dumps(g2, ensure_ascii=False)[:400])
    assert g2.get("name", "").lower().startswith(name.lower()), g2

    i2 = payload(h2.call("inspect_work_part_geometry"))
    print("B3 inspect_work_part_geometry ->", json.dumps(i2, ensure_ascii=False)[:400])

    s2 = payload(h2.call("save_work_part"))
    print("B4 save_work_part ->", json.dumps(s2, ensure_ascii=False))
    assert s2.get("file_exists"), s2
    h2.close()
    print(f"SMOKE LIVE PASS (pid={pid}, part={c2.get('full_path')})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
