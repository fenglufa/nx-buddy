# 安装包端到端冒烟（不需要 NX 在跑；startup 部署指向临时假 UGII 目录，绝不碰真机）。
# 用法: python build/smoke_installer.py
# 覆盖：install.ps1（三组件+规则包+HKCU Run+清单）→ 安装态自检（stdio 33 工具 / 托盘探测 / 插件 DLL 落假 startup）
#       → uninstall.ps1 -RemovePluginFiles（目录/Run 值/插件文件全清）→ 用户数据保留 → 防呆拒删。
import json
import os
import shutil
import subprocess
import sys
import tempfile

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
dist = os.path.join(root, "dist")
ps = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass"]
tmp = tempfile.mkdtemp(prefix="nxa_inst_")
target = os.path.join(tmp, "Programs", "NXAssistant")
fake_ugii = os.path.join(tmp, "fake_ugii")
startup = os.path.join(fake_ugii, "startup")
run_key = "HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"


def run_ps(script, *args, expect_fail=False):
    cmd = ps + ["-File", script] + list(args)
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=300)
    if expect_fail:
        assert r.returncode != 0 or "拒绝" in (r.stdout + r.stderr), \
            f"应拒但未拒: {cmd}\n{r.stdout}\n{r.stderr}"
    else:
        assert r.returncode == 0, f"脚本失败 rc={r.returncode}: {cmd}\n{r.stdout}\n{r.stderr}"
    return r.stdout


def reg_get(name):
    r = subprocess.run(ps + ["-Command",
        f"(Get-ItemProperty -Path '{run_key}' -Name '{name}' -ErrorAction SilentlyContinue).'{name}'"],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (r.stdout or "").strip()


def exists(p):
    return os.path.exists(p)


try:
    # T0 前置：真机若已有同名自启值，本脚本会覆盖，直接拒绝跑
    if reg_get("NXAssistant"):
        print("ABORT: HKCU Run\\NXAssistant 已存在（真装过？），冒烟不覆盖它", file=sys.stderr)
        sys.exit(2)

    out = run_ps(os.path.join(root, "build", "installer", "install.ps1"),
                 "-TargetDir", target, "-UgiiUserDir", fake_ugii)
    for rel in ["mcp/NxAssistant.Mcp.exe", "mcp/company_v3/pack.json",
                "tray/NxAssistant.exe", "nx_plugin/NxAssistant_NxPlugin.dll",
                "install.json", "installer/uninstall.ps1", "installer/deploy_plugin.ps1"]:
        assert exists(os.path.join(target, *rel.split("/"))), f"缺 {rel}"
    print("T1 install：三组件+规则包+脚本随装 全部落位")

    assert reg_get("NXAssistant") == f'"{os.path.join(target, "tray", "NxAssistant.exe")}"', "Run 值不符"
    assert exists(os.path.join(startup, "NxAssistant_NxPlugin.dll")), "插件未进（假）startup"
    print("T2 install：HKCU Run 自启登记 + startup 插件部署（假 UGII 目录）")

    # 安装态宿主：stdio 33 工具照跑（用已随装的规则包）
    r = subprocess.run([sys.executable, os.path.join(root, "build", "smoke_stdio.py"),
                        os.path.join(target, "mcp", "NxAssistant.Mcp.exe")],
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
    assert "SMOKE PASS" in r.stdout, r.stdout + r.stderr
    print("T3 安装态宿主：smoke_stdio 通过")

    r = subprocess.run([os.path.join(target, "tray", "NxAssistant.exe"), "--status-json"],
                       capture_output=True, text=True, encoding="utf-8", timeout=60)
    s = json.loads(r.stdout)
    assert s["rules_pack_found"] and os.path.normpath(s["rules_dir"]) == \
        os.path.normpath(os.path.join(target, "mcp", "company_v3")), \
        f"安装态规则包未被探测: {s['rules_dir']}"
    print("T4 安装态托盘：rules_dir 解析到随装 company_v3（独立安装态闭环）")

    out = run_ps(os.path.join(root, "build", "installer", "uninstall.ps1"),
                 "-TargetDir", target, "-UgiiUserDir", fake_ugii, "-RemovePluginFiles")
    assert not exists(target), "安装目录未删净"
    assert not reg_get("NXAssistant"), "Run 值未移除"
    assert not exists(os.path.join(startup, "NxAssistant_NxPlugin.dll")), "startup 插件未清理"
    print("T5 uninstall：目录/Run 值/startup 插件文件全清")

    # 防呆：对无清单目录（临时根）执行卸载必须拒
    run_ps(os.path.join(root, "build", "installer", "uninstall.ps1"),
           "-TargetDir", tmp, expect_fail=True)
    assert exists(fake_ugii), "拒删场景不应动任何东西"
    print("T6 防呆：无 install.json 的目录拒删，未误伤")
    print("SMOKE INSTALLER PASS")
finally:
    # 兜底清理（正常路径 T5/T6 后 fake_ugii 仍在，用户数据场景无需保留）
    shutil.rmtree(tmp, ignore_errors=True)
