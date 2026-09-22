# NX 小助手 (nx-buddy)

面向 **Siemens NX 2412** 的可交付合规设计助手：把公司图纸/模型标准（company_v3 规则包，含华恒子包）
变成**人和 AI Agent 共用同一套**的可执行闸门与可审计操作入口。全链路本机通信，不出网。

产品定义见 [`docs/prd-01.md`](docs/prd-01.md)，审图长任务模型见 [`docs/prd-02.md`](docs/prd-02.md)，
工具移植蓝图见 [`docs/tool-migration-v1.md`](docs/tool-migration-v1.md)，逐批开发进度见 [`docs/progress-v1.md`](docs/progress-v1.md)。

它能做什么：

- **33 个 MCP 工具**：建模与编辑（块/参数化草图/拉伸/圆柱孔/圆角/倒角/表达式改参/撤销助手变更）、
  拓扑定位（stable_id 几何指纹，存盘回开仍可重定位）、STEP 导入导出、
  读回与抽取（部件摘要/拓扑/特征/草图/图层/工程图注释/标题栏/板厚/孔组等）。
- **写前守卫**：每一次受守卫的写操作在**转发给 NX 之前**先过规则引擎——
  不合规尺寸直接拒绝并给出邻近标准值建议（如 φ13.2 → 建议 13/14）；规则包缺失时 fail-closed。
- **审图长任务**：`review_folder` 批量逐张判定，产出 `审图报告.xlsx` + 机器可读 `summary.json`，
  支持中断续跑/取消，全程不扰动用户当前工作部件。
- **可回退**：`undo_last_assistant_change` 用"具名 undo mark + 模型指纹"双校验，绝不误撤用户自己的操作。

## 架构（三组件，本机 IPC 解耦）

```
Agent ──stdio──► NxAssistant.Mcp (net8, 官方 ModelContextProtocol)   ← MCP 宿主
                     │  命名管道 "nxassistant-mcp"：{id,method,params[,token]} / {id,ok,result|error}
                     ▼
                 NxAssistant.NxPlugin (net48, NXOpen) ──► NX 2412 (ugraf.exe 进程内)
                     ▲
NxAssistant.exe 托盘 ─┘（只读状态探测 + 设置窗，与宿主分离）
```

| 项目 | 交付物 | 框架 | 职责 |
|---|---|---|---|
| `src/NxAssistant.Mcp` | `NxAssistant.Mcp.exe` | net8 | MCP 宿主：参数校验 / 授权闸门 / 规则引擎 / 审图编排 / 报告，经 IPC 调插件 |
| `src/NxAssistant.NxPlugin` | `NxAssistant_NxPlugin.dll` | **net48** | 唯一直接调用 NXOpen 的层，随 NX 从 `%UGII_USER_DIR%\startup` 加载；NXOpen 是 .NET Framework 4.8 程序集，必须跑在 ugraf.exe 进程内 |
| `src/NxAssistant.Tray` | `NxAssistant.exe` | net8-windows | 托盘 + 设置窗（PRD §6）：授权/NX 连接/MCP 在跑/审图忙碌四态，`--status-json` 无头采集 |
| `src/NxAssistant.Core` | 类库 | netstandard2.0 | IPC 契约 + `NxaSettings` 统一路径解析（下见） |
| `src/NxAssistant.Licensing` / `Rules` | 类库 | netstandard2.0 | ECDSA-P256 离线验签 / company_v3 规则引擎 |
| `licensing/` | `nxa-keygen` | — | 签发工具（仅产品方持有，私钥不进客户包） |

**配置解析优先级**（三组件共用同一实现，FILE-001 沙箱根写侧/校验侧不分叉）：

```
环境变量  >  %LOCALAPPDATA%\NXAssistant\settings.json  >  默认值
NXA_IPC_TOKEN(token 不共享)   NXA_WORKSPACE(workspace)   NXA_RULES_DIR(rules_dir)   NXA_LICENSE_PATH(license_path)
```

`settings.json` 由托盘"设置"分页写入，宿主/插件下次解析即重读（热改）。
IPC 令牌：`NXA_IPC_TOKEN` 非空时报文须携带匹配 token，挡本机其它进程误连。

## 环境要求

| | 客户机（运行） | 开发机（源码构建） |
|---|---|---|
| 必需 | Windows x64；NX 2412；`%UGII_USER_DIR%` 已定义；**.NET 8 Base Runtime + Windows Desktop Runtime**（当前发布为 framework-dependent，托盘是 WinForms） | .NET 8 SDK；Python 3（冒烟脚本）；NX 2412（仅实机冒烟需要） |
| 可选 | — | Inno Setup 6（仅出安装包需要）：`winget install JRSoftware.InnoSetup` |

## 构建（开发机）

```powershell
# 本机 .NET 8 SDK 在用户目录：
& "$env:USERPROFILE\.dotnet\dotnet.exe" build src/NxAssistant.sln -c Debug

pwsh build/build.ps1        # Release 发布三组件到 dist/（mcp/、tray/、nx_plugin/；不自动碰 %UGII_USER_DIR%）

# 出安装包（需 Inno Setup 6；产物 dist\installer\NXAssistant-Setup-<版本>.exe）：
pwsh build/make_installer.ps1            # -Pfx <证书> -PfxPass <口令> 为代码签名预留钩子
```

开发期把插件部署进 NX（改代码后重复执行，需重启 NX 生效；NX 运行中 DLL 被锁时自动暂存，关 NX 后重跑）：

```powershell
pwsh build/deploy_plugin.ps1             # dist\nx_plugin → %UGII_USER_DIR%\startup（只复制、绝不删他人文件）
```

## 安装（客户机）

两条等价路径，装出来的布局、自启、startup 部署完全一致（同一套语义，冒烟脚本分别覆盖）。

### A. 双击安装包（推荐）

运行 `NXAssistant-Setup-<版本>.exe`：向导中选装组件，若机器上有 `%UGII_USER_DIR%` 会出现
"把 NX 插件合并部署进 startup"任务（勾选即随装部署，之后重启 NX 加载）。
装完可选立即启动托盘，并在 `HKCU\...\Run` 登记自启。

静默/批量推送（IT 友好）：

```powershell
NXAssistant-Setup-1.0.0.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=deployplugin
# /DIR="..." 可改安装位置；deployplugin 任务要求进程环境里有 UGII_USER_DIR
```

### B. 脚本安装器（无安装包的内部/远程场景）

```powershell
pwsh build/installer/install.ps1       # 同上布局；pwsh -File 亦可（Windows PowerShell 5.1 可用）
pwsh build/installer/uninstall.ps1     # 按 install.json 清单卸载；-RemovePluginFiles 连 startup 插件一并清
```

### 装完之后机器上有什么

```
%LOCALAPPDATA%\Programs\NXAssistant\
├─ mcp\        宿主 + company_v3\ 规则包（必须随宿主同目录——独立安装态下这是规则解析链唯一可靠落点，缺则 fail-closed）
├─ tray\       托盘 NxAssistant.exe（HKCU Run 自启）
├─ nx_plugin\  插件 DLL（startup 部署的名单来源）
└─ installer\  install/uninstall/deploy_plugin.ps1 随装副本（脱离仓库可维护）
%UGII_USER_DIR%\startup\               插件 DLL 合并复制（不删他人文件）
%LOCALAPPDATA%\NXAssistant\            用户数据：settings.json / license.lic / runs\ / workspace\
```

**卸载**（控制面板或 `unins000.exe /VERYSILENT`）：删除安装目录 + 撤销 Run 自启 +
只删"安装清单里出现过、且 startup 同名"的插件文件——他人文件与用户数据（settings/license/runs/workspace）一律不动。

### 首次使用

1. **授权激活**：产品方用 `licensing/` KeyGen 按客户机指纹签发 `.lic`（一 Key 一机、首激活锁机）；
   放到 `%LOCALAPPDATA%\NXAssistant\license.lic`，或托盘右键"导入授权 Key"。过期/篡改一律 `LICENSE_INVALID`。
2. **接入 Agent**：托盘 →"复制 MCP 配置片段"，粘进 MCP 客户端（Qoder/Claude 等）：

   ```json
   { "mcpServers": { "nx-buddy": { "command": "C:\\Users\\<用户>\\AppData\\Local\\Programs\\NXAssistant\\mcp\\NxAssistant.Mcp.exe" } } }
   ```

3. **验证**：对 Agent 说"在 NX 里建一个 40×25 的块"或看托盘四态——NX 连接一栏变绿即链路通。

## 验证与冒烟（不改真机状态的自检）

```powershell
python build/smoke_stdio.py dist/mcp/NxAssistant.Mcp.exe   # tools/list 齐 33 工具 + schema/描述完整（不需要 NX 在跑）
python build/smoke_license.py <keygen.dll> <host.exe> <私钥> <公钥>   # 授权端到端（签发→锁机→篡改必拒）
dotnet run --project tests/NxAssistant.Rules.Tests         # 规则引擎离线金样（φ13.2→FAIL+建议 13/14）
python build/smoke_tray.py                                 # 托盘 --status-json 形状 + settings.json 往返
python build/smoke_installer.py                            # 脚本安装器装卸闭环（假 UGII 目录，不碰真机）
python build/smoke_iss.py                                  # Inno 安装包静默装卸（假 UGII 目录 + 第三方 DLL 存活断言）
```

需要 NX 2412 在跑（实机全链路，阶段 A–K）：

```powershell
pwsh build/deploy_plugin.ps1; 重启 NX
$env:NXA_LICENSE_PATH="..."; python build/smoke_live.py dist/mcp/NxAssistant.Mcp.exe
```

验收金样：`test/drawings/` 5 个 `.prt`（合规板、φ13.2 违规、4×重复孔、华恒销轴/传感器、空图）+ 预期 findings 对照表，**保留不删**。

## 当前状态（2026-09-22）

V1 交付面已全部落地并实机验证：33 工具白名单（第十片齐）、审图长任务（第九片）、
托盘 + 共享配置层（第十一片）、脚本安装器 + Inno Setup 壳双路径（第十二片）——
`smoke_live` A–K 全绿、安装包静默装卸端到端全绿。
余下工作全部登记在 [`docs/progress-v1.md`](docs/progress-v1.md) 待办节：
客户 2D 样件与华恒数值表到位后把占位/负路径转正、代码签名（证书暂缓采购）、
framework-dependent 发布改自包含或文档化运行时前置、安装向导人工观感验收。

## 已知约束 / 红线

- stdio 传输：stdout 只走 JSON-RPC，诊断一律进 stderr/文件（已在宿主 `ClearProviders`）。
- **插件线程亲和**：NXOpen 对主线程有亲和性。管道 worker 只做收发，所有 handler 经 `NxPlugin/MainThread.cs` 的
  隐藏 WinForms Control `BeginInvoke` marshal 回 NX 主线程执行（超时 120s 明确报错，不盲目重试）。
  **任何异常不得从 BeginInvoke 回调逃逸**：.NET Framework WinForms 会把回调异常额外送进主线程
  `Application.OnThreadException`，开着 JIT 调试的机器上每错一次弹一次 NX 对话框；因此闭包内捕获异常、
  回到 worker 线程后用 `ExceptionDispatchInfo` 原样重抛（`build/verify_no_dialog.py` 实机复验无弹窗）。
- 部署插件进 `%UGII_USER_DIR%\startup`：旧 NX-MCP 桥已从 startup 摘除，`startup\` 现仅含 NxAssistant 插件及其依赖。
  后续切换：把 Qoder `siemens_nx` MCP 配置指向 `E:\nx-buddy\dist\mcp\NxAssistant.Mcp.exe`。
  NX 运行中 startup DLL 会被锁定，`deploy_plugin.ps1` 会自动暂存到 `dist\nx_plugin_staged` 并在下次 NX 关闭后重跑生效。
- Windows PowerShell 5.1 按 GBK 读无 BOM 的 UTF-8 `.ps1`，中文注释会吞行：**所有 .ps1 必须带 UTF-8 BOM 保存**。
  同理 Inno 的 `.iss` 也必须 UTF-8 BOM，否则中文向导文案按 ANSI 码页编译成乱码（静默冒烟看不出来）。
- 目标框架锁定 NX 2412；占位规则数值不得写死进 C#。
