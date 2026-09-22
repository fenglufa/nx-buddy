# Inno 壳端到端冒烟（不需要 NX 在跑；UGII_USER_DIR 指向临时假目录，绝不碰真机）。
# 前置：build/make_installer.ps1 已编出 dist\installer\NXAssistant-Setup-*.exe。
# 用法: python build/smoke_iss.py
# 覆盖：/VERYSILENT 安装（文件落位 + HKCU Run + 安装尾声自动走 deploy_plugin.ps1）
#       → 安装态托盘探测 → unins000 /VERYSILENT 卸载（目录/Run 值/同名 startup 插件清空）
#       → 清单外的第三方 startup DLL 必须存活（iss 卸载代码只按名删）。
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
setup = sorted(glob.glob(os.path.join(root, "dist", "installer", "NXAssistant-Setup-*.exe")))
assert setup, "未找到安装包，先跑 build/make_installer.ps1"
setup = setup[-1]

tmp = tempfile.mkdtemp(prefix="nxa_iss_")
target = os.path.join(tmp, "Programs", "NXAssistant")
fake_ugii = os.path.join(tmp, "fake_ugii")
startup = os.path.join(fake_ugii, "startup")
os.makedirs(startup)
# 第三方 DLL 放题眼：卸载只按 {app}\nx_plugin 名单删，它必须存活
dummy = os.path.join(startup, "zz_third_party.dll")
open(dummy, "wb").write(b"not ours")

run_key = "HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run"
ps = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass"]
env = dict(os.environ, UGII_USER_DIR=fake_ugii)


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

    # T1 静默安装：插件部署已改为尾声无条件执行（环境里有 UGII_USER_DIR 即部署）
    r = subprocess.run([setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                        f"/DIR={target}"],
                       env=env, timeout=600)
    assert r.returncode == 0, f"setup 退出码 {r.returncode}"
    plugin_dlls = []
    src_plugin = os.path.join(root, "dist", "nx_plugin")
    for f in os.listdir(src_plugin):
        if f.lower().endswith(".dll"):
            plugin_dlls.append(f)
    assert plugin_dlls, "dist\\nx_plugin 里没有 DLL？先 build"
    for rel in ["mcp/NxAssistant.Mcp.exe", "mcp/company_v3/pack.json",
                "tray/NxAssistant.exe", "nx_plugin/NxAssistant_NxPlugin.dll",
                "installer/install.ps1", "installer/uninstall.ps1",
                "installer/deploy_plugin.ps1"]:
        assert exists(os.path.join(target, *rel.split("/"))), f"缺 {rel}"
    print(f"T1 静默安装：文件落位（{len(plugin_dlls)} 个插件 DLL 随装），setup rc=0")

    # T2 自启登记 + startup 部署（含 deploy_plugin.ps1 复制过去的非 DLL 依赖）
    v = reg_get("NXAssistant").strip('"')
    assert os.path.normpath(v) == os.path.normpath(
        os.path.join(target, "tray", "NxAssistant.exe")), f"Run 值不符: {v}"
    for f in plugin_dlls:
        assert exists(os.path.join(startup, f)), f"startup 缺 {f}"
    assert exists(dummy), "deploy 阶段误动了清单外文件"
    print("T2 静默安装：HKCU Run 登记正确 + startup 插件全量部署 + 第三方 DLL 未动")

    # T3 安装态托盘探测：规则包解析到随装 company_v3（独立安装态闭环）
    r = subprocess.run([os.path.join(target, "tray", "NxAssistant.exe"), "--status-json"],
                       capture_output=True, text=True, encoding="utf-8", timeout=60)
    s = json.loads(r.stdout)
    assert s["rules_pack_found"] and os.path.normpath(s["rules_dir"]) == \
        os.path.normpath(os.path.join(target, "mcp", "company_v3")), \
        f"安装态规则包未被探测: {s['rules_dir']}"
    print("T3 安装态托盘：rules_dir 解析到随装 company_v3")

    # T4 静默卸载：unins000 会自复制到临时目录后台执行，轮询等待收敛
    r = subprocess.run([os.path.join(target, "unins000.exe"),
                        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"],
                       env=env, timeout=120)
    assert r.returncode in (0, 1), f"unins000 rc={r.returncode}"  # 1=成功入队
    deadline = time.time() + 180
    while exists(target) or reg_get("NXAssistant") or any(exists(os.path.join(startup, f)) for f in plugin_dlls):
        if time.time() > deadline:
            break
        time.sleep(2)
    assert not exists(target), "安装目录未删净"
    assert not reg_get("NXAssistant"), "Run 值未移除（uninsdeletevalue 未生效）"
    for f in plugin_dlls:
        assert not exists(os.path.join(startup, f)), f"startup {f} 未清理"
    assert exists(dummy), "卸载误删清单外第三方 DLL——按名删除逻辑失守"
    print("T4 静默卸载：目录/Run 值/startup 同名插件全清，第三方 DLL 存活")
    print("SMOKE ISS PASS")
finally:
    shutil.rmtree(tmp, ignore_errors=True)
