#!/usr/bin/env python3
"""授权端到端冒烟：用测试私钥签发 .lic -> 宿主 license_status 真验签 -> 断言授权有效。

用法:
  python build/smoke_license.py <nxa-keygen.dll> <NxAssistant.Mcp.exe> <test_private.pem> <test_public.pem>

依赖测试密钥夹具（licensing/testdata/）。真正签发私钥永不入库、不用于此。
"""
import base64
import json
import os
import subprocess
import sys
import tempfile


def run(dotnet_args, **kw):
    return subprocess.run(["dotnet"] + dotnet_args, text=True, encoding="utf-8",
                          errors="replace", capture_output=True, **kw)


def main():
    keygen, host_exe, priv, pub = sys.argv[1:5]
    work = tempfile.mkdtemp(prefix="nxa_lic_smoke_")
    lic = os.path.join(work, "smoke.lic")
    ledger = os.path.join(work, "ledger.jsonl")

    env = dict(os.environ, NXA_KEYGEN_PASSWORD="dev-only-not-for-release")
    r = run([keygen, "issue", "--private", priv, "--customer", "冒烟测试", "--days", "30",
             "--features", "build,review", "--out", lic, "--ledger", ledger], env=env)
    if r.returncode != 0:
        print("issue FAILED:", r.stdout, r.stderr)
        return 1

    # 篡改检测：改一个 base64 字符应被拒签
    tampered = lic + ".tamper"
    lines = open(lic, encoding="utf-8").read().split("\n")
    assert lines[1].startswith("payload ")
    b = bytearray(base64.b64decode(lines[1].split(" ", 1)[1]))
    b[0] ^= 0x01
    lines[1] = "payload " + base64.b64encode(bytes(b)).decode()
    open(tampered, "w", encoding="utf-8").write("\n".join(lines))
    rt = run([keygen, "verify", "--lic", tampered, "--public", pub])
    assert rt.returncode != 0, "篡改后仍验签通过 —— 不可接受"
    print("tamper rejected OK")

    # 宿主 license_status：置 NXA_LICENSE_PATH 指向刚签发的 .lic
    proc = subprocess.Popen([host_exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                             stderr=subprocess.PIPE, text=True, encoding="utf-8", bufsize=1,
                             env=dict(os.environ, NXA_LICENSE_PATH=lic))

    def send(o):
        proc.stdin.write(json.dumps(o, ensure_ascii=False) + "\n")
        proc.stdin.flush()

    def rd():
        return json.loads(proc.stdout.readline())

    send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2024-11-05", "capabilities": {},
        "clientInfo": {"name": "smoke", "version": "0"}}})
    assert rd().get("id") == 1
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    send({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
          "params": {"name": "license_status", "arguments": {}}})
    resp = rd()
    text = resp["result"]["content"][0]["text"]
    status = json.loads(text)
    print("license_status:", json.dumps(status, ensure_ascii=False))
    assert status["licensed"] is True, f"应授权有效，实际 {status}"
    assert status["customer"] == "冒烟测试", status
    proc.terminate()
    print("LICENSE SMOKE PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
