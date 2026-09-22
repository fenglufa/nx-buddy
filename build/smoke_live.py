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
import time


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
    # 金样板自 11:36 未变：CYLINDER(1) 存留“Tool body completely outside target body”历史报错，
    # 但 DoUpdate 本身 0 错误。冒烟只硬校验 update_error_count，diagnostics 打印供人工判读。
    assert rb.get("update_error_count") == 0, rb
    print("C7 rebuild_work_part -> update_errors=0, diagnostics =", len(rb.get("diagnostics", [])))


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

    # create_part 遇同名文件会拒绝（existsFail），所有新建件统一带时间戳后缀保证可重复运行。
    sfx = str(int(time.time()))
    name = os.environ.get("NXA_LIVE_PART", f"NXA_LIVE_smoke1_{sfx}")
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

    # ---- 阶段 D：写操作闭环（在 B1 新建件上 block→sketch→extrude→表达式改名） ----
    d1 = payload(h2.call("create_block", {"length": 50, "width": 30, "height": 20}))
    assert d1.get("ok") and d1.get("body_count") == 1, d1
    print("D1 create_block ->", d1["name"], "bodies =", d1["body_count"])

    d2 = payload(h2.call("create_parametric_sketch", {
        "name": "MCP_PROFILESK", "plane": "XY",
        "geometry": [{"type": "rectangle", "name": "R0", "origin": [-20, -10], "width": 40, "height": 20}],
        "dimensions": [{"type": "horizontal", "geometry": "R0_0", "value": 40.0, "name": "WIDTH"}],
    }))
    assert d2.get("ok"), d2
    assert d2.get("constraint_count", 0) >= 8, d2  # 矩形自动 8 条 + 0 手传
    expr_name = d2["dimensions"][0]["expression"]["name"]
    assert expr_name.endswith("_WIDTH"), expr_name
    print("D2 create_parametric_sketch ->", d2["name"], "expr =", expr_name, "status =", d2["status"])

    d3 = payload(h2.call("extrude_sketch", {
        "sketch_id": d2["journal_id"], "distance": 15,
        "direction": [0, 0, 1], "feature_name": "MCP_PAD",
    }))
    assert d3.get("ok") and d3.get("section_loop_count") == 1, d3
    assert d3.get("feature_body_count") == 1, d3
    print("D3 extrude_sketch ->", d3["name"], "loops =", d3["section_loop_count"])

    d4 = payload(h2.call("inspect_sketch", {"sketch_id": d2["name"]}))
    assert d4.get("ok") and d4["sketch_count"] == 1, d4
    sk0 = d4["sketches"][0]
    assert sk0["geometry_count"] == 4 and any(
        e["name"].endswith("_WIDTH") and abs(e["value"] - 40.0) < 1e-6 for e in sk0["expressions"]), sk0
    print("D4 inspect_sketch ->", sk0["name"], "geom =", sk0["geometry_count"], "exprs =", sk0["expression_count"])

    d5 = payload(h2.call("set_feature_expression", {
        "feature_id": d2["feature_journal_id"], "expression_id": expr_name, "right_hand_side": "25",
    }))
    assert d5.get("ok") and abs(d5["new_expression"]["value"] - 25.0) < 1e-6, d5
    print("D5 set_feature_expression -> 40 -> 25 生效, update_errors =", d5["update_error_count"])

    d6 = payload(h2.call("rebuild_work_part"))
    assert d6.get("ok") is True, d6
    d7 = payload(h2.call("save_work_part"))
    assert d7.get("file_exists"), d7
    print("D6 rebuild ok; D7 save ->", d7["full_path"], d7["file_size"], "bytes")

    # ---- 阶段 E：create_cylindrical_hole + HOLE-DIA-001 写前守卫 ----
    e1 = payload(h2.call("create_cylindrical_hole", {
        "diameter": 10, "depth": 60, "origin": [25, 15, 20], "direction": [0, 0, -1],
        "feature_name": "NXA_PH10",
    }))
    assert e1.get("ok") and abs(e1.get("diameter", 0) - 10) < 1e-9, e1
    print("E1 合规 φ10 孔 ->", e1["name"], "| bodies =", e1["part_body_count"])

    before = payload(h2.call("get_part_summary"))
    e2 = payload(h2.call("create_cylindrical_hole", {
        "diameter": 13.2, "depth": 60, "origin": [25, 15, 20], "direction": [0, 0, -1],
    }))
    assert e2.get("ok") is False and e2.get("error_type") == "RULE_BLOCKED", e2
    f0 = e2["findings"][0]
    assert f0["rule_id"] == "HOLE-DIA-001", f0
    assert "13" in f0["suggestions"] and "14" in f0["suggestions"], f0
    print("E2 φ13.2 被写前拦截 ->", f0["message"], "| 建议 =", f0["suggestions"])

    after = payload(h2.call("get_part_summary"))
    assert after.get("feature_count") == before.get("feature_count"), (before, after)
    print("E3 拦截后 NX 侧零改动（feature_count =", after.get("feature_count"), "）")

    e4 = payload(h2.call("save_work_part"))
    assert e4.get("file_exists"), e4
    print("E4 save ->", e4["full_path"], e4["file_size"], "bytes")

    # ---- 阶段 F：fillet/chamfer + FEAT-FILLET-002 warn 守卫 ----
    # 每步用独立的全新"简单块"样件，避免前一步特征改变拓扑后 edge index 漂移干扰断言。

    def fresh_block(name):
        cp = payload(h2.call("create_part", {"file_name": name, "units": "millimeters"}))
        assert cp.get("ok"), cp
        b = payload(h2.call("create_block", {"length": 50, "width": 30, "height": 20}))
        assert b.get("ok") and b.get("body_count") == 1, b
        t = payload(h2.call("inspect_body_topology", {"body_index": 0}))
        assert t.get("ok"), t
        return t["edges"]

    edges = fresh_block(f"NXA_LIVE_fillet10_{sfx}")
    f1 = payload(h2.call("fillet_edges", {
        "radius": 3.0, "edge_indices": [max(edges, key=lambda e: e["length"])["index"]],
        "feature_name": "MCP_FILLET_R3",
    }))
    assert f1.get("ok") and "rule_warnings" not in f1, f1
    print("F1 fillet r=3（系列内）->", f1["name"], "无告警")

    edges = fresh_block(f"NXA_LIVE_fillet27_{sfx}")
    f2 = payload(h2.call("fillet_edges", {
        "radius": 2.7, "edge_indices": [max(edges, key=lambda e: e["length"])["index"]],
    }))
    assert f2.get("ok"), f2
    w0 = (f2.get("rule_warnings") or [{}])[0]
    assert w0.get("rule_id") == "FEAT-FILLET-002" and w0.get("suggestions"), f2
    print("F2 fillet r=2.7 成孔但带 warn ->", w0["message"], "| 建议 =", w0["suggestions"])

    edges = fresh_block(f"NXA_LIVE_chamfer_{sfx}")
    f3 = payload(h2.call("chamfer_edges", {
        "distance": 2.0, "edge_indices": [min(edges, key=lambda e: e["length"])["index"]],
        "feature_name": "MCP_CHAMFER2",
    }))
    assert f3.get("ok"), f3
    rb3 = payload(h2.call("rebuild_work_part"))
    assert rb3.get("ok") is True and rb3.get("update_error_count") == 0, rb3
    f4 = payload(h2.call("save_work_part"))
    assert f4.get("file_exists"), f4
    print("F3 chamfer d=2 + rebuild 0 错 ->", f3["name"], "; F4 save ->", f4["file_size"], "bytes")

    # ---- 阶段 G：move_object 包围盒位移回读护栏（§5 #20 教科书样板） ----
    # 实机结论：block/extrude 均为特征驱动实体，Move Body "提交成功但没动"，
    # 护栏必须回滚且实体包围盒分毫不变。真移动的正路径等非特征实体来源
    # （import_exchange STEP 导入件）到位后补测。
    g0 = payload(h2.call("move_object", {"translation": [0.0, 0.0, 0.0]}))
    assert g0.get("ok") is not True and "zero" in json.dumps(g0), g0
    print("G0 零向量 translation 宿主侧直接拒绝 ->", g0["error"][:60])

    def rollback_guard(tag):
        before = payload(h2.call("inspect_work_part_geometry"))
        mv = payload(h2.call("move_object", {"translation": [10.0, 0.0, 5.0]}))
        after = payload(h2.call("inspect_work_part_geometry"))
        assert mv.get("ok") is not True, mv
        txt = json.dumps(mv, ensure_ascii=False)
        assert "rolled back" in txt or "did not move" in txt or "rejected" in txt, mv
        b0 = before["bodies"][0]["bounds"]["max"]
        a0 = after["bodies"][0]["bounds"]["max"]
        assert all(abs(b0[i] - a0[i]) < 1e-6 for i in range(3)), (before, after, mv)
        rb = payload(h2.call("rebuild_work_part"))
        assert rb.get("ok") is True and rb.get("update_error_count") == 0, rb
        sv = payload(h2.call("save_work_part"))
        assert sv.get("file_exists"), sv
        print(f"{tag} 移动被回滚 ->", mv["error"][:110], "... | 包围盒不变, rebuild 0 错, save ok")

    fresh_block(f"NXA_LIVE_moveblk_{sfx}")
    rollback_guard("G1 block(特征驱动)")

    gp = payload(h2.call("create_part", {"file_name": f"NXA_LIVE_movepad_{sfx}", "units": "millimeters"}))
    assert gp.get("ok"), gp
    sk = payload(h2.call("create_parametric_sketch", {
        "name": "MCP_MOVESK", "plane": "XY",
        "geometry": [{"type": "rectangle", "name": "R0", "origin": [-20, -10], "width": 40, "height": 20}],
        "dimensions": [{"type": "horizontal", "geometry": "R0_0", "value": 40.0, "name": "WIDTH"}],
    }))
    assert sk.get("ok"), sk
    pad = payload(h2.call("extrude_sketch", {
        "sketch_id": sk["journal_id"], "distance": 15, "direction": [0, 0, 1], "feature_name": "MCP_MOVEPAD",
    }))
    assert pad.get("ok"), pad
    mv2 = payload(h2.call("move_object", {"translation": [0.0, 0.0, 30.0]}))
    after2 = payload(h2.call("inspect_work_part_geometry"))
    if mv2.get("ok"):
        print("G2 实测：extrude 实体真的被移动了（NX 行为更新，回滚分支留作保险）->", mv2["name"])
        d2z = after2["bodies"][0]["bounds"]["max"][2] - 15.0
        assert abs(d2z - 30.0) <= 1e-3, (d2z, after2)
    else:
        txt2 = json.dumps(mv2, ensure_ascii=False)
        assert "rolled back" in txt2 or "did not move" in txt2 or "rejected" in txt2, mv2
        rb2 = payload(h2.call("rebuild_work_part"))
        assert rb2.get("ok") is True and rb2.get("update_error_count") == 0, rb2
        print("G2 extrude 实体移动被回滚 ->", mv2["error"][:110], "... | rebuild 0 错")

    # ---- 阶段 H：export_exchange STEP 导出（FILE-001 工作区沙箱 + 文件回读护栏） ----
    step_name = f"NXA_LIVE_step_{sfx}.stp"
    hp = payload(h2.call("create_part", {"file_name": f"NXA_LIVE_step_{sfx}", "units": "millimeters"}))
    assert hp.get("ok"), hp
    hb = payload(h2.call("create_block", {"length": 40, "width": 25, "height": 15}))
    assert hb.get("ok"), hb
    hs = payload(h2.call("save_work_part"))
    assert hs.get("file_exists"), hs

    bad = payload(h2.call("export_exchange", {"file_name": "../escape.stp"}))
    assert bad.get("ok") is not True and "plain file name" in json.dumps(bad), bad
    fmt = payload(h2.call("export_exchange", {"file_name": step_name, "format": "parasolid"}))
    assert fmt.get("ok") is not True and "step" in json.dumps(fmt), fmt
    hx = payload(h2.call("export_exchange", {"file_name": step_name}))
    assert hx.get("ok") and hx["file_size"] > 1000, hx
    with open(hx["output_file"], "rb") as f:
        head = f.read(128)
    assert b"ISO-10303" in head, head[:64]
    print("H0 ../逃逸 与 format=parasolid 均被拒 ->", bad["error"][:52], "|", fmt["error"][:30])
    print("H1 STEP(ap242) 导出 ->", hx["output_file"], hx["file_size"], "bytes, 文件头含 ISO-10303")

    dup = payload(h2.call("export_exchange", {"file_name": step_name}))
    assert dup.get("ok") is not True and "already exists" in json.dumps(dup), dup
    ow = payload(h2.call("export_exchange", {"file_name": step_name, "overwrite": True}))
    assert ow.get("ok"), ow
    print("H2 重复导出被拒 / overwrite=true 重导 ok ->", ow["file_size"], "bytes")
    h2.close()
    print(f"SMOKE LIVE PASS (pid={pid}, part={c2.get('full_path')})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
