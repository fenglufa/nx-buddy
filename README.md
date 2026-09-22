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
# 规则引擎金样（离线，不需要 NX）：
dotnet run --project tests/NxAssistant.Rules.Tests
```

## 当前进度

已完成：三项目解决方案跑通编译；`ping` 一条链的**协议脊柱**（宿主 stdio→IPC→插件 handler）；
stdio 冒烟通过（`tools/list` 暴露 `ping`、`license_status`）。
**离线授权批次**：`licensing/` KeyGen 签发工具 + 共享 `NxAssistant.Licensing` 验签库（ECDSA-P256、载荷规范化、一 Key 一机、首激活锁机、seats=1、验期）；
宿主 `license_status` 真验签、`LICENSE_INVALID` 闸门就绪；端到端冒烟 `build/smoke_license.py` 通过（含篡改必拒）。
**规则引擎批次**：`NxAssistant.Rules` 加载 company_v3 主包 + 华恒子包（合并 28 条规则），消费"证据"模型产出 findings（standard/severity/enforcement/建议/占位标记）；
写前拦截与审图共用；金样 `dotnet run --project tests/NxAssistant.Rules.Tests`（含 φ13.2→FAIL+建议 13/14）全绿。
**测试样件**：`test/drawings/` 用 NX 2412 实机造出 5 个 `.prt` 金样（合规板、φ13.2 违规、4×重复孔、华恒销轴/传感器、空图），附预期 findings 对照表，保留不删。
**读/写工具批次（第一片）**：插件侧 `MainThread`（隐藏 WinForms Control 把 NXOpen 调用 marshal 回主线程）、`AssemblyResolve` 钩子（让 ugraf AppDomain 解析同目录依赖）、工作区沙箱 `Workspace`；已接线 5 方法：`ping`/`get_part_summary`/`inspect_work_part_geometry`/`save_work_part`/`create_part`（后者受 `LICENSE_INVALID` 闸门保护）；`build/deploy_plugin.ps1` 复制产物到 startup（不删既有 DLL）。**已过 NX 2412 实机全链路**（`build/smoke_live.py`：ping→无件报错→无授权拦截→带授权 create/summary/inspect/save 闭环落盘）。

待办（按 `tool-migration-v1.md` §5 分批）：
- 读/写工具批次（续）：部署插件 + 重启 NX 实机联调本切片；再补 rebuild/inspect_feature/topology/stable_id 与写操作（create_block/extrude/hole…含写前规则守卫）。
- 审图长任务：`review_folder`/`review_status`/`review_findings`（run_id 状态机 + xlsx 报告，xlsx 用 ClosedXML）；把规则引擎接上 NX 证据抽取，用 `test/drawings` 验收。
- 工程图类样件（图框/标题栏/图层）：待客户确认真实图框 id 后补 2D 图纸样件。

## 已知约束 / 红线

- stdio 传输：stdout 只走 JSON-RPC，诊断一律进 stderr/文件（已在宿主 `ClearProviders`）。
- **插件线程亲和**：NXOpen 对主线程有亲和性。管道 worker 只做收发，所有 handler 经 `NxPlugin/MainThread.cs` 的
  隐藏 WinForms Control `BeginInvoke` marshal 回 NX 主线程执行（超时 120s 明确报错，不盲目重试）。
- 部署插件进 `%UGII_USER_DIR%\startup`：旧 NX-MCP 桥已摘除（备份在 `E:\NX-MCP\nx_user\startup_backup\`，实机验证通过后待删）。NX 运行中 startup DLL 会被锁定，`deploy_plugin.ps1` 会自动暂存到 `dist\nx_plugin_staged` 并在下次 NX 关闭后重跑生效。
- Windows PowerShell 5.1 按 GBK 读无 BOM 的 UTF-8 `.ps1`，中文注释会吞行：**所有 .ps1 必须带 UTF-8 BOM 保存**。
- 目标框架锁定 NX 2412；占位规则数值不得写死进 C#。
