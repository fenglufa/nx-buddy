# V1 开发进度记录

> 本文件自根 README 迁出（2026-09-22 整理）：根 README 讲"这是什么、怎么编译、怎么装"，
> 这里逐批记录"做到哪了、怎么验的、还欠什么"。批次推进时更新此文件与
> [`tool-migration-v1.md`](tool-migration-v1.md) §5 进度链，不再回填 README。

## 里程碑总览（V1 全部实机验证）

| 切片 | 内容 | 提交 |
|---|---|---|
| 骨架 | 三组件解决方案 + ping 协议脊柱 | 8d304a0 |
| 授权 | KeyGen 签发 + 验签闸门（PRD §8） | 0188b1a |
| 规则引擎 | company_v3 主包 + 华恒子包，离线金样 | d747436 |
| 测试金样 | test/drawings 5 件 .prt + findings 对照表 | f9f88da |
| 第一片 | 主线程 marshal + 读/存/建 5 方法（实机） | a63a351 + eda698b |
| 第二片 | stable_id 拓扑/特征四方法（实机全绿） | 1476ebe |
| — | BeginInvoke 异常逃逸修复（NX 无弹窗） | 9495ddd |
| 第三片 | block/sketch/extrude/表达式写闭环（实机） | cd872c8 |
| 第四片 | create_cylindrical_hole + HOLE-DIA-001 写前守卫 | ce1aa06 |
| 第五片 | fillet/chamfer + FEAT-FILLET-002 warn 守卫 | 777cfd4 |
| 第六片 | move_object 包围盒位移回读护栏 | dfac3f8 |
| 第七片 | export_exchange STEP 导出 + 沙箱扩展 | 145ef2c |
| 第八片 | import_exchange STEP 导入 + 往返闭环 | 5a2623c |
| 第九片 | 审图长任务三工具 + run 状态机 + xlsx 报告 | 0e35682 |
| 第十片 | 抽取类 inspect_*×8 + undo_last_assistant_change（33 工具齐） | c04f8ec |
| 第十一片 | 托盘 NxAssistant.exe + NxaSettings 共享配置层 | cbca953 |
| 第十二片 | 脚本安装器 install/uninstall + 端到端冒烟 | 86ac0c2 |
| 第十二片续 | Inno Setup 壳 installer.iss + 静默装卸冒烟 | ca4047c |
| 第十三片 | 规则可见与可管理：rules_state 覆盖层 + 托盘规则管理分页 | 86fef2e |
| 第十四片 | 审图工作台：托盘左键主页 + MCP stdio 客户端直连宿主 | 2c42590 |
| 第十四片续 | 装机验收修正：桌面快捷方式 + 启动即开主页 + 宿主控制台窗口隐藏 | 6b467b3 |
| 第十四片续2 | NX 插件部署改安装尾声必做 + 结果弹窗 | 5099588 |
| 第十四片续3 | 规则管理页交互全失效修复（列 Name 缺失） | 34bc207 |
| 第十四片续4 | 安装器中文化 + 升级前自动关闭自家进程 | f45b7b5 |

## 批次详记

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

**Inno Setup 壳（第十二片续）**：选型定案 Inno（用户级免 UAC 与脚本安装器同口径；MSIX 虚拟文件系统与"NX 从真实 startup 加载 DLL"根本冲突，Squirrel 面向 Electron 自动更新，真 MSI 仅在客户 IT 强要求时走 WiX 迁移）。`build/installer/installer.iss` 只做"双击体验 + 卸载登记"，**业务逻辑零重实现**：三组件+规则包由 `[Files]` 落位（与 install.ps1 同布局），HKCU Run 走 `[Registry]`（`uninsdeletevalue` 自动撤销），startup 部署挂 `[Tasks] deployplugin`（`Check: UgiiDirExists`），安装后由 `[Code] CurStepChanged` 调**随装的** `deploy_plugin.ps1`（合并语义原样复用）；卸载由 `CurUninstallStepChanged` 按 `{app}\nx_plugin` 的 DLL 名单**逐个对名删** startup 同名文件——清单外文件（他人插件）绝不触碰。`build/make_installer.ps1` 自动定位 ISCC（Program Files 或 winget 用户目录），预留 `-Pfx` 签名钩子（证书采购后启用）。**实机端到端冒烟**（`build/smoke_iss.py`：编出 `dist\installer\NXAssistant-Setup-1.0.0.exe`→`/VERYSILENT /TASKS=deployplugin` 装进临时目录→11 个插件 DLL+规则包落位、Run 值精确、假 startup 部署且预埋的第三方 DLL 未动→安装态托盘 `rules_dir` 命中随装包→`unins000 /VERYSILENT` 目录/Run 值/同名 startup DLL 全清、**第三方 DLL 存活**）。

**规则可见与可管理批次（第十三片）**：补用户反馈的两个缺口——规则"看不见、改不了"。设计定为**两层模型**：
`docs/company_v3` 基线包**只读**（升级整目录替换，用户改动绝不写回），用户态改动落在
`%LOCALAPPDATA%\NXAssistant\rules_state.json` **覆盖层**（env `NXA_RULES_STATE` > settings.json `rules_state_path` > 默认），
结构为 `{disabled:[rule_id], data:{"rel/path.json":<整文件替换>}, log:[{t,action,detail}]}`。
叠加钩子打在 `RulePack.Load(rootDir, state)`（规则清单过滤 + `Data()` 按相对路径拦截替换），
因此**写前守卫与审图零改动共享同一份用户态**；`HostRules.LoadPack` 每次解析都复查包目录与覆盖层文件的
mtime，托盘保存后宿主**下一次受守卫写/审图即生效，无需重启**；fail-closed 语义不变（基线包缺失仍全拦，
"禁用"≠"删除"，升级包后新规则默认启用）。`RuleDataFiles` 映射 rule_id→数据文件供 UI 反查；
FEAT-FILLET-002 的圆角系列从 C# 硬编码移入 `fillet_series.json`（兑现"占位数值不写死"红线并让用户可改）。
托盘 →"主页"（第十三片时在"设置 → 规则管理"，第十四片起移入主页，见下）：全规则表格
（启用勾选/编号/组/名称/级别/用户改动标记）+ 每规则"改参数"对话框
（纯数值/字符串数组按逗号列表编辑，其余字段原样保留，整文件替换语义）+ 保存/重置 + 最近 15 条变更日志
（回答签核报告"这条规则被谁停的"）。`--status-json` 新增 `rules_state_path/rules_disabled_count/rules_overrides_count/rules_state_latest`。
**离线全绿**（规则金样新增 9 用例：禁用后 φ13.2 不报且螺纹仍拦、改参进系列放行、圆角改参、保存/重载、三类留痕；
`smoke_stdio.py` T4a/T4b：同进程内 φ13.2 被拦→写覆盖层禁用→即时放行到插件阶段（`target_body_index=999` 防呆，
真机 NX 在跑也绝不会真钻孔）；`smoke_tray.py` T4 覆盖层探测；安装态回归：`smoke_installer`（含随装包）与
`smoke_iss` 静默装卸全绿）。V1 权限口径（2026-09-22 用户定）：自由改 + 全程留痕，不做审批链。

**审图工作台 + 托盘主页批次（第十四片）**：兑现"人没有 Agent 也要能审图"与"左键图标要有主页"
（2026-09-22 用户定前端形态 A：纯 WinForms 深化，不起 HTTP 端口）。核心决定：托盘做成 **MCP 客户端**——
`McpHostClient` 逐行 JSON-RPC（initialize→initialized→tools/call，与 build/smoke_stdio.py 同语义）
自拉一个**托盘专属** `NxAssistant.Mcp.exe`，审图走 review_folder/review_status/review_findings 原工具，
**不存在第二套判定实现**：授权闸门、写前守卫、run 状态机、xlsx 报告与 Agent 完全同源。
主页三分页：状态与操作（四态+授权详情+导入 Key/复制 MCP 配置/打开日志/报告目录/路径设置）、
审图工作台（目录选择→发起即回 run_id→2 秒轮询进度/当前张→取消（当前张完成后生效）→续跑→
findings 表格（列对齐金样：图号/规则/级别/对象/实测/建议/占位）→打开 审图报告.xlsx；
顶栏下拉切换历史 run）、规则管理（第十三片 RulesTab 自设置窗迁入主页，设置窗只留路径）。
两个跨进程护栏：① `review_status` 会把"磁盘在跑但本宿主不认识"的 run 判成 interrupted——
因此 MCP 轮询/取消**只允许**作用于本托盘宿主自己发起的 run，他方 run 只读 run.json 展示并给出
"去发起方取消"提示；② 发起前若探测到别的宿主有 running run（ReviewBusy）弹确认——两个宿主
并发审图会共用 NX 插件单 worker 队列、同名部件互踩记单张失败。主页关闭=隐藏（审图继续），
托盘"退出"=真关并 dispose 宿主（未完成 run 下次查询自动判 interrupted，可续跑）。
无头自检：`--review-probe`（真走 stdio 客户端：license_status + 不存在 run 必回结构化错误）与
`--ui-probe`（MainWindow 三分页构造+1.2 秒真关）入 `smoke_tray.py` T5/T6；离线全绿，
安装态 `smoke_installer`/`smoke_iss` 复跑全绿。V1 已知边界：Agent 与托盘各持宿主进程，
互为"他方 run"（只读可见、不可代取消）；主页人工观感验收仍欠（同安装向导一项）。

**装机验收修正三条（第十四片续，2026-09-22 用户在已装机上反馈）**：
① 安装向导新增"创建桌面快捷方式"任务（默认勾选，`[Icons] {autodesktop}`，卸载随 Inno 一并移除；
静默参数显式给 /TASKS 时未列出任务不生效，README 已注明 `/TASKS="deployplugin desktopicon"` 写法）；
② 托盘启动（开机自启/快捷方式/装完立即启动）先自动打开主页，不再只蜷在托盘角里；
③ 修"点托盘图标弹一个空白 cmd 窗口"：宿主是 console 子系统 exe，GUI 父进程拉起时 Windows 会另开
控制台——`McpHostClient` 补 `CreateNoWindow = true`（stdio 三条早已重定向，那个窗口永远是空的）。
状态栏"有宿主进程在跑/无"随之解释清楚：主页开着即托盘专属宿主活着（与 Agent 宿主同等计数），
关窗收回托盘宿主退出显示"无"——本来就是这个设计，异常只有弹窗一项。
验证：smoke_tray T1–T6 全绿（含 ui-probe 真开关窗）；ISCC 编译通过并重建 setup.exe；
`smoke_iss` 在装有真机的开发机上按其护栏拒跑（保护用户 HKCU Run 自启不被临时目录装覆写），留净机/下轮执行。

**插件部署改必选（同日第二轮反馈，覆盖上条①的 deployplugin 任务与 /TASKS 写法）**：
用户裁定"用 NX 小助手这步必须发生，不该摆给用户选"——iss 删除 `deployplugin` 任务行，
`CurStepChanged(ssPostInstall)` 无条件尝试部署：环境有 `UGII_USER_DIR` 就跑随包
`deploy_plugin.ps1`（合并复制语义不变），缺变量/脚本非零退出记为失败态；
`ssDone` 且非静默时弹一次结果提示——成功=信息框（提醒重启 NX 生效），
失败=错误框说清"插件本次未部署 + 原因是没找到 UGII_USER_DIR/退出码 N"+给手动补部署命令。
静默/IT 推送不弹窗（看 deploy 脚本自己的输出窗口）。`smoke_iss.py` 去掉 `/TASKS=deployplugin`。
用户第三问的答案（本来就有）：**卸载会清 startup**——`CurUninstallStepChanged` 在 usUninstall
阶段只删"与 `{app}\nx_plugin\*.dll` 同名且存在"的文件，他人 startup 程序集不碰
（smoke_iss T4 第三方 dummy DLL 存活断言覆盖）。备选方案"主页显示未部署"暂未做：
状态页的"NX 未连接"已能间接暴露，人工验收后再定是否加专门提示。

**规则管理页修复（同日第三轮装机反馈）**：用户实测"点行无反应、改参数无弹窗、找不到增删规则"。
根因是静默级 bug：`RulesTab` 勾选列/按钮列只设了 `DataPropertyName`、没设 `Name`，而
`OnCellValueChanged`/`OnCellClick` 按 `Columns[i].Name` 比对语义标记——Name 默认空串，
两个处理器对一切点击直接 return，**启用/禁用勾选框同样失效**（离线冒烟 T4 走 RulesState 文件层、
不经网格事件，所以一直全绿没拦住）。修复=两列显式 `Name = ColEnabled/ColEdit`。
体验补齐：行**双击弹规则详情**（级别/标准/适用对象/用户改动/当前生效数据文件+内容预览）、
表头下加操作提示行、"改参数"对无独立数据规则的说明弹窗随修复恢复可见。
"添加/删除规则"维持 V1 边界：删除的等价物=禁用勾选（留日志可审计）；新增判定逻辑属引擎随版本扩展，
纯阈值差异走"复制 company_v3 + rules_dir 指向"（UI 提示行也写明了）。
教训：WinForms 网格按 `Column.Name` 分发事件的，构造列必须同时赋 Name；此类"点了没反应"
离线冒烟测不到，属人工验收不可替代的品类。

**安装器中文化 + 升级免"文件占用"弹窗（同日第四轮装机反馈）**：用户退出托盘后重装，仍被 Inno 的
英文 "The following applications are using files…" 框住，占用者是 `NxAssistant.Mcp.exe`。
根因两层：直接原因是开发机上残留孤儿宿主（一个是崩溃冒烟脚本没 kill 掉的子进程，一个是 Qoder 按
用户级 MCP 配置常驻拉起的）；但**通用原因是真实的**——任何 MCP 客户端（Qoder/Claude 等）都会把
宿主拉起来常驻，用户升级安装时必然撞文件锁，退出托盘管不到客户端名下的宿主。修复走安装器侧：
`installer.iss` 加 `PrepareToInstall`，安装前 `taskkill /F` 自家两个进程（先托盘后宿主，避免托盘
按需重拉宿主的竞态，尾部 Sleep 500ms 等句柄释放）——两者皆无用户数据，装完托盘/客户端各自重拉，
等价于自动选了"关闭应用"且不再弹框。语言问题一并处理：内嵌 Inno 官方 unofficial 简体中文语言包
（`build/installer/Languages/ChineseSimplified.isl`，jsDelivr 取，**入库前必须补 UTF-8 BOM**，
raw.githubusercontent 直连被墙则走 CDN）；[Languages] 只挂中文 = 全程中文、无选择对话框，
Restart Manager 等系统文案随语言包变中文。`smoke_iss.py` 为行为级断言、不解析 iss 文本，无需改。

待办（按 `tool-migration-v1.md` §5 分批）：
- 主页/工作台人工验收：`--ui-probe` 只证构造无异常；左键开主页、审图全流程（真 NX + 金样目录）、
  规则管理交互手感需要在开发机人检一轮后再发客户（与安装向导观感同轮）。
- 规则"新增"入口：第十三片覆盖层兑现了启停/改参；全新判定逻辑属引擎扩展（随版本），纯阈值/系列差异
  引导用户走"复制 company_v3 改 JSON + `rules_dir` 指向"路线（README 已写明）。
- 读/写工具批次（续）：`shell_body`/`mirror_feature` 等按需排期（`import_exchange` 已随第八片落地，见上）。
- 工程图类样件与校准（待客户）：真实图框 id、图层名反查通道、标题栏字段别名、焊缝/BOM 行文本、圆形孔组 PCD 容差、板厚启发在异形件上的替代口径——客户 2D 图纸样件到位后把第十片各负路径/占位逐项转正。
- 规则占位表（待客户数据）：FLANGE-PCD/STEEL/WELD/BOM/EDGE-SAFE 的数值表目前为 placeholder，命中即标 `placeholder:true`。
- 打包交付：脚本安装器（`build/installer/*.ps1`）与 Inno Setup 壳（`build/installer/installer.iss` + `build/make_installer.ps1`）均已过端到端冒烟；余下仅**代码签名**——2026-09-22 用户定：测试验证阶段先不采购证书（注意：nginx 用的 SSL/TLS 证书是 serverAuth 用途，不能签代码），`make_installer.ps1 -Pfx` 钩子已备。
- 运行时前置（2026-09-22 README 整理时新发现）：当前 publish 为 **framework-dependent**（csproj 无 RuntimeIdentifier/SelfContained），裸机客户需先装 .NET 8——宿主吃 Base Runtime，托盘（WinForms）吃 **Windows Desktop Runtime**。交付前二选一：文档化前置安装，或改 `-r win-x64 --self-contained` 发布（安装包体积换免依赖）。
- 安装向导人工验收：`smoke_iss.py` 只覆盖静默路径；双击向导（中文界面+升级免"文件占用"弹窗已随
  第四轮落地，需人检观感；部署结果弹窗三态：成功/缺 UGII_USER_DIR/脚本报错、桌面图标、
  真 startup 部署含 NX 占用暂存分支）需在开发机人检一轮后再发客户。
- 托盘观感：状态窗/设置为功能版（系统图标占位），品牌图标与文案打磨待交付设计。
