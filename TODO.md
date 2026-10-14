# FlexBot TODO

> 唯一的待办清单 —— 合并自原 `docs/roadmap.md` 与记忆/聊天记录设计里的阶段计划。
> 设计细节不堆在这里，只给链接：[记忆与聊天记录设计](docs/memory-and-history-design.md)。
> 每一条都带**实测证据**与**验收方式**，不是愿望清单。

## 已完成（基线）

| 提交 | 内容 | 实测结果 |
|---|---|---|
| `cf2a5b1` | 磁盘卫生：依赖进程日志轮转（20MB）、宿主日志保留 14 天、清理废弃目录 | `memory/` **410.3MB → 79MB**；`logs/llbot.log` 单文件 119MB 问题根治 |
| `8c586e6` | 注释清理 + 死代码删除 | 过时注释 2 处已修；复述式注释全仓命中 **0**；`ProcessManager` 里"启用 ANSI"实为空操作，连同其 Win32 包装删除 **42 行**（187 → 142） |
| `f3800d7` | WebView2 用户数据目录隔离 | 根治"多开 Photino 程序互抢 ⇒ 黑屏/崩溃"；两个程序并发各自 5+1 个 WebView2 进程 |
| `d9b6586` `423f016` | ANSI 控制符支持（控制台开 VT + WebUI 日志页解析） | 8 条单测通过；非 TTY 剥色仍待做（见 ③） |
| `9c6245c` | FileSystem 搜索/替换补强 | 任意后缀目录搜索、目录级替换、`--glob/--ignore-case/--context`、默认预览 + `--apply`，端到端实测通过 |
| `ea90153` `35f8651` `fe0d563` `09868d1` | 文档：路线图 + 记忆与聊天记录设计（Mermaid 图）+ 隔离威胁模型 + 会话 id 待办 | 381 行 / 12 图，围栏平衡，特殊字符标签 0 处 |

## 待办（按建议顺序）

### 代码质量

- [ ] **② 拆分巨型文件** —— 现状：`PluginManager.cs` **1578 行**（加载/ALC + 设置 + zip 安装 + NuGet + 清单 + 命令混杂）、`WebUi.cs` **870 行**（37 个端点内联）、`Program.cs` **521 行**。
      动作：拆成 `PluginLoader` / `PluginSettings` / `PluginInstaller` / `PluginCommands`；WebUI 端点按页面分模块。
      验收：拆分前后 `dotnet test` 全绿，插件侧零感知（公开签名不变），37 个端点冒烟全通。纯移动代码，一步一提交。
- [ ] **③ 日志管道** —— 现状：`TimestampWriter.Write(char)` **逐字符**、`LogStore` 每行 `AutoFlush=true` **同步刷盘**、stdout 被重定向时**仍带 ANSI 色码**。
      动作：行级缓冲（≥N 行或 ≥100ms 批量刷）；非 TTY 走 `AnsiConsole.Strip`；环形缓冲与日志文件分开存（WebUI 保留颜色）。
      验收：灌 10 万行对比吞吐；日志文件无 ESC；WebUI 日志页颜色仍在。
- [ ] **⑤ 测试补面** —— 现状：仅 `CoreTests.cs`（16 用例，集中在 SecretBox / 插件名门禁 / 去重 / 配置加密）。
      动作：补 WebUI 端点冒烟（鉴权与返回体）、**zip 安装防目录穿越**（实现有、测试无）、命令派发与管理员门禁、配置迁移。
      验收：`dotnet test` 全绿，CI 自动跑。
- [ ] **④ 可观测性补完** —— 现状：AI **token/费用无聚合**；无插件耗时/异常排行；`/healthz` 只看连接状态。
      动作：Abstractions 加极小的 `IBotMetrics`（宿主实现、`ctx.Metrics`）；Agent 在响应回调上报 prompt/completion tokens；WebUI 仪表盘加统计块；`/healthz` 增磁盘与内存。
      验收：跑一轮 AI 对话后 `/api/stats` 出现 token 计数；`/healthz` 含磁盘指标。
- [ ] **⑥ 小项** —— `IdMap` 加容量上限与淘汰（官方通道身份映射会一直涨）；`MessageDedup` 淘汰改环形（去掉 `OrderBy` 的 O(n log n)）；WebUI 加响应压缩与 CSRF token；插件目录加文件监听（改动自动 reload，默认关闭）。

### 数据层：记忆与聊天记录

> 完整设计（架构图 / 时序图 / ER 图 / 隔离威胁模型 / 向量库取舍）：[docs/memory-and-history-design.md](docs/memory-and-history-design.md)

- [ ] **会话重置：允许 agent「遗忘」当前会话（不清空记忆）** —— 现在只有"清空记忆"这一种破坏性操作，缺一个安全的"丢上下文"。
      **可立刻动手，不依赖 P1**（当前内存实现清 `_convHist[key]` 即可）。
      动作：① 新增 `session_reset` 工具（模型可调）+ `!reset` 命令（按现有权限模型）；② 影响范围**只有短期上下文**；
      ③ **绝不动** `mem` 长期条目、`conversation_*.md` 摘要、消息流水；④ 重置后同一回合重建必带块（global + 高重要度条目 + 最近摘要），避免"以为记得"；
      ⑤ 频控（每会话 N 分钟内最多一次）+ 留痕（日志写清谁在何时重置了哪个会话）；⑥ 回执明确写"记忆已保留"。
      安全：工具签名**不含任何删除类参数** —— 模型无法借它清记忆（与 §5 的"工具不带 scope"同一原则）。
      验收：重置后模型不再知道刚聊过的细节，但被问"我们之前聊过什么"仍能从摘要/条目答出；整个过程**零数据删除**。

- [ ] **P1 宿主落库** —— 关注群 + 私聊的消息流水落 SQLite 单文件（`memory/flexbot.db`）、批量事务写入（每 ~200ms 或 200 条）、保留期（群 90 天 / 私聊 180 天）、WebUI 只读页（条数 / 体积 / 最近消息）。
      范围：**只存关注群与私聊**，未关注群不存；`no_llm`（不回复）≠ 不存储，两者独立。
      验收：发消息后在 WebUI 可见；`memory/` 体积随保留期稳定；导入 1 万条压测写入不阻塞。
- [ ] **P2 检索** —— `msg_fts`（FTS5，**必须 `tokenize='trigram'`**，默认 `unicode61` 不切中文）+ `history_search` / `history_recent` 工具；会话历史与"必带注入"改为从库取近 N 轮（同时修掉"超限就丢"）。
      安全：**工具签名不含 `scope` 参数**，scope 由宿主从 `TurnContext` 注入（见设计 §5 注）。
      验收：能答"上周谁说过 X"；跨 scope 检索被拒。
- [ ] **P3 增强** —— 首次关注群用 `GetGroupMsgHistoryAsync` 分页补拉历史；`SummarizeDayAsync` 改走库存（不再受单次 1500 条分页上限）；按天总结顺带产出结构化 `mem` 条目（复用同一次模型调用，零额外成本）。
      验收：新关注的群立即有历史；日报覆盖全天消息。
- [ ] **P4（可选项）会话 id + 关联/分离** —— 每个会话分配稳定短 id（`s_xxxxxx`），主人可用 `!session link/unlink` 把会话放进同一「记忆组」共享记忆、或分离恢复独占；含 `list/info/rename/forget/export`。
      设计：[§7.3](docs/memory-and-history-design.md)。**不阻塞 P1–P3**，可随时插入或跳过。
      防护（最容易出事）：关联 `private → group` 等于把私聊暴露给该群 ⇒ **默认禁止 + 显式二次确认 + 审计留痕 + WebUI 风险标记**；关联只增不减权限。
      验收：关联后两边记忆互通、分离后历史互不可见；越权关联被拒并留下审计。

## 明确不做

自研向量库 / 本地 embedding（真要做也只对 `mem` 条目嵌入，不对全量消息，见设计 §8.1）；
多实例管理后台；更换 WebUI 前端框架；Photino 换 Electron；重构 vendored 的 `FlexBot.OneBotLib/OneBotClient.cs`（1080 行协议层）；
全量收集所有群；按天分目录存 JSONL 再靠 grep 检索；每条消息同步 fsync；把私聊内容送远端做 embedding。
