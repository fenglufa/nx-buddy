# company_v3 标准包

NX 小助手 V1 规则数据。画图拦截与本地目录审图共用。

## 目录

- pack.json 包清单与子包引用
- rules.json 通用 20 条
- hole_series.json / fillet_series.json / drawing_frames.json / layers.json / part_number_pattern.json / title_block_fields.json
- CHECKLIST_20.md
- huaheng_logistics_robot_v1/ 华恒结构件 + 物流机器人子包
- 本目录是**只读基准包**：升级整目录替换。用户的规则开关/参数改动不写这里，
  写 `%LOCALAPPDATA%\NXAssistant\rules_state.json` 覆盖层（托盘"规则"分页管理，含变更日志）。

报告 standard 字段：
- 仅通用：company_v3
- 启用华恒子包：company_v3+huaheng_logistics_robot_v1

标「占位」的数值可运行，上线前换成华恒正式表，不要写死进 C#。
