# V1 白名单工具迁移 / 重做清单

> 目的：把 `prd-01.md` 第 9 节的 V1 工具白名单，逐个对齐到参考实现 `E:\NX-MCP`（Python + pythonnet + NXOpen）的既有做法，
> 并给出 C#/.NET 重写的调用蓝图、护栏要求、规则挂钩点与分批顺序。
> 参考实现的 `nx_remote_ops.py`（11000+ 行）与 `ADDING_TOOLS.zh-CN.md` 已经过 NX 2412 实机验证，本项目"参考其逻辑与规范、用 C# 重写"。

## 0. 范围界定（对齐 PRD §9）

| 分组 | 处理方式 | 数量 |
|------|----------|------|
| §9.1 保留（对标 Hub） | **移植**：C# 重写，语义与 NX-MCP 对应 `_op_` 接近 | 19 |
| §9.1 外补充（客户已确认纳入 V1） | `move_object`（回读护栏样板） | 1 |
| §9.2 新增（Hub 无） | **新做**：授权、审图长任务、图纸/属性抽取、定向撤销 | 14 |
| §9.3 推迟（V1.1） | 本轮**不做**，仅登记边界（revolve/sweep/loft/boolean/pattern/mirror/shell/import/装配全家/工程图创建） | — |
| §9.4 不做 | 明确排除：曲面、钣金、`create_involute_gear`、`run_python`、全部 CAM/机床/仿真 | — |

V1 交付集 = §9.1 + §9.2 + `move_object` = **34 个工具**（客户 2026-09-22 确认：`move_object` 进 V1）。

---

## 1. 跨工具通用移植约定（先定契约，所有工具遵守）

这些是从 NX-MCP 提炼、必须原样平移的"活插件"规范。C# 侧对应放在 `NxAssistant.NxPlugin` 内（唯一直接调用 NXOpen 的层）。

### 1.1 请求 / 结果信封
- NX-MCP：`handle(request_json)` 收 `{id, method, params}`，分发 `_OPS[method](params)`；
  成功 `{id, ok:true, result:{...}}`，失败 `{id, ok:false, error:{message,type,traceback}}`。**op 内部 `result.ok` 恒 true**（唯一例外 `rebuild_work_part` 的 `ok` 可为 false）。
- C# 移植：IPC 报文沿用同一 JSON 形状（`System.Text.Json`）。`method`→插件内 handler 表；异常在边界捕获转 `error` 信封；`traceback` 换 `Environment.StackTrace`。**审图/写操作的"未知态"必须靠信封字段表达，不得让宿主猜**（见 `prd-02`）。

### 1.2 写操作护栏模板（金标准 = `_op_move_object`）
每个改模型的 op 必须：
1. `mark = Session.SetUndoMark(MarkVisibility.Visible, "NXA <op>")`；
2. 用 `stage` 变量跟踪进度（`create_builder`/`select`/`set_params`/`validate`/`commit`/`name`）；
3. `try { builder 配置 → Validate() → CommitFeature() → SetName → 回读 } `；
4. `catch { Session.UndoToMark(mark, null)（自身再 try 吞）; throw 带 stage 的异常 }`；
5. `finally { builder.Destroy()（吞异常） }`；
6. **回读校验，绝不信任 Commit 成功**：写后实测效果（包围盒位移 via `UFSession.ModlGeneral.AskBoundingBox`、body 计数、`UpdateManager.DoUpdate` 错误数、`feature.GetFeatureErrorMessages()`），不符则回滚并抛"NX 提交了但未生效"。
   - 教训原型：`MoveBodyBuilder` 对特征驱动 body 静默零位移；故所有 commit 类 op 一律回读。

### 1.3 数值 = 表达式字符串通道
- 一切长度/直径/半径/偏移写 `X.RightHandSide = value.ToString("R", InvariantCulture)`（对应 Python 的 `repr(float)`），不要直接赋浮点属性。
  - 涉及：`Extrude.Limits.StartExtend/EndExtend.Value`、`Cylinder.Diameter/Height`、`Chamfer.FirstOffsetExp/SecondOffsetExp`、`EdgeBlend.AddChainset(collector, radiusString)`、`Motion.DeltaXc/Yc/Zc`、`Block.SetOriginAndLengths(pt, lStr, wStr, hStr)`。
- 草图尺寸先 `Part.Expressions.CreateSystemExpressionWithUnits($"name = {value:R}", lengthUnit)` 再喂给 `CreateDimension`。

### 1.4 拓扑选择统一走 ScRuleFactory dumb 规则 + 稳定 ID
- 选 body/face/edge 用 `work.ScRuleFactory.CreateRuleBodyDumb/CreateRuleEdgeDumb/CreateRuleCurveDumb` + `ScCollector.ReplaceRules([rule], false)`；拉伸用 `Section.AddToSection(...)`。避开 UI 选择语义。
- **稳定 ID**（`resolve_topology` 的核心）：对 face/edge 计算几何指纹（`UFSession.Modeling.AskFaceData` 拿 type/point/direction/radius/box；edge 拿 SolidEdgeType/端点/邻接面）→ 规范化 JSON（字段序无关）→ `SHA1[:20]`，前缀 `face:`/`edge:`。失配时按 tolerance 几何 fallback + `sort_by`/`occurrence` 选择。
  - **PRD 硬要求**：面向 Agent 的对象引用一律用 stable_id，禁止让模型死记面/边下标（§7.2）。

### 1.5 参数校验与文件名安全
- 廉价正向校验放宿主（server 层）：长度、非零、枚举合法、有限性；深层校验放插件（对应 Python helper `_finite_positive`/`_vector3`/`_body_by_index`/`_items_by_indices`）。
- 特征命名统一前缀 `NXA_`（替代 NX-MCP 的 `MCP_`）；`_safe_object_name` 语义：清洗为 `[A-Za-z0-9_]`，数字开头补前缀，≤120 字符。
- 文件类工具（`create_part`/`save`/`export`）：仅接受纯文件名 + 白名单扩展名，`Path.GetFullPath` 后必须落在配置的工作区根内（`FILE-001`），盘符跳跃/`..` 逃逸拒绝。

### 1.6 已知 NX2412 陷阱（移植时必须带上）
- 单位对象名是 `"MilliMeter"`（NX 拼写），依次 fallback `Millimeter`/`mm`/`Inch`。
- STEP 导出（`export_exchange`）依赖磁盘文件：`StepCreator.ExportFrom=ExistingPart` + `InputFile=work.FullPath` → **导出前必须先 save**；`ProcessHoldFlag=true`；协议默认 `ap242`。
- 草图：`InferNoConstraints` + 全部显式约束；`CalculateStatus()`/`GetStatus()` 回读；成型后 `sketch.Blank()`。
- `AskBoundingBox` 可能抛异常 → 回退边顶点 min/max（记 `bounds_method`）。
- `Save`/`DoUpdate` 返回的 `SaveStatus` 等需 `Dispose()`。
- 集合类不可直接下标（`work.Bodies`）→ 先物化成数组。

---

## 2. §9.1 保留工具逐一对齐（19）+ `move_object`

图例：**保留=可直接移植**；护栏列指 §1.2 是否适用；规则列指 commit 前需过的 company_v3 规则。

| # | 工具 | NX-MCP 参考 (`_op_`) | 核心 NXOpen/UF 调用链 | 读/写 | 规则挂钩点 |
|---|------|---------------------|----------------------|-------|-----------|
| 1 | `ping` | `_op_ping` | 读 `Session`：`Parts.Work`/`Leaf`、pid、`ApplicationName` | 读 | 无；但需回报"插件是否已连、NX 是否就绪" |
| 2 | `get_part_summary` | `_op_part_summary` | 遍历 `work.Bodies`/`work.Features`（`max_features` 截断） | 读 | 无 |
| 3 | `inspect_work_part_geometry` | `_op_body_geometry` | 每 body `GetFaces/GetEdges/GetVertices` + `ModlGeneral.AskBoundingBox` | 读 | 供 `BODY-001/002`（空图/多余片体）判定 |
| 4 | `inspect_body_topology` | `_op_body_topology` | `_topology_records`：`Modeling.AskFaceData`、SolidEdgeType，产 stable_id | 读 | 拓扑选择产出端 |
| 5 | `resolve_topology` | `_op_resolve_topology` | stable_id 精确→几何 fallback→`sort_by/occurrence` | 读 | `unique=true` 多义时报错，逼 Agent 补选择器 |
| 6 | `inspect_feature` | `_op_inspect_feature` | `feature.GetParents/Children/GetBodies`、`IsOutOfDate`、`GetFeatureErrorMessages`、表达式记录 | 读 | `FEAT-EXT-001`/`FEAT-FILLET-002` 取参数值 |
| 7 | `rebuild_work_part` | `_op_rebuild_work_part` | `UpdateManager.DoUpdate(mark)` + 全 feature 诊断扫描（不回滚） | 写(报告) | 返回 `update_error_count`，是"假成功"防线 |
| 8 | `save_work_part` | `_op_save_work_part` | `work.Save(SaveComponents.True, CloseAfterSave.False)`，回读 `isfile`+size | 写 | `FILE-001` 路径沙箱 |
| 9 | `create_part` | `_op_create_part` | `Session.Parts.NewDisplay(path, Part.Units.Millimeters)`；文件名沙箱 | 写 | `FILE-001` |
| 10 | `create_block` | `_op_create_block` | `CreateBlockFeatureBuilder`+`SetOriginAndLengths(pt,l,w,h 字符串)`+`BooleanType.Create` | 写 | **NX-MCP 此处护栏最薄，移植时必须补 §1.2 stage/rollback**；`BODY`/尺寸正向校验 |
| 11 | `set_feature_expression` | `_op_set_feature_expression` | 四路解析 expression→`RightHandSide=rhs`(字符串)→`DoUpdate`→回读 old/new+errors+out_of_date→失败 UndoToMark | 写 | 表达式护栏范本；改后跑相关规则复检 |
| 12 | `create_parametric_sketch` | `_op_create_parametric_sketch` | `CreatePlane`+`CreateSketchInPlaceBuilder2`+`Curves.CreateLine/Arc`+`AddGeometry(InferNoConstraints)`+显式约束+`CreateDimension/Create*Dimension`+`CalculateStatus` | 写 | `SKETCH-001`(须 Finished)、`SKETCH-002`(单位一致) |
| 13 | `inspect_sketch` | `_op_inspect_sketch` | `work.Sketches`→`GetAllGeometry/GetAllExpressions/GetStatus` | 读 | `SKETCH-001/002` 判据来源 |
| 14 | `extrude_sketch` | `_op_extrude_sketch` | `Sections.CreateSection`+`CreateRuleCurveDumb(sketch.GetAllGeometry)`+`AddToSection`+`CreateExtrudeBuilder`+`Limits.*.RightHandSide`+`BooleanType.Create` | 写 | `SKETCH-001`(成型前须完成)、`FEAT-EXT-001`(distance>0 且有限)；`GetNumberOfLoops()>=1` |
| 15 | `create_cylindrical_hole` | `_op_create_cylindrical_hole` | `CreateCylinderBuilder`+`Type=AxisDiameterAndHeight`+`Diameter/Height.RightHandSide`+`BooleanType.Subtract`+`SetTargetBodies` | 写 | **`HOLE-DIA-001` 主战场**：`create_cylindrical_hole(13.2)` 必须在 commit 前拒绝并建议 13/14（PRD 验收项）；`HOLE-BOLT-002`/`PIN-BORE-001` |
| 16 | `fillet_edges` | `_op_fillet_edges` | `CreateEdgeBlendBuilder`+`CreateRuleEdgeDumb`+`AddChainset(collector, radiusStr)` | 写 | `FEAT-FILLET-002`（R 建议系列，warn 级不阻断） |
| 17 | `chamfer_edges` | `_op_chamfer_edges` | `CreateChamferBuilder`+`SmartCollector`+`Option=SymmetricOffsets`+`First/SecondOffsetExp.RightHandSide` | 写 | 可选倒角系列校验 |
| 18 | `export_exchange` | `_op_export_exchange` | `DexManager.CreateStepCreator/CreateParasolidExporter`+`ProcessHoldFlag`+回读文件 size | 写 | 仅工作区内 STEP（PRD §9.1 限定）；`FILE-001` |
| 19 | `inspect_drawing_annotations` | `_op_inspect_drawing_annotations` | 遍历 `Part.DraftingSheets`→sheet.Annotations（尺寸/注释/中心标记），提取文本与测量值 | 读 | 审图抽取端，配合 §3 `inspect_*` 与规则 `DRAW-*` |
| 20 | `move_object` | `_op_move_object` | `CreateMoveBodyBuilder`+`CreateRuleBodyDumb`+`Motion.Option=DeltaXyz`+`DeltaXc/Yc/Zc.RightHandSide=repr`+**包围盒位移回读否则回滚** | 写 | §1.2/§1.4 回读护栏的**教科书样板**（本项目里作为其它写 op 的模板） |
| 21 | `import_exchange` | `_op_import_exchange` | `DexManager.CreateStep203/214/242Importer`+`ImportTo=WorkPart`+缝合/简化+`SetMode(NativeFileSystem)`+`ProcessHoldFlag`；回读**实体增量>0 且 update_error_count==0**，否则 ok:false | 写 | `FILE-001`（输入必须存在于沙箱内）。**注：PRD §9.1 未含此工具**，2026-09-22 用户指示追加（STEP 往返闭环+move_object 正路径探索），待客户追认 |

> 注：`move_object` 在 PRD §9 未显式列出；客户 2026-09-22 已确认**纳入 V1**（作为回读护栏范式的实现与验收样例），故 V1 = 34；`import_exchange`（§2 第 21 行）为用户指示追加、客户追认前 V1 = 34(+1)。

---

## 3. §9.2 新增工具（Hub 无，产品必须新做，14）

这些是"从画图工具升级为可售卖合规审图产品"的关键增量，NX-MCP 里没有对应实现，需在插件/宿主里从零写。

| 工具 | 归属层 | 职责 | 关键实现要点 |
|------|--------|------|-------------|
| `license_status` | **宿主**（不需 NX） | 读授权状态：有效/过期/未绑机/机器指纹 | 验签→验期→验指纹（PRD §8.3）；返回 `status`+`seats`+`not_after`+`machine_bound`。未授权时 create/review 工具返回 `LICENSE_INVALID` |
| `review_folder` | 宿主编排 | 提交本地目录审图，**立即返回 `run_id`**（不同步堵死） | 枚举工作区下 `*.prt`+工程图；串行调度（PRD §7.3）；`FILE-001` 目录沙箱；`resume_run_id` 从下一张续跑（interrupted/cancelled/failed 可用）；逐张经内部 IPC 方法 `extract_evidence`（插件非显示打开→抽 `Evidence.Part` 段→即关并恢复 Work part） |
| `review_status` | 宿主 | 按 `run_id` 查进度 | 状态机枚举见 `prd-02`：`queued/running/completed/completed_with_errors/failed/cancelled/interrupted`；始终带 `run_id/done/total/report_path/error_code/can_resume`；状态落磁盘 run 文件，进程重启可查（`running` 滞留自动判 `interrupted`）；`cancel=true` 请求停止（当前张完成后生效，Agent 侧取消能力由本参数兑现，不另立第 4 工具） |
| `review_findings` | 宿主 | 拉取某 run 的 finding 明细（分页） | finding 结构对齐 §7.5 金样：`drawing_no/version/rule_id/object_id/view/actual/suggestion/standard` |
| `inspect_title_block` | 插件(NXOpen) | 读图纸标题栏字段 | 抽 `drawing_no/revision/title/material/scale/drawn_by/date` → 喂 `TITLE-001~004`（正则见 `part_number_pattern.json`、材料白名单） |
| `inspect_drawing_sheet` | 插件 | 读图纸页/图框/视图清单 | 图框 id/模板名 → `FRAME-001`（白名单 `drawing_frames.json`）；视图数 → `VIEW-001` |
| `inspect_layers` | 插件 | 读对象所在图层分布 | → `LAYER-001/002`（allowed/forbidden 见 `layers.json`）；审图批处理用 |
| `inspect_weld_annotations` | 插件 | 判断工程图有无焊缝符号 | → 华恒 `WELD-ANN-001`（只查有没有，warn） |
| `inspect_parts_list` | 插件 | 读明细表/BOM 行 | → 华恒 `BOM-BUY-001`（外购件词典匹配，warn） |
| `inspect_sheet_thickness` | 插件 | 读板厚（钣金壁/属性） | → 华恒 `PLATE-THK-001`（`plate_thickness.json` 白名单，fail） |
| `inspect_hole_pattern` | 插件 | 读法兰/孔组（孔分布） | → 华恒 `FLANGE-PCD-001`（`flange_patterns.json` 样本比对，fail）；复用 §1.4 稳定选择 |
| `undo_last_assistant_change` | 插件 | 只撤销小助手上一次变更 | 依赖 §1.2 每次写都设的具名 undo mark；记录并回到"本工具上一次 mark"，绝不误撤用户操作 |

> 抽取类 `inspect_*`（title_block/sheet/layers/weld/parts_list/thickness/hole_pattern）复用 §2 读工具的回读风格：返回结构化 JSON，不改模型、不保存。规则引擎（宿主）拿 JSON 比对 company_v3，产出 finding——**几何判定用程序硬规则，LLM 只解说 findings**（PRD 原则 §5）。

> **实现说明（2026-09-22，批 5 抽取类 + 撤销切片落地）**：
> - §9.3 把工程图**创建**推迟到 V1.1，无客户 2D 样件，故图纸侧抽取器按**负路径**验收：读的是真实 API（`DrawingSheets`/`GetUserAttributes`→`AttributeInformation.Title/StringValue`/`GetScale`/`GetDraftingViews`/`Annotations.Welds|PartsLists`），建模件上返回 `drawing_sheet_count=0` 且 `ok:true`；样件到位后不改契约直接转正。
> - `inspect_title_block` 返回逐页**原始属性全集**（`title_block_raw`），canonical 字段（drawing_no/revision/…）由宿主 `RuleEngine.TitleField` 经 `docs/company_v3/title_block_fields.json` 占位别名表解析（placeholder:true，待客户校准）；审图 findings 行"图号/版本"已改读真实标题栏而非文件名。
> - `inspect_drawing_sheet` 的图框 id/模板名从候选属性名（图框/FRAME/DrawingFrame/frame_id、模板/TEMPLATE/…）嗅探，真实 key 待客户图框样本确认。
> - `inspect_layers` 只能报**图层号**：NX2412 的 `Part.Layers`(LayerManager)/UFLayer 实测无反查图层名通道；规则侧 `LAYER-002` 对纯数字图层做占位守卫（不阻断），待名称通道或客户样本后收紧。
> - `inspect_sheet_thickness` 为**启发**：`method=solid_bbox_min_dim`（实体包围盒最小边取全局最小），钣金壁/属性读取待有样件；响应里明确标注非权威。
> - `inspect_hole_pattern` 为**几何聚类**：同径 + 归一化轴向分组 ≥3 且径向散布 <15% 均值判圆形孔组（PCD=2·平均半径，组名 `HOLE_GROUP_<径>_<数>`），螺栓对照表匹配仍归规则侧。
> - `undo_last_assistant_change`（`UndoOps.AssistantMarks`）：每次写成功记 (undo mark id, 具名, 模型指纹=特征名/表达式值/体名 SHA1)，撤销需 ①`NewestVisibleUndoMark` 属助手或在 no-op 白名单（rebuild/move-regen）②当前指纹==写入后快照，任一失配即拒并给中文原因；账本进程内、按部件路径最多 8 条、NX 重启清空——§1.2"绝不误撤用户操作"由双校验兑现。
> - `extract_evidence` 证据补 `part.plate_thickness`/`part.hole_patterns` 与整段 `drawing`；规则引擎对缺失段自然跳过，审图判定零回归。

---

## 4. 规则 ↔ 工具挂钩矩阵（防 + 查 同一套规则包）

| 规则 ID | 创作面（画图拦截，commit 前 block） | 质量面（`review_*` 审图） | 数据来源工具 |
|---------|-----------------------------------|--------------------------|-------------|
| HOLE-DIA-001 | `create_cylindrical_hole` commit 前校验直径∈系列 | 抽所有孔特征比对 | `inspect_feature`/`get_part_summary` |
| HOLE-BOLT-002 | 同上（标注 M 时按通孔表） | 比对 | 同上 |
| HOLE-THD-003 | 螺纹孔规格白名单 | 比对 | `inspect_feature` |
| HOLE-DUP-004 | （warn 不阻断） | 同直径≥4 警告 | `get_part_summary` |
| SKETCH-001/002 | `extrude_sketch`/`create_parametric_sketch` 成型前校验 | 比对 | `inspect_sketch` |
| FEAT-EXT-001 | `extrude_sketch` distance>0 有限 | — | 入参校验 |
| FEAT-FILLET-002 | `fillet_edges`（warn） | — | 入参校验 |
| BODY-001/002 | — | 空图 fail / 多余片体 warn | `inspect_work_part_geometry` |
| FRAME-001 / TITLE-001~004 / VIEW-001 | — | 工程图字段比对 | `inspect_title_block`/`inspect_drawing_sheet` |
| LAYER-001/002 | — | 图层比对 | `inspect_layers` |
| DIM-001 | — | 外形尺寸完整性(warn) | `inspect_drawing_annotations` |
| FILE-001 | 所有文件工具路径沙箱 | `review_folder` 目录沙箱 | 宿主 |
| PLATE-THK/STEEL/WELD/BOM/FLANGE/PIN/SENSOR/EDGE-SAFE | 视子包 | 华恒子包 | 对应 `inspect_*` |

- 力度 `block/warn/off` 可按阶段配置；报告与工具错误都带 `standard` 字段（`company_v3` 或 `company_v3+huaheng_logistics_robot_v1`）。
- 硬规则程序判；`φ13.2` 金样必须稳定失败（PRD §14）。
- **占位数值不得写死进 C#**（华恒板厚/型钢/法兰等待客户提供，PRD 风险表）。

---

## 5. 分批实现顺序（逐步提交，不攒代码）

> 进度（2026-09-22）：批 1 骨架 ✅（8d304a0）；批 2 授权 ✅（0188b1a）；批 4 规则引擎部分 ✅（d747436）+ 批 6 金样 5 件 ✅（f9f88da）；批 3 读工具首切片 ✅ **实机联调通过**（a63a351+eda698b：主线程 marshal + `get_part_summary`/`inspect_work_part_geometry`/`save_work_part`/`create_part`；leaveOpen 版插件已在 pid 16556 复验全绿；旧 NX-MCP 桥已摘除）；批 3 拓扑/特征第二切片 ✅ **实机全绿**（`TopologyOps`：`inspect_body_topology`/`resolve_topology`(stable_id+几何回退+sort_by)/`inspect_feature`/`rebuild_work_part`，smoke_live 阶段 C 在金样板上验证回环；PipeServer 断开噪音与 Dispose 修复已部署复验）；批 4 写操作第三切片 ✅ **实机已验证**（`WriteOps`：`create_block`/`create_parametric_sketch`/`inspect_sketch`/`extrude_sketch`/`set_feature_expression`，smoke_live 阶段 D 跑通 block→矩形草图(自动约束+WIDTH 尺寸)→拉伸→表达式改名 40→25→rebuild→save 全闭环，写工具受 `LICENSE_INVALID` 闸门保护）；批 4 `create_cylindrical_hole` + **HOLE-DIA-001 写前守卫** ✅ **实机已验证**（宿主 `HostRules`：commit 前以候选孔证据跑 `RuleEngine.Blocking`，φ13.2 拒绝并建议 13/14、NX 侧零改动；规则包缺失 fail-closed；smoke_live 阶段 E 全绿）；批 4 `fillet_edges`/`chamfer_edges` ✅ **实机已验证**（`EdgeBlendBuilder.AddChainset`/`ChamferBuilder` SymmetricOffsets，edge_indices 与拓扑 index 同源；FEAT-FILLET-002 warn 级守卫以 `rule_warnings` 随成功响应附带，smoke_live 阶段 F 全绿）；批 4 `move_object` ✅ **实机已验证（回滚护栏路径）**（`MoveBodyBuilder`+`CreateRuleBodyDumb`+`ModlMotion.DeltaXyz`，提交后 UF 包围盒位移回读，>1e-3 失配即 UndoToMark 回滚——block/extrude 特征驱动实体实测“提交成功但 0 位移”，护栏兑现；非特征实体正路径待 import_exchange 批次补测；smoke_live 阶段 G 全绿）；批 4 `export_exchange` ✅ **实机已验证**（`DexManager.CreateStepCreator` STEP 导出，V1 仅 STEP（PRD §9.1），`Workspace.ResolveExchange` 扩展 FILE-001 沙箱至 `.stp/.step`+overwrite 语义+导出文件回读；smoke_live 阶段 H 全绿，逃逸/parasolid/重名三类拒绝+8132 字节 ISO-10303 落盘）；批 4 `import_exchange`（§2 追加行）✅ **实机已验证**（StepImporter→WorkPart，回读护栏=实体增量>0 且 update_errors==0；smoke_live 阶段 I 全绿并打通 STEP 往返。**连带实测修正**：STEP 导入件仍带 `ImportedModel` 特征，Move Body 对它同样 0 位移→护栏回滚兑现；move_object“正路径待补测”一项关闭——V1 白名单内不存在可移动的静态体，工具价值定为安全尝试+可解释回滚）；批 5 审图长任务 ✅ **实机已验证**（宿主 `ReviewOrchestrator`：`review_folder` 立即返回 run_id+后台串行、run.json/findings.jsonl 落 `%LOCALAPPDATA%\NXAssistant\runs\<run_id>\`、状态机含 interrupted 滞留判定与 `resume_run_id` 续跑、`review_status(cancel=true)` 请求停止、单张失败入 failed_files 整批继续；ClosedXML 出 `审图报告.xlsx`（摘要+明细，列对齐 §7.5）+ `summary.json`；插件内部方法 `extract_evidence` 非显示打开沙箱 `.prt` 抽 Part 段证据即关并恢复 Work part；`HoleEvidence.FeatureName` 让 finding 主题带孔特征名。smoke_live 阶段 J 五张金样全绿：φ13.2→HOLE-DIA-001 阻断+建议 13/14、dup4→HOLE-DUP-004 warn、pin/sensor→华恒子包 placeholder、空图→BODY-001 阻断、沙箱外目录直拒、审图后工作部件不变；工程图段证据（title_block/sheet/layers 等）按 §9.2 批次接入后同一状态机自动补齐）；批 5 抽取类 + 撤销 ✅ **实机全绿**（§3 九工具：8 个 `inspect_*` 抽取器 + `undo_last_assistant_change`，宿主达 33 工具；图纸侧按负路径验收、板厚/孔组为标注启发、图框/标题栏/图层名/别名为占位待客户样本——详见 §3 实现说明。撤销=具名 mark+模型指纹双校验、no-op 白名单、进程内账本（MaxDepth 8）。`extract_evidence` 证据补 plate_thickness/hole_patterns/drawing 段且阶段 J 判定零回归；findings 行改读真实标题栏图号/版本。smoke_live 阶段 K 全绿：无账本直拒→block→撤销 body 归 0→block→rebuild→再撤销（no-op mark 不阻断链）→撤销到底再拒；板厚启发=12、图层 `{'1': 1}`、三图纸抽取器空证据正常；stdio 33 工具清单核对通过）；批 5 后托盘批次 ✅ **实机已验证（零回归）**（PRD §6 交付形态补齐：`NxAssistant.Tray`→`NxAssistant.exe` 托盘+设置窗，与宿主分离进程；`StatusProbe` 只读四态=授权(`LicenseManager.Check(path)`)+NX 连接（枚举 `\\.\pipe\` 命名空间，不发 IPC 请求避免抢插件单 worker 队列）+MCP 进程+runs 内 running；`Core/NxaSettings` 统一三组件解析优先级 **env > settings.json > 默认**（workspace/rules_dir/license_path/mcp_exe），插件 `Workspace.Root` 与宿主 `HostRules`/`ReviewOrchestrator`/`LicenseService` 同口径（FILE-001 沙箱根写侧/校验侧不分叉）；导入 Key、复制 MCP 配置片段、设置分页、`--status-json` 无头模式；`build.ps1` 发布 `dist/tray`。smoke_tray+settings 往返+stdio+规则金样离线全绿；因插件 Workspace 有改动重跑 **smoke_live 全量 A–K 零回归**，NX 在线态托盘探测 `plugin_online=true`）；V1 白名单 33 工具 + 三组件交付面至此全部落地，余下为客户校准与安装包。

按"先跑通骨架 → 再补读 → 再补写 → 最后审图长任务"推进，每批一个可验证闭环、独立提交：

1. **骨架**：三组件解决方案（`NxAssistant` 托盘 net8 / `NxAssistant.Mcp` 宿主 net8 + 官方 ModelContextProtocol NuGet / `NxAssistant.NxPlugin` net48 NXOpen 插件）+ 本机 IPC（命名管道，令牌）。客户已确认旧 NX-MCP 桥可删除、无需共存，插件直接部署到 startup 目录即可。**跑通 `ping` 一条链**（Agent→MCP→IPC→插件→NX→回）。
2. **授权**：`license_status` + 签发工具（独立 `licensing/` 文件夹，私钥不进客户包）+ create/review 的 `LICENSE_INVALID` 闸门。
3. **读工具**：`get_part_summary`、`inspect_*`、`resolve_topology`（含 stable_id 体系）、`rebuild_work_part`、`save_work_part`、`create_part`、`export_exchange`。
4. **写工具 + 规则引擎**：`create_block`→`extrude_sketch`→`create_cylindrical_hole`（+ commit 前规则拦截，φ13.2 金样）→`create_parametric_sketch`/`inspect_sketch`→`fillet`/`chamfer`→`set_feature_expression`→`move_object`（回读样板）。规则引擎读 company_v3 JSON。
5. **审图长任务**：`review_folder`/`review_status`/`review_findings`（run_id 状态机 + xlsx 报告）+ 抽取类 `inspect_*`（title_block/sheet/layers/weld/parts_list/thickness/hole_pattern）+ `undo_last_assistant_change`。
6. **测试图纸集**：在 `test/`（新建）用工具造样件——全通过件、φ13.2 失败件、错误图框件、缺字段件等（`CHECKLIST_20.md` 建议），`.prt` 保留不删，作为验收金样。

### 已确认（客户 2026-09-22 决策）
- `move_object` **进 V1**（V1 共 34 个工具）。
- 审图报告 xlsx 生成库 = **ClosedXML**（MIT）。
- 旧 NX-MCP 桥**可删除**，不需要共存；插件直接部署到 NX startup 目录。

### 待确认（实现到相应批次前问）
- 华恒真实图框 id、板厚/型钢/法兰正式表（客户提供前用可配占位）。
