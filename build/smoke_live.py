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


def phase_c_topology(h):
    """阶段 C：对启动时打开的金样板（NXA_TEST_OK_plate.prt）验证拓扑/stable_id/特征/重建。

    无金样在屏时跳过（不判失败），便于脚本在任意会话下复用。"""
    g = payload(h.call("get_part_summary"))
    if not g.get("ok") or "NXA_TEST_OK_plate" not in str(g.get("name", "")):
        print("C 跳过：工作部件不是金样板（当前:", g.get("name"), "）")
        return
    print("C0 金样板在屏 ->", g.get("name"), "bodies =", g.get("body_count"))

    t = payload(h.call("inspect_body_topology", {"body_index": 0}))
    assert t.get("ok") and t.get("face_count", 0) > 0, t
    assert all(str(f.get("stable_id", "")).startswith("face:") for f in t["faces"]), "面 stable_id 前缀异常"
    assert all(str(e.get("stable_id", "")).startswith("edge:") for e in t["edges"]), "边 stable_id 前缀异常"
    print("C1 inspect_body_topology -> faces =", t["face_count"], "edges =", t["edge_count"])

    face = max(t["faces"], key=lambda f: f["area_proxy"])
    r1 = payload(h.call("resolve_topology", {"kind": "face", "selector": {"stable_id": face["stable_id"]}}))
    assert r1.get("ok") and r1["selected"]["stable_id"] == face["stable_id"], r1
    print("C2 resolve by stable_id -> match_count =", r1["match_count"])

    sel = {"uf_type": face["uf_type"], "normal": face["normal"], "plane_offset": face["plane_offset"],
           "radius": face["radius"], "tolerance": 0.01, "sort_by": "largest"}
    r2 = payload(h.call("resolve_topology", {"kind": "face", "selector": sel}))
    assert r2.get("ok") and r2["match_count"] >= 1, r2
    assert r2["selected"]["stable_id"] == face["stable_id"], "几何回退应命中同一最大面"
    print("C3 resolve by geometry fallback+sort_by -> match_count =", r2["match_count"])

    edge = max(t["edges"], key=lambda e: e["length"])
    r3 = payload(h.call("resolve_topology", {"kind": "edge", "selector": {"stable_id": edge["stable_id"]}}))
    assert r3.get("ok") and r3["selected"]["stable_id"] == edge["stable_id"], r3
    print("C4 resolve edge by stable_id -> length =", r3["selected"]["length"])

    bad = payload(h.call("resolve_topology",
                        {"kind": "face", "selector": {"stable_id": "face:" + "0" * 20}}))
    assert bad.get("ok") is False and "no geometry fallback" in str(bad.get("error", "")), bad
    print("C5 失配 stable_id 且无回退字段 -> 按预期报错")

    hole = next((f for f in g["features"] if "HOLE" in str(f.get("journal_id", "")).upper()
                 or "HOLE" in str(f.get("name", "")).upper()), g["features"][-1])
    fi = payload(h.call("inspect_feature", {"feature_id": hole["journal_id"]}))
    assert fi.get("ok") and fi.get("name"), fi
    print("C6 inspect_feature ->", fi["name"], "|", fi["type"], "| out_of_date =", fi["is_out_of_date"])

    rb = payload(h.call("rebuild_work_part"))
    assert rb.get("ok") is True, rb
    print("C7 rebuild_work_part -> ok, update_error_count =", rb["update_error_count"])


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
    phase_c_topology(h2)

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
