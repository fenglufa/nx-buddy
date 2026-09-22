# 托盘冒烟：无头模式 --status-json 的形状与本机一致性检查（不需要 NX 在跑）。
# 用法: python build/smoke_tray.py <dist/tray/NxAssistant.exe> [--expect-plugin-online true|false]
import json
import subprocess
import sys

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
print("SMOKE TRAY PASS")
