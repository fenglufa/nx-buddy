# 审图测试样件（保留，勿删）

用 NX 2412 实机生成的 `.prt` 金样（先经旧桥造在 E:\NX-MCP\workspace，再拷入本目录持久化）。
每个样件对应规则引擎的**预期 findings**，作为后续"证据抽取 + review_folder 长任务"批次的验收标准。
判定逻辑本身已由 `tests/NxAssistant.Rules.Tests` 离线覆盖；本目录覆盖的是"从真实 .prt 抽出正确证据"这一环。

| 文件 | 几何 | 预期判定 |
|------|------|----------|
| `NXA_TEST_OK_plate.prt` | 100×60×8 板 + 2×φ10 通孔（特征名 NXA_HOLE_PH10/_B） | 无阻断项（φ10 在系列内；孔数<4 不触发重复孔） |
| `NXA_TEST_BAD_hole132.prt` | 100×60×8 板 + 1×φ13.2 通孔（NXA_HOLE_PH132） | **HOLE-DIA-001 阻断**，建议邻近 13/14（验收金样） |
| `NXA_TEST_WARN_dup4.prt` | 120×80×10 板 + 4×φ10 独立孔（NXA_HOLE_A..D，非阵列） | HOLE-DUP-004 **警告**（同径≥4 独立孔）；无阻断 |
| `NXA_TEST_HUAHENG_pin_sensor.prt` | 100×40×25 块 + φ15 孔（NXA_PIN_15）+ φ5.0 孔（NXA_SENSOR_5） | PIN-BORE-001 **阻断**（15 不在 10/12/16…系列，建议 12/16）；SENSOR-HOLE-001 **警告**（5.0 不在 4.5/5.5/6.6）；两规则标 placeholder |
| `NXA_TEST_BAD_empty.prt` | 空部件（无实体） | **BODY-001 阻断**（空图） |

## 约定

- 特征名前缀承载语义：`NXA_PIN_*`=销轴孔、`NXA_SENSOR_*`=传感器安装孔；
  其余圆孔默认按 simple_through 系列判定。证据抽取层（inspect_hole_pattern 等批次）沿用此命名契约。
- 孔均为 Z 向通孔（深度大于板厚）。
- 工程图类规则（FRAME/TITLE/LAYER/VIEW）的样件需要图框模板与标题栏属性，
  待客户确认真实图框 id 后补 2D 图纸样件（见 `docs/tool-migration-v1.md` §5 待确认）。
