# FlexBot 优化路线图

本文记录 **宿主主体（FlexBot 核心）** 的优化事项：每条都带**实测证据**与**验证方式**，不是泛泛的愿望清单。
按"收益 / 成本"排序，随时可以从中挑一项开工。

## 已完成（基线）

| 提交 | 内容 | 实测结果 |
|---|---|---|
| `cf2a5b1` | ① 磁盘卫生：依赖进程日志轮转（20MB 上限）、宿主日志保留 14 天、清理废弃目录 | `memory/` **410.3MB → 79MB**；`logs/llbot.log` 119MB 单文件问题根治 |
| `8c586e6` | 注释清理 + 死代码删除 | 过时注释 2 处已修；复述式注释全仓命中 **0**；`ProcessManager` 里"启用 ANSI"实为空操作（原模式原样写回），连其 Win32 包装共 42 行已删，**187 → 142 行** |
| `f3800d7` | WebView2 用户数据目录隔离 | 根治"多开 Photino 程序互抢 ⇒ 黑屏/崩溃"，两个程序并发各自 5+1 个 WebView2 进程 |
| `d9b6586` `423f016` | ANSI 控制符支持（控制台开 VT + WebUI 日志页解析） | 8 条单测通过；非 TTY 剥色仍待做（见 ③） |

## ② 拆分巨型文件（长期收益最大）

**现状证据**

- `PluginManager.cs` **1578 行**：一个类同时负责插件发现、ALC 加载与卸载、设置读写与加密、zip 安装与安全解压、NuGet 依赖解析、清单校验、命令注册
- `WebUi.cs` **870 行**：37 个端点内联在同一个方法里
- `Program.cs` **521 行**：启动编排 + 路由 + 命令分发 + 身份绑定挤在一起

**动作**：`PluginManager` → `PluginLoader`（发现/ALC/依赖）+ `PluginSettings`（读写/加密）+ `PluginInstaller`（zip 安装与安全解压）+ `PluginCommands`（注册与派发）；`WebUi.cs` 按页面拆端点模块（status / plugins / config / logs / messages）

**验证**：拆分前后 `dotnet test` 全绿；插件侧零感知（公开方法签名不变）；WebUI 37 个端点冒烟全通

**风险控制**：纯移动代码、不改逻辑；一步一提交

## ③ 日志管道

**现状证据**：`TimestampWriter.Write(char)` **逐字符**处理；`LogStore` 每行 `AutoFlush=true` **同步刷盘**；stdout 被重定向（管道/文件）时**仍带 ANSI 色码**

**动作**：行级缓冲（攒够 N 行或 ≥100ms 批量刷盘）；非 TTY 走 `AnsiConsole.Strip`；环形缓冲与日志文件分开存（WebUI 保留颜色）

**验证**：灌 10 万行对比吞吐；日志文件无 ESC 字符；WebUI 日志页颜色仍在

## ⑤ 测试补面

**现状证据**：仅 `CoreTests.cs`（16 个用例，集中在 SecretBox / 插件名门禁 / 去重 / 配置加密）

**动作**：补四组 —— WebUI 端点冒烟（status/stats/messages/plugins/settings 的鉴权与返回体）、**zip 安装防目录穿越**（实现有、测试无）、命令派发与管理员门禁、配置迁移（旧字段 → 插件设置）

**验证**：`dotnet test` 全绿；CI（`.github/workflows/build.yml`）自动跑

## ④ 可观测性补完

**现状证据**：AI **token / 费用无聚合**（每次请求的 usage 只进日志）；无插件耗时与异常排行；`/healthz` 只看连接状态

**动作**：Abstractions 增一个极小的 `IBotMetrics`（宿主实现，`ctx.Metrics`）；Agent 在响应回调里上报 prompt/completion tokens；WebUI 仪表盘加统计块；`/healthz` 增磁盘与内存指标

**验证**：跑一轮 AI 对话后 `/api/stats` 出现 token 计数；`/healthz` 含磁盘指标

## ⑥ 小项（顺手做）

- `IdMap` 加容量上限与淘汰（官方通道身份映射会一直增长）
- `MessageDedup` 淘汰改环形，去掉 `OrderBy` 的 O(n log n)
- WebUI 加响应压缩与 CSRF token
- 插件目录加文件监听（改动自动 reload，默认关闭）

## 数据层（最大功能缺口，单独立项）

群消息落 SQLite（**FTS5** 全文检索），解锁「谁说过 X」「每日群聊日报」「Agent 记忆关键词检索」。
选 SQLite 的理由：零外部依赖、单文件可备份；且官方通道**没有历史消息接口**，自行落库是唯一解。

## 明确不做

自研向量库 / 本地 embedding；多实例管理后台；更换 WebUI 前端框架；Photino 换 Electron；
重构 vendored 的 `FlexBot.OneBotLib/OneBotClient.cs`（1080 行协议层，不动更安全）
