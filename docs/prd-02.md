Agent **不会在你电脑里“监听 NX”**，它只看 MCP 工具的返回值（以及你是否主动再问一次）。所以「审完了 / 中断了」必须变成 **工具说得清楚的状态**，不能指望模型自己猜。

---

###  推荐：先开工单，再查状态（V1 就按这个做）**

```text
① Agent → review_folder(path)
   立即返回：{ run_id: "20260922-001", status: "running", total: 120 }

② Agent 过一会儿 → review_status(run_id)
   返回：{ status: "running", done: 47, total: 120, current: "P-21007-03.prt" }

③ 再查：
   completed / failed / cancelled / interrupted
```

Agent 怎么“知道”，就是 **反复调用 `review_status`**（有的客户端还能收 MCP 进度通知，那是加分，不能当唯一手段）。

---

### 状态要设计成机器能读的枚举

| status | 含义 | Agent 该做什么 |
|--------|------|----------------|
| `queued` | 已受理，NX 还没开始 | 稍后重查 |
| `running` | 正在一张张打开 | 告诉用户进度，继续查 |
| `completed` | 全部跑完（允许部分图有 finding） | 读报告，向用户汇报 |
| `completed_with_errors` | 跑完但有文件打不开 | 汇报成功数+失败文件清单 |
| `failed` | 整批无法继续（没 NX、没授权、目录不存在） | 把 error 告诉用户，不要假装审完 |
| `cancelled` | 用户/Agent 调了取消 | 说明已停，报告可能不完整 |
| `interrupted` | 进程被杀、NX 崩溃、主机重启 | 说明做到第几张，能否续跑 |

另外始终带：

- `run_id`  
- `done` / `total`  
- `report_path`（有才给）  
- `error_code` / `error_message`  
- `can_resume`（中断后能否从下一张继续）

「审核完成」= `status == completed`（或 `completed_with_errors`），不是 Agent 感觉时间够了。

---

### 中断是怎么发生的、怎么通知 Agent

| 实际情况 | 小助手内部 | Agent 何时知道 |
|----------|------------|----------------|
| 用户点托盘「停止审核」 | 标 `cancelled`，尽量保存已扫结果 | 下次 `review_status` 看到 cancelled |
| NX 崩溃 / 插件掉线 | 标 `interrupted`，记下最后文件 | 同上；`ping` 也会失败 |
| MCP 进程被关掉 | 工单落在磁盘上，状态文件仍在 | Agent 再调 `review_status` 或 `review_folder` 会读到旧 run；进程没了则调用报错 |
| 对话超时，Agent 没等完 | 审核可能仍在后台跑 | Agent **必须**用 run_id 再查，不能当失败 |
| 单张 PRT 损坏 | 该张记失败，整批继续 `running` | 最终 `completed_with_errors` |

所以：**中断不是靠 Agent 感应，是靠小助手把状态写到本机（内存 + 一个 run 文件），供下一次工具调用读取。**

---

### Agent 侧典型对话（你要在工具 description 里写清楚）

1. 调用 `review_folder` → 拿到 `run_id`。  
2. 对用户说「已开始，共 120 张」。  
3. 间隔调用 `review_status`（例如每轮对话或客户端进度）。  
4. 看到 `completed` → `review_findings` 或直接用返回里的摘要。  
5. 看到 `failed` / `interrupted` → 原样告诉用户原因，问是否 `resume`。

系统提示里应写死：

> 审图是长任务。不要假设一次 tool 返回就结束。必须看 status 字段。running 时继续 review_status。不要对目录里每个文件单独 inspect。

---

### 和「传递目录」的关系

你的理解对了一半：

- **发起**：目录传给小助手。  
- **结束/中断**：不是目录自己回调 Agent，而是  
  - 同一次调用的最终 JSON，或  
  - 之后用 `run_id` 查询。

V1 建议：`review_folder` **立刻返回 run_id**（目录很大时不要同步堵死）；短目录也可以选择同步等完，但返回结构仍要带同一套 `status`，方便 Agent 统一处理。

**一句话：Agent 只通过工具返回值知道审完还是中断；小助手负责把每张进度写成 status + run_id，断了也能查。**