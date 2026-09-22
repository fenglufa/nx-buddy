# NX 小助手 (nx-buddy)

面向 NX 2412 的可交付合规设计助手：给人和 Agent 同一套可执行的公司标准与可审计操作入口。
产品定义见 [`docs/prd-01.md`](docs/prd-01.md)，审图长任务模型见 [`docs/prd-02.md`](docs/prd-02.md)，
工具移植蓝图见 [`docs/tool-migration-v1.md`](docs/tool-migration-v1.md)，规则数据见 [`docs/company_v3/`](docs/company_v3/)。

## 架构（三组件，本机 IPC 解耦）

```
Agent ──stdio──► NxAssistant.Mcp (net8, 官方 ModelContextProtocol)
                     │  命名管道 {id,method,params} / {id,ok,result|error}
                     ▼
                 NxAssistant.NxPlugin (net48, NXOpen) ──► NX 2412 (ugraf.exe)
```

- `src/NxAssistant.Core` (netstandard2.0)：IPC 报文/信封/方法名等共享契约。
- `src/NxAssistant.NxPlugin` (net48)：唯一直接调用 NXOpen 的层，随 NX 启动加载，跑命名管道服务。
  **必须 net48**——NXOpen 是 .NET Framework 程序集，运行在 ugraf.exe 进程内。
- `src/NxAssistant.Mcp` (net8)：MCP 宿主，做参数校验 / 授权闸门 / 规则引擎 / 报告，并经 IPC 调插件。
- `licensing/`：离线 Key 签发工具（仅产品方，私钥不进客户包）。

## 构建

```powershell
# 本机用用户目录里的 .NET 8 SDK
& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/NxAssistant.sln -c Debug
pwsh build/build.ps1            # 发布到 dist/（不自动写 %UGII_USER_DIR%，部署需显式执行）
python build/smoke_stdio.py dist/mcp/NxAssistant.Mcp.exe   # stdio 冒烟：initialize + tools/list
# 授权端到端冒烟（用 licensing/testdata 测试密钥夹具）：
python build/smoke_license.py licensing/NxAssistant.KeyGen/bin/Debug/net8.0/nxa-keygen.dll \
  src/NxAssistant.Mcp/bin/Debug/net8.0/NxAssistant.Mcp.exe \
  licensing/testdata/test_private.pem licensing/testdata/test_public.pem
```

## 当前进度

已完成：三项目解决方案跑通编译；`ping` 一条链的**协议脊柱**（宿主 stdio→IPC→插件 handler）；
stdio 冒烟通过（`tools/list` 暴露 `ping`、`license_status`）。
**离线授权批次**：`licensing/` KeyGen 签发工具 + 共享 `NxAssistant.Licensing` 验签库（ECDSA-P256、载荷规范化、一 Key 一机、首激活锁机、seats=1、验期）；
宿主 `license_status` 真验签、`LICENSE_INVALID` 闸门就绪；端到端冒烟 `build/smoke_license.py` 通过（含篡改必拒）。

待办（按 `tool-migration-v1.md` §5 分批）：
- 读/写工具批次：移植 §9.1 各 `_op_` 到 C#（含写后回读护栏、stable_id、表达式字符串通道）。
- 规则引擎：读 company_v3 JSON，画图为防 / 审图为查同一套。
- 审图长任务：`review_folder`/`review_status`/`review_findings`（run_id 状态机 + xlsx 报告，xlsx 用 ClosedXML）。
- 测试图纸集：`test/` 造合规/违规样件（φ13.2 金样等），保留不删。

## 已知约束 / 红线

- stdio 传输：stdout 只走 JSON-RPC，诊断一律进 stderr/文件（已在宿主 `ClearProviders`）。
- **插件线程亲和**：骨架阶段管道在请求线程内直接调 NXOpen；接线 NX 联调前必须改为 marshal 到 NX 主线程
  （NXOpen 对主线程有亲和性）。见 `NxPlugin/PipeServer.cs` `// THREADING`。
- 部署插件进 `%UGII_USER_DIR%\startup`：客户已确认旧 NX-MCP 桥可删除、无需共存；但这一步需重启 NX，改动正在运行的 NX 前先确认。
- 目标框架锁定 NX 2412；占位规则数值不得写死进 C#。
