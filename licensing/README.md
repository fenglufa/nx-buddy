# licensing/ — 离线授权签发（产品方工具，永不进客户包）

对应 PRD §8。完全离线、一 Key 一机、私钥口令保护。

## 组成

- `NxAssistant.KeyGen/`：签发命令行工具 `nxa-keygen`（net8.0）。命令：
  - `genkey --out <private.pem>`：生成口令保护的 P-256 私钥，打印公钥 PEM。
    公钥写入 `../src/NxAssistant.Licensing/Keys/trusted_public.pem` 后重构建宿主即完成"换信任根"。
    私钥口令读环境变量 `NXA_KEYGEN_PASSWORD`，无则交互输入。
  - `issue --private <pem> --customer <名> (--months N | --days N | --not-after yyyy-MM-dd)
     [--features a,b] [--machine-hash H] [--standard-pack P] --out <lic> [--ledger <jsonl>]`
    签发 `.lic`。不给 `--machine-hash` 即发"未绑机 Key"，客户端首次激活锁机（方案 B）。
  - `verify --lic <lic> --public <pem> [--ledger <jsonl>]`：离线核验（客户可用）。
  - `revoke --lic-id <ID> --reason <文本> [--ledger <jsonl>]`：台账登记作废。
- `testdata/`：**仅供测试/冒烟**的密钥夹具（`test_private.pem`/`test_public.pem`）。
  宿主内嵌的 `trusted_public.pem` 当前即此测试公钥，便于 CI 端到端验证。
  ⚠️ 正式发布前必须 `genkey` 生成真实私钥、替换内嵌公钥；真实私钥存 `keys/`（已 gitignore），永不入库、永不随包分发。
- `keys/`：真实签发私钥目录（gitignore，不入库）。
- `ledger.jsonl`：签发台账（编号/客户/期限/绑机/作废事件，追加式）。

## 校验链（客户端 `NxAssistant.Licensing.LicenseManager`）

验签（内嵌公钥） → 验 seats=1 → 验期（含当日） → 验指纹：
Key 带 `machine_hash` 则必须等于本机；为空则查/写 `activation.json` 首激活锁机，
已锁到别的机器即拒。全通过 → 缓存（宿主侧 10 分钟）。

授权文件位置：环境变量 `NXA_LICENSE_PATH`，否则 `%LOCALAPPDATA%\NXAssistant\license.lic`。

## 冒烟

```
python build/smoke_license.py <nxa-keygen.dll> <NxAssistant.Mcp.exe> \
  licensing/testdata/test_private.pem licensing/testdata/test_public.pem
```
签发→篡改必拒→宿主 `license_status` 真验签=有效。
