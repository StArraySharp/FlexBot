# 记忆与聊天记录：存储与读取设计

> 目标：让 Agent 能**记得住**、**查得到**；范围只覆盖**关注群**与**私聊**，不做全量收集。
> 本文所有"现状"结论都来自实际代码与实测数据（见文末「依据」）。

## 1. 目标与边界

| 要解决 | 不要做 |
|---|---|
| 会话历史重启即失（现在只在内存里） | 全量收集所有群（隐私 + 磁盘） |
| 长期摘要只注入"最近一段"，早先内容永不出现 | 把全部历史塞进 prompt |
| 结构化记忆写了却没人读（模型得猜文件名） | 每条消息同步落盘 |
| "上周谁说过 X" 答不了 | 引入外部数据库/服务（SQLite 单文件足够） |

## 2. 总体架构

```mermaid
flowchart LR
    subgraph 通道层
        A1["onebot 通道<br/>LLBot / NapCat"]
        A2["official 通道<br/>QQ 官方机器人"]
    end
    subgraph 宿主核心
        B["Ingress 归一化<br/>scope 判定 + 去重"]
        C["MessageStore<br/>SQLite 单文件 memory/flexbot.db"]
        D["MessageRouter<br/>IMessageHub"]
    end
    subgraph 插件层
        E["Admin / Luck / …"]
        F["Agent 插件<br/>ChatPlugin"]
    end
    G["LLM 提供方"]
    A1 --> B
    A2 --> B
    B -->|"先落库"| C
    B -->|"再派发"| D
    D --> E
    D --> F
    F <--> G
    C -->|"history_recent / history_search"| F
    C -->|"IMessageStore"| E
```

要点：**存储挂在派发之前**。插件抛异常、短路、不回复，都不影响记录；存储也不依赖任何插件是否加载。

## 3. 写入路径

```mermaid
sequenceDiagram
    participant T as 通道 onebot/official
    participant I as Ingress 归一化
    participant P as 策略过滤
    participant S as MessageStore
    participant R as MessageRouter
    participant G as Agent 插件
    T->>I: 收到消息事件
    I->>I: 转 MsgRecord，算 scope
    I->>P: 判断是否入库
    alt 关注群 或 私聊，且不在黑名单
        P->>S: 入队（批量事务）
        S-->>P: ok
    else 未关注群 / 已拉黑
        P-->>I: 跳过
    end
    I->>R: 派发（无论是否入库）
    R->>G: 交给插件处理
    Note over S: 每 200ms 或攒够 200 条 flush 一次
```

```mermaid
flowchart TD
    M["消息事件"] --> N["归一化<br/>transport / scope / message_id / ts / text / raw_json"]
    N --> F1{"scope 在关注列表?"}
    F1 -- 私聊 --> K1{"该用户已拉黑?"}
    F1 -- 关注群 --> K1
    F1 -- 未关注群 --> SKIP["不入库"]
    K1 -- 否 --> Q["写入队列"]
    K1 -- 是 --> SKIP
    Q --> B["批量事务 flush"]
    B --> U["UNIQUE 冲突则忽略<br/>重投与补拉天然去重"]
```

## 4. 存储模型

```mermaid
erDiagram
    MSG ||--o| MSG_FTS : "全文索引"
    MSG ||--o{ MEM : "可由消息抽取"
    SCOPE ||--o{ MSG : "归属"
    SCOPE ||--o{ MEM : "归属"
    MSG {
        int id PK
        string transport
        string scope
        string message_id
        int ts
        int user_id
        string nick
        string text
        string raw_json
        int recalled
    }
    MEM {
        int id PK
        string scope
        string kind
        string text
        string tags
        int importance
        int created_at
        int updated_at
        string source_msg_ids
    }
    SUMMARY {
        string scope
        string day
        string text
    }
    SCOPE {
        string scope PK
        string kind
        int store_enabled
        int keep_days
    }
```

```sql
-- 原始流水（关注群 + 私聊）
CREATE TABLE msg(
  id INTEGER PRIMARY KEY,
  transport TEXT,        -- onebot | official
  scope     TEXT,        -- group:748394300 | private:2794805076
  message_id TEXT,
  ts INTEGER,            -- 秒
  user_id INTEGER, nick TEXT,
  text TEXT,             -- 纯文本化：@昵称、图片→[图片]
  raw_json TEXT,         -- 原样存档，以后加字段不必重新采集
  recalled INTEGER DEFAULT 0,
  UNIQUE(transport, scope, message_id, ts)   -- 特意带上 ts：onebot 的 message_id 是 int32，机器人重启后可能重复
);
CREATE INDEX idx_msg_scope_ts ON msg(scope, ts);

-- 中文检索：默认 unicode61 不切中文，必须用 trigram
CREATE VIRTUAL TABLE msg_fts USING fts5(text, content='msg', content_rowid='id', tokenize='trigram');

-- 长期记忆条目（替代散落的 md）
CREATE TABLE mem(
  id INTEGER PRIMARY KEY,
  scope TEXT, kind TEXT,   -- kind: rule | preference | fact | event | relation
  text TEXT, tags TEXT,
  importance INTEGER, created_at INTEGER, updated_at INTEGER,
  source_msg_ids TEXT
);

-- 滚动摘要
CREATE TABLE summary(scope TEXT, day TEXT, text TEXT, PRIMARY KEY(scope, day));
```

人类可读性：`mem` 变更时**单向导出**到 `memory/export/<scope>/*.md`（只写不读），你仍能直接看和手改。

## 5. 读取路径（两段式）

```mermaid
flowchart TD
    Q["一次对话请求"] --> A["第一段：必带块（不检索，约 400 token）"]
    A --> A1["global 与当前 scope 的高重要度条目<br/>如 bot_rules / owner-preference"]
    A --> A2["最近 N 轮对话（来自库，不再只靠内存）"]
    A --> A3["最近一条滚动摘要"]
    A1 --> B{"模型觉得信息不够?"}
    A2 --> B
    A3 --> B
    B -- 是 --> C["工具调用 memory_search / history_search"]
    C --> D["FTS5 + BM25 排序"]
    D --> E["跨来源去重 + 预算截断<br/>importance × exp(-age/τ) × 命中次数"]
    E --> F["拼进上下文"]
    B -- 否 --> F
```

```mermaid
flowchart LR
    subgraph "必带 · 稳定"
        X1["人格与规则"]
        X2["近 N 轮"]
        X3["最近摘要"]
    end
    subgraph "按需 · 可检索"
        Y1["mem 长期条目"]
        Y2["msg 原始流水"]
        Y3["summary 历史摘要"]
    end
    X1 --> Z["上下文预算 ≤ 1200 token"]
    X2 --> Z
    X3 --> Z
    Y1 --> Z
    Y2 --> Z
    Y3 --> Z
```

Agent 获得四个工具：`history_search` / `history_recent` / `memory_write` / `memory_forget`。

## 6. 保留期与清理

```mermaid
flowchart LR
    S1["宿主启动时"] --> C["清理任务"]
    S2["每 6 小时"] --> C
    C --> P1["群消息 90 天"]
    C --> P2["私聊 180 天"]
    C --> P3["宿主日志 14 天"]
    C --> P4["依赖进程日志 20MB 轮转"]
    C --> P5["废弃目录与孤儿影子目录"]
    P1 --> V["统计体积并在 WebUI 显示"]
    P2 --> V
```

保留期可从 WebUI 调整；清理沿用已经落地的 `LogHousekeeping` 模式（启动一次 + 周期一次）。

## 7. 隐私与隔离

| scope | 含义 | 检索可见性 |
|---|---|---|
| `global` | 全局规则与偏好 | 任何对话可见 |
| `group:<id>` | 该群的消息与条目 | **仅该群**可见 |
| `user:<uin>` | 该用户私聊的消息与条目 | **仅该私聊**可见 |

规则：检索**强制** scope 过滤，群记忆绝不进入私聊或别的群；`store_enabled=0` 的 scope 完全不写；凭证类内容（cookie、token、密钥）永不入库；`raw_json` 只存消息本体。

## 8. 分阶段落地

```mermaid
flowchart LR
    P1["P1 宿主落库<br/>关注群 + 私聊<br/>保留期 + WebUI 只读查看"] --> P2["P2 检索<br/>FTS5 trigram<br/>history_search / history_recent"] --> P3["P3 增强<br/>首次关注自动补拉历史<br/>日报改走库存 + 长期条目抽取"]
```

- **P1（半天左右）**：宿主 `MessageStore` + 策略过滤 + 批量写入 + 保留期；WebUI 只读页（条数 / 体积 / 最近消息）
- **P2**：`msg_fts` + 两个检索工具 + 必带注入改为从库取近 N 轮（同时修掉"超限就丢"的毛病）
- **P3**：首次关注群调用 `GetGroupMsgHistoryAsync` 分页补拉；`SummarizeDayAsync` 改为查库；按天总结顺带产出结构化 `mem` 条目（复用同一次模型调用，零额外成本）

## 9. 能力边界（必须知道的硬约束）

```mermaid
flowchart TD
    G1["关注群"] --> R1["可补拉历史<br/>GetGroupMsgHistoryAsync 分页"]
    G2["私聊"] --> R2["无法补拉<br/>通道没有私聊历史接口"]
    R1 --> C1["注意单次分页上限<br/>现有实现 100 × 15 = 1500 条"]
    R2 --> C2["只能从开启时刻起实时收集<br/>此前历史永久缺失"]
```

## 10. 依据（实测）

- 会话历史目前是 `_convHist` **纯内存**，超 `MaxContextTurns*2` 直接丢最旧一轮，**无补偿**
- 长期摘要 `GetMemSummary` 只取 `Split("## ").LastOrDefault()` ⇒ **只有最近一段**进 prompt
- 结构化记忆已存在（`memory/group_748394300/*.md` 等实测 6 个文件），但 Agent 侧**没有任何枚举/注入**代码，只有 `fs_list` 工具 ⇒ 写了没人读
- 全仓**无任何消息持久化实现**；`IBotApi` 只有群历史接口，**无私聊历史接口**
- 体积估算：纯文本 ~200B/条；1 万条/天 ≈ 2MB/天 ⇒ 群 90 天 ≈ 180MB（对照：本轮磁盘卫生刚从 410MB 压到 79MB，保留期必须一开始就定）
- 写入绝不能每条 fsync：本轮已在日志管线中确认 `AutoFlush=true` 逐行同步写盘的代价

## 11. 明确不做

全量收集所有群；按天分目录存 JSONL 再靠 grep 检索；每条消息同步落盘；把私聊内容送远端做 embedding；为存储引入外部数据库服务。
