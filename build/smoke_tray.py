# 托盘冒烟：无头模式 --status-json 的形状与本机一致性检查（不需要 NX 在跑）。
# 用法: python build/smoke_tray.py <dist/tray/NxAssistant.exe> [--expect-plugin-online true|false]
import json
import os
import subprocess
import sys
import tempfile

exe = sys.argv[1]
expect_online = None
if "--expect-plugin-online" in sys.argv:
    expect_online = sys.argv[sys.argv.index("--expect-plugin-online") + 1] == "true"

out = subprocess.run([exe, "--status-json"], capture_output=True, text=True, encoding="utf-8", timeout=30)
assert out.returncode == 0, f"tray exit={out.returncode} stderr={out.stderr[:300]}"
s = json.loads(out.stdout)

required = {
    "licensed", "license_state", "license_reason", "license_path", "machine_hash",
    "plugin_online", "mcp_running", "review_busy", "workspace_root", "rules_dir",
    "rules_pack_found", "settings_path", "plugin_log_path", "runs_root", "mcp_exe",
    "rules_state_path", "rules_disabled_count", "rules_overrides_count", "rules_state_latest",
}
missing = required - set(s)
assert not missing, f"status-json 缺字段: {missing}"
assert s["license_state"] in {
    "valid", "not_activated", "license_malformed", "license_invalid_signature",
    "license_seats_invalid", "license_not_yet_valid", "license_expired",
    "license_machine_mismatch", "license_invalid",
}, s["license_state"]
assert isinstance(s["licensed"], bool) and (s["licensed"] == (s["license_state"] == "valid"))
assert s["machine_hash"], "机器指纹为空"
# 授权有效时必须有到期日与剩余天数
if s["licensed"]:
    assert s["not_after"] and isinstance(s.get("days_left"), int)

if expect_online is not None:
    assert s["plugin_online"] == expect_online, \
        f"plugin_online={s['plugin_online']} 期望 {expect_online}"

print("T1 status-json 形状/状态码一致 | licensed={} state={} days_left={}".format(
    s["licensed"], s["license_state"], s.get("days_left")))
print("T2 plugin_online={} mcp_running={} review_busy={}".format(
    s["plugin_online"], s["mcp_running"], s["review_busy"]))
print("T3 rules_pack_found={} rules_dir={}".format(s["rules_pack_found"], s["rules_dir"]))
assert isinstance(s["rules_disabled_count"], int) and isinstance(s["rules_overrides_count"], int)
assert s["rules_state_path"], "rules_state_path 为空"

# T4 第十三片：用户态覆盖层文件被状态探针读取（临时 state，绝不碰真机默认路径）
fd, state_path = tempfile.mkstemp(prefix="nxa_tray_state_", suffix=".json")
os.close(fd)
try:
    with open(state_path, "w", encoding="utf-8") as f:
        json.dump({
            "disabled": ["HOLE-DIA-001"],
            "data": {"fillet_series.json": {"radius_series": [0.5, 1.0]}},
            "log": [{"t": "2026-09-22T10:00:00Z", "action": "disable",
                     "detail": "HOLE-DIA-001 圆孔直径标准系列"}],
        }, f)
    env = dict(os.environ, NXA_RULES_STATE=state_path)
    out2 = subprocess.run([exe, "--status-json"], capture_output=True,
                          text=True, encoding="utf-8", timeout=30, env=env)
    assert out2.returncode == 0, f"tray(env) exit={out2.returncode}"
    s2 = json.loads(out2.stdout)
    assert s2["rules_state_path"] == state_path, s2["rules_state_path"]
    assert s2["rules_disabled_count"] == 1, s2
    assert s2["rules_overrides_count"] == 1, s2
    assert "disable" in s2["rules_state_latest"] and "HOLE-DIA-001" in s2["rules_state_latest"], \
        s2["rules_state_latest"]
    print("T4 rules_state 覆盖层读取 | disabled={} overrides={} latest={}".format(
        s2["rules_disabled_count"], s2["rules_overrides_count"], s2["rules_state_latest"]))
finally:
    try:
        os.unlink(state_path)
    except OSError:
        pass

print("SMOKE TRAY PASS")
