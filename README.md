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
- `src/NxAssistant.Tray` (net8-windows)：托盘 + 设置窗（交付名 `NxAssistant.exe`），与宿主分离的桌面进程；只读状态采集，路径类设置写 `settings.json`（三组件共用解析优先级）。
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
# 安装包骨架（用户级免 UAC；冒烟用假 UGII 目录，不碰真机 startup）：
python build/smoke_installer.py
pwsh build/installer/install.ps1      # 真装：%LOCALAPPDATA%\Programs\NXAssistant + 自启 + startup 合并部署
pwsh build/installer/uninstall.ps1    # 按 install.json 清单卸载；用户数据保留
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
**拓扑/特征批次（第二片）**：插件侧 `TopologyOps`（对齐 nx_remote_ops.py 的 stable_id 几何指纹：SHA1 规范化 JSON 前 20 位，save/回开重定位、失配几何回退、sort_by/occurrence/unique 语义）+ `inspect_body_topology`/`resolve_topology`/`inspect_feature`/`rebuild_work_part` 四方法宿主接线；PipeServer 断开类 IOException 不再刷日志。**已过 NX 2412 实机全绿**（smoke_live 阶段 C：金样板拓扑 8 面/16 边，stable_id 命中→回退命中同一最大面→报错路径→特征检查→重建 update_errors=0，日志 0 噪音）。
**写操作批次（第三片）**：插件侧 `WriteOps`——`create_block`（OriginAndEdgeLengths）、`create_parametric_sketch`（XY/XZ/YZ 平面 + line/rectangle/circle/arc + 自动矩形约束 + 尺寸独立表达式）、`inspect_sketch`、`extrude_sketch`（Section+Direction，失败自动 UndoToMark 回滚）、`set_feature_expression`（改 RHS→更新→失败回滚，old/new 对照）；宿主 5 工具全走 `LICENSE_INVALID` 闸门 + Guard。**实机写闭环全绿**（smoke_live 阶段 D：create_part→block→矩形草图 WIDTH=40→拉伸→表达式改 25 生效→rebuild→save，102KB 落盘）。
**写前守卫批次（第四片）**：`create_cylindrical_hole`（插件 `CreateCylinderBuilder` AxisDiameterAndHeight+Subtract，失败 UndoToMark 回滚；对齐旧桥语义）+ 宿主 `HostRules` 写前规则守卫：候选孔证据→`RuleEngine.Blocking`，失配系列直径（如 φ13.2）在 **转发给 NX 之前** 即返回 `RULE_BLOCKED`+邻近建议；规则包缺失时 fail-closed（`RULE_PACK_UNAVAILABLE`）。**实机全绿**（smoke_live 阶段 E：φ10 正常成孔、φ13.2 被拦且 feature_count 前后不变、save 落盘）。
**边特征批次（第五片）**：`fillet_edges`（`EdgeBlendBuilder`+`CreateRuleEdgeDumb`+`AddChainset(collector,radius)`；`edge_indices` 与 `inspect_body_topology` 边 index 同源，非空/去重/越界必错）与 `chamfer_edges`（`ChamferBuilder` EdgesAlongFaces+SymmetricOffsets 双偏置）；FEAT-FILLET-002 为 **warn 级守卫**：系列外半径（如 2.7）不拦截成孔，成功响应附带 `rule_warnings`+邻近建议。**实机全绿**（smoke_live 阶段 F：r=3 无告警、r=2.7 带 warn 建议 [2,3]、chamfer d=2+rebuild 0 错）。
**位移回读护栏批次（第六片）**：`move_object`（`CreateMoveBodyBuilder`+`CreateRuleBodyDumb`+`ModlMotion.DeltaXyz` 表达式 RHS 赋值，`Validate()` 前置校验）——提交后按 UF 包围盒回读实际位移，与请求分量差 >1e-3 即 `UndoToMark` 回滚并报"特征驱动实体请改特征参数"，把 Move Body 的**静默假成功**消灭在护栏内；这是 §1.2/§1.4 回读护栏的模板实现，后续写 op 对齐。**实机全绿**（smoke_live 阶段 G：零向量宿主直拒；block 与 extrude 两类特征驱动实体均实测"提交成功但没动"→ 护栏回滚、包围盒分毫不差、rebuild 0 错；非特征实体真移动正路径待 `import_exchange` 批次补测）。
**交换导出批次（第七片）**：`export_exchange`（`DexManager.CreateStepCreator`：ap203/ap214/ap242/ap242ED2、ExistingPart+精确实体、官方 translator `.def` 设置文件按 `UGII_BASE_DIR` 探测；PRD §9.1 限定 V1 仅 STEP，`format=parasolid` 明确拒绝）+ `Workspace.ResolveExchange` 把 FILE-001 沙箱扩展到 `.stp/.step`（`../` 逃逸/带路径文件名直拒，重名需 `overwrite=true`）；导出后回读文件存在且 size>0。**实机全绿**（smoke_live 阶段 H：逃逸与 parasolid 被拒、8132 字节 STEP 落盘且文件头含 ISO-10303、重复导出被拒后 overwrite 重导成功）。
**交换导入批次（第八片）**：`import_exchange`（ap203/214/242 `StepImporter`→`ImportTo=WorkPart`，默认缝合+简化；FILE-001 沙箱复用于输入侧"必须存在"校验；回读护栏=**实体增量>0 且 update_error_count==0 双条件**，否则 ok:false 交 Agent 判读）。与 `export_exchange` 打通后冒烟获得 STEP 往返闭环（导出件回导进自有工作部件）。**实机全绿**（smoke_live 阶段 I：缺文件直拒、+1 实体导入、save 落盘）。**同时修正一个假设**：实测 STEP 导入件仍带 `ImportedModel` 特征（feature_count +2），Move Body 对它同样 0 位移并被护栏回滚——"导入件=可移动的非特征实体"不成立；move_object 正路径需真正的静态体来源（Parasolid 通道/去参数化，均在 V1 白名单外），本工具价值定为"安全尝试+可解释回滚"。
**审图长任务批次（第九片）**：`review_folder`/`review_status`/`review_findings` 三工具 + 内部 IPC 方法 `extract_evidence`（插件对 FILE-001 沙箱内 `.prt` 非显示打开→抽 `Evidence.Part` 段（孔=减料圆柱/孔特征柱面直径·特征名前缀定 kind、实体/片体计数、圆角半径）→抽完即关并恢复原 Work part；同名部件已在会话时给出可解释的"本张记失败"）。宿主 `ReviewOrchestrator` 按 prd-02 契约实现：`review_folder` **立即返回 run_id**、后台串行逐张判定，状态与 findings 落 `%LOCALAPPDATA%\NXAssistant\runs\<run_id>\`（run.json + findings.jsonl），宿主重启后 `running` 滞留态自动判 `interrupted`；`review_status` 支持 `cancel=true`（当前张完成后生效）；`review_folder(resume_run_id)` 从下一张续跑；单张失败入 `failed_files` 整批继续（`completed_with_errors`）。报告 = ClosedXML（客户确认库）生成 `审图报告.xlsx`（摘要+明细，明细列对齐 §7.5 金样）+ `summary.json`。`Finding` 主题带孔特征名（`HoleEvidence.FeatureName`），写前守卫消息不受影响。**实机全绿**（smoke_live 阶段 J：沙箱外目录直拒；5 张金样 run_id 状态机走通；φ13.2→HOLE-DIA-001 阻断·建议 13/14·对象含 `NXA_HOLE_PH132`；dup4→HOLE-DUP-004 warn 列 4 特征名；pin/sensor→华恒子包阻断+警告且标 placeholder；空图→BODY-001 阻断；xlsx+summary 落盘；审图全程未扰动用户工作部件）。

**抽取类 + 撤销批次（第十片）**：§9.2 批 5 剩余 10 个工具落地，宿主共 **33 工具**。插件侧 `InspectOps` 新增 8 个抽取器：`inspect_layers`（按**图层号**统计，Part/LayerManager 与 UFLayer 均无反查图层名通道）、`inspect_drawing_annotations`（注释文本 + 尺寸实测值）、`inspect_title_block`（逐页原始属性全集 + `GetScale`）、`inspect_drawing_sheet`（图框/模板从候选属性名嗅探）、`inspect_weld_annotations`、`inspect_parts_list`、`inspect_sheet_thickness`（**启发**：实体包围盒最小边 `solid_bbox_min_dim`，非板类零件不权威）、`inspect_hole_pattern`（同径 + 归一化轴向分组 ≥3 且径向散布 <15% 判圆形孔组，PCD=2·平均半径）。`undo_last_assistant_change`（`UndoOps.AssistantMarks`）把 §1.2"绝不误撤用户操作"做成**双校验**：写成功时记 (undo mark id, 名, 模型指纹=特征名/表达式值/体名 SHA1)，撤销时要求 ① 最新可见标记确属助手（或 rebuild/move-regen no-op 白名单）② 当前指纹等于写入后快照，任一不满足即拒并给中文原因；账本进程内、按部件路径最多 8 条，NX 重启即清空。因 §9.3 把工程图**创建**推迟到 V1.1，图纸侧抽取器以负路径验收（无图纸页时 `drawing_sheet_count=0` 且 ok:true），客户 2D 样件到位后无需改契约即可转正；标题栏字段→canonical 名走 `docs/company_v3/title_block_fields.json` 占位别名表（placeholder:true，待客户校准），`RuleEngine.TitleField` 负责解析，审图 findings 行的"图号/版本"已从文件名改为读真实标题栏。`extract_evidence` 证据补 `part.plate_thickness`/`part.hole_patterns` 与整段 `drawing`（规则引擎对缺失段自然跳过，第九片审图判定零回归）。**实机全绿**（smoke_live 阶段 K：无账本直拒→建 block→撤销 body_count 归 0→建 block→rebuild→再撤销（证明 no-op mark 不阻断回退）→链撤销到底再拒；板厚启发=12 命中、图层分布 `{'1': 1}`、三图纸抽取器空证据正常；阶段 J 复跑判定不变）。

**托盘 + 共享配置批次（第十一片）**：补上三组件里最后的交付面——`NxAssistant.Tray`（net8-windows，产物名 `NxAssistant.exe`，PRD §6 的"托盘 + 设置窗"）。`StatusProbe` 只读采集四类状态：授权（`LicenseManager.Check(path)`）、NX 连接（枚举 `\\.\pipe\` 命名空间判管道名在位，**不下 IPC 请求**——宿主是逐请求短连接，托盘若发 ping 会和真实请求抢单 worker 队列）、MCP 在跑（进程表）、审图忙碌（扫 `runs\*\run.json` 有无 `running`）。新增 `NxAssistant.Core/NxaSettings`：三组件统一"环境变量 > `settings.json` > 默认"的路径解析优先级（`NXA_WORKSPACE`/`NXA_RULES_DIR`/`NXA_LICENSE_PATH` 各对应 `workspace`/`rules_dir`/`license_path` 键）——插件 `Workspace.Root`、宿主 `HostRules`/`ReviewOrchestrator`/`LicenseService` 全部改走同一 `Resolve`，确保 FILE-001 沙箱根在写入侧与校验侧不再分叉；`settings.json` 由托盘"设置"分页写、宿主/插件下次解析即重读（热改）。托盘另提供 `--status-json` 无头模式（状态与 UI 解耦，供 `build/smoke_tray.py` 与技术支持一键采集）、右键"导入授权 Key"（复制到授权位并重探）、可复制的 MCP 配置片段（`command` 指向宿主绝对路径）、打开插件日志/审图报告目录。`build/build.ps1` 增发布 `dist/tray/`。**离线全绿**（`smoke_tray.py` 状态形状 + settings.json 写入→重探生效→清理回落；`smoke_stdio.py` 仍 33 工具；规则金样全绿）；**过 NX2412 实机**（`smoke_live` 全量 A–K 零回归——第十/十一片改动波及插件 `Workspace`，重跑证明沙箱根仍解析到 `%LOCALAPPDATA%\NXAssistant\workspace` 且 5 张金样判定、STEP 往返、undo 链、抽取负路径全部不变；托盘 `--expect-plugin-online true` 在 NX 起来后命中）。

**安装包骨架批次（第十二片）**：`build/installer/install.ps1` + `uninstall.ps1` 把 PRD §7.1 的"Windows 安装程序"先兑现成**免 UAC 的用户级脚本安装**（正式 MSI/EXE 壳待选型，包装逻辑已可复用）。装：三组件进 `%LOCALAPPDATA%\Programs\NXAssistant`（`mcp\`、`tray\`、`nx_plugin\`），**规则包随宿主**复制到 `mcp\company_v3`——独立安装态下这是宿主规则解析链（env > settings.json > 同目录 > docs 祖先）唯一可靠落点，缺了会 fail-closed 拒掉所有受守卫写；托盘登记 `HKCU Run` 自启；startup 插件部署**复用 `deploy_plugin.ps1` 的合并语义**（只复制、绝不删他人文件，NX 占用则暂存），且安装器调的是随装副本、脱离仓库可用；`install.json` 记录安装清单。**卸载=清单驱动**：无 `install.json` 或 target 不匹配即拒删（防误指他人目录），startup 插件文件默认保留、`-RemovePluginFiles` 才按清单逐个清；`settings.json`/license/runs/workspace 属用户数据一律不动。**实机端到端冒烟**（`build/smoke_installer.py`：假 UGII 目录装→三组件+规则包+脚本落位、Run 值精确匹配、插件进假 startup→**安装态宿主跑通 stdio 33 工具**、安装态托盘 `rules_dir` 解析到随装包→按清单卸载全清→无清单目录拒删防呆；不碰真机 `%UGII_USER_DIR%`/注册表既有值）。

待办（按 `tool-migration-v1.md` §5 分批）：
- 读/写工具批次（续）：`shell_body`/`mirror_feature` 等按需排期（`import_exchange` 已随第八片落地，见上）。
- 工程图类样件与校准（待客户）：真实图框 id、图层名反查通道、标题栏字段别名、焊缝/BOM 行文本、圆形孔组 PCD 容差、板厚启发在异形件上的替代口径——客户 2D 图纸样件到位后把第十片各负路径/占位逐项转正。
- 规则占位表（待客户数据）：FLANGE-PCD/STEEL/WELD/BOM/EDGE-SAFE 的数值表目前为 placeholder，命中即标 `placeholder:true`。
- 打包交付：`build/installer/install.ps1`/`uninstall.ps1` 用户级脚本安装已过端到端冒烟（见第十二片）；正式图形化安装包/MSI 壳与代码签名待选型。
- 托盘观感：状态窗/设置为功能版（系统图标占位），品牌图标与文案打磨待交付设计。

## 已知约束 / 红线

- stdio 传输：stdout 只走 JSON-RPC，诊断一律进 stderr/文件（已在宿主 `ClearProviders`）。
- **插件线程亲和**：NXOpen 对主线程有亲和性。管道 worker 只做收发，所有 handler 经 `NxPlugin/MainThread.cs` 的
  隐藏 WinForms Control `BeginInvoke` marshal 回 NX 主线程执行（超时 120s 明确报错，不盲目重试）。
  **任何异常不得从 BeginInvoke 回调逃逸**：.NET Framework WinForms 会把回调异常额外送进主线程
  `Application.OnThreadException`，开着 JIT 调试的机器上每错一次弹一次 NX 对话框；因此闭包内捕获异常、
  回到 worker 线程后用 `ExceptionDispatchInfo` 原样重抛（`build/verify_no_dialog.py` 实机复验无弹窗）。
- 部署插件进 `%UGII_USER_DIR%\startup`：旧 NX-MCP 桥已从 startup 摘除，`startup\` 现仅含 NxAssistant 插件及其依赖。后续切换：把 Qoder `siemens_nx` MCP 配置指向 `E:\nx-buddy\dist\mcp\NxAssistant.Mcp.exe`。NX 运行中 startup DLL 会被锁定，`deploy_plugin.ps1` 会自动暂存到 `dist\nx_plugin_staged` 并在下次 NX 关闭后重跑生效。
- Windows PowerShell 5.1 按 GBK 读无 BOM 的 UTF-8 `.ps1`，中文注释会吞行：**所有 .ps1 必须带 UTF-8 BOM 保存**。
- 目标框架锁定 NX 2412；占位规则数值不得写死进 C#。
