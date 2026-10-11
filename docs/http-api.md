# 宿主 HTTP API

宿主的 WebUI 与 HTTP 接口由 ASP.NET Core Minimal API 提供，默认监听 `0.0.0.0:<WebUiPort>`（配置项 `WebUiPort`，默认 **1025**），本机访问即 `http://127.0.0.1:1025`。

> 绑定 `0.0.0.0` 是为了局域网内可用；**不要把端口直接暴露到公网**。

---

## 1. 认证

### 1.1 凭证

| 方式 | 用法 |
|---|---|
| Cookie | 登录后下发 `session_token`（`HttpOnly` / `SameSite=Lax` / `Path=/`；勾选"记住我"时有效期 30 天，否则为会话级） |
| Bearer | 请求头 `Authorization: Bearer <session_token>`（脚本/命令行调用方便） |

会话 token 是**进程级单一值**：重启宿主后旧凭证全部失效。
是否启用认证取决于配置里的 `WebUiPassword`；首次启动若为空，宿主会**自动生成 16 位随机密码并只打印到控制台**（不写日志、不进环形缓冲）。

### 1.2 免认证路径（白名单）

```
/login            /app.css  /app.js        /css/*    /js/*
/api/auth*        /api/theme-cfg           /background
```

其余路径必须带凭证：`/api/*` 未认证返回 **401 + `{"error":"unauthorized"}`**；非 `/api` 路径未认证则 **302 重定向到 `/login`**。

### 1.3 登录接口

```http
POST /api/auth/login        # 免认证
Content-Type: application/json

{ "password": "控制台打印的密码", "remember": true }
```

- 成功：`{"ok":true}` 并下发 `session_token` Cookie
- 失败：`{"ok":false,"error":"密码错误"}`
- **失败退避**：按来源 IP 计数，前 2 次立即返回，之后 `2^(n-2)` 秒（上限 300 秒）；退避期内返回 `{"ok":false,"error":"失败次数过多，请 N 秒后再试"}`
- 密码用 SHA256 十六进制 + 定长时间比较，避免计时侧信道

```http
POST /api/auth/logout       # 清 Cookie
GET  /api/auth/status       # → {"authRequired":true|false}
```

---

## 2. 端点总表

认证列：`免` = 白名单免认证；`✓` = 需要凭证。

### 页面与静态资源

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/` | ✓ | 主页面（`WebUi/index.html`，注入资源指纹后返回） |
| GET | `/login` | 免 | 登录页 |
| GET | `/app.js` | 免 | **按 `UiAssets` 顺序合并**后的 JS 单文件（带 `ETag`、`Cache-Control: no-cache`） |
| GET | `/app.css` | 免 | 合并后的 CSS 单文件（同上） |
| GET | `/css/*`、`/js/*` | 免 | 磁盘上的原始模块文件（`<exe旁>/WebUi/`） |

### 外观 / 背景

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/api/theme-cfg` | 免 | → `{uiOpacity, ctlOpacity, bgOpacity, hasBackground}` |
| POST | `/api/theme-cfg` | 免 | 请求体 `{uiOpacity?, ctlOpacity?, bgOpacity?}`（0–100，自动 clamp）→ `{ok, …三个当前值}` |
| GET | `/background` | 免 | 背景图（按扩展名给 `image/png`、`image/webp`，其它按 `image/jpeg`）；未设置返回 **404** |
| POST | `/background` | ✓ | 上传背景图（`multipart/form-data`，字段 `file`）→ `{ok:true}`；仅支持 jpg/png/webp（gif 按原扩展存近似）、≤15MB |
| DELETE | `/background` | ✓ | 删除背景图 → `{ok:true}`；未设置时 `{ok:false,error:"未设置背景"}` |

### 状态 / 日志

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/api/status` | ✓ | → `{connected, selfId, wsUrl, transport, startedAt, uptimeSec}`；`transport` 为 `onebot` / `official` |
| GET | `/api/logs?after=N` | ✓ | 增量拉日志 → `{next, lines:[{id,t}]}`，把 `next` 作为下次的 `after` |
| POST | `/api/logs/clear` | ✓ | 清空环形缓冲 → **200 空响应**（非 JSON） |
| GET | `/api/deps` | ✓ | → `{napcatConfigured, llbotConfigured, napcatRunning, llbotRunning, connected}` |

### 连接控制

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| POST | `/api/start-connect` | ✓ | 请求体 `{start:"napcat"\|"llbot"\|"none"}`：按需拉起进程并尝试连接 → `{ok, connected, message}`（`message` 是分号拼接的说明，如"已启动；正在连接…"） |
| POST | `/api/connect` | ✓ | 手动重连（后台进行，结果轮询 `/api/status`）→ `{ok:true, message:"正在连接…"}` |

### 插件

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/api/plugins` | ✓ | 插件快照数组：`{name, version, desc, loaded, autoLoad, source, model}`（`source`=`ai`/`manual`/`handmade`） |
| POST | `/api/plugins/rescan` | ✓ | 重新扫描 `plugins/` 目录（识别手工拷进来的插件） |
| POST | `/api/plugins/upload` | ✓ | `multipart/form-data`，字段 `file`，**仅接受 `.zip`**（插件目录打包；包内可含多个插件）→ `{ok, …}` |
| POST | `/api/plugins/{name}/{action}` | ✓ | `action` 仅 `load` / `unload` / `reload` → `{ok}`；插件名非法返回 `{ok:false,error:"插件名非法"}` |
| GET | `/api/plugins/{name}/settings` | ✓ | → `{loaded, defs:[{Key,Label,Type,Default,Description,Options}], values:{…}}` |
| POST | `/api/plugins/{name}/settings` | ✓ | 请求体为键值对象（只提交要改的键）→ `{ok, error}`；保存后宿主回调插件的 `OnSettingsChangedAsync()` |

### 人格文件（Agent 插件用）

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/api/personas/files` | ✓ | → `{files:[…]}`（`Agent/personas/*.md`，已排序） |
| GET | `/api/personas/file?file=xxx.md` | ✓ | → `{ok, text}`（文件不存在时 `ok:false`、`text` 为空） |
| POST | `/api/personas/file` | ✓ | 请求体 `{file, text}`：保存/新建（`file` 自动补 `.md`，只取文件名防路径穿越）→ `{ok, file}` |
| DELETE | `/api/personas/file?file=xxx.md` | ✓ | → `{ok:true}`（不存在也算成功） |

### 命令与模型

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| POST | `/api/command` | ✓ | 请求体 `{text:"!plugin list"}`；**必须带命令前缀**；WebUI 视为管理员通道 → `{message}` |
| POST | `/api/test-model` | ✓ | 请求体 `{apiKey, baseUrl, model}`（三者可省，缺省取 Agent 插件设置）→ 成功 `{ok:true, reply, model}`，失败 `{ok:false, error}`；`baseUrl` 必须是 `http(s)://` 开头 |
| GET | `/api/instructions` | ✓ | → `{text}`：当前 Agent 指令（人格）正文 |
| POST | `/api/instructions` | ✓ | 请求体 `{text, reload?}`；`reload=true` 时顺带重载 Agent 插件 → `{ok, reloaded}` |

### 配置

| 方法 | 路径 | 认证 | 说明 |
|---|---|---|---|
| GET | `/api/config` | ✓ | 返回配置；密码类字段只回标志位（`webUiPasswordSet`），**不回显** AppSecret 等机密 |
| POST | `/api/config` | ✓ | 请求体为要改的字段（`transport` / `officialAppId` / `officialSandbox` / `officialRegisterCommands` / 连接与命令等）；→ `{ok, wsChanged, reloaded, transportChanged}` |

约定：
- 机密字段**留空即保持原值**，传 `"-"` 表示清空（AppSecret 等）
- `transport` 变更**下次启动生效**（响应里 `transportChanged:true`，前端会提示"需重启"）
- `wsChanged` 为 true 时宿主会立即尝试重连

---

## 3. 调用示例

```bash
BASE=http://127.0.0.1:1025

# 1) 登录拿 Cookie（脚本里也可以改用 Bearer）
curl -s -c cj.txt -H 'Content-Type: application/json' \
     -d '{"password":"控制台打印的密码","remember":true}' $BASE/api/auth/login

# 2) 增量拉日志（先 after=0，之后用上次返回的 next）
curl -s -b cj.txt "$BASE/api/logs?after=0"

# 3) 装插件（zip 里可以是多个插件目录）
curl -s -b cj.txt -F "file=@FlexBot-BuiltInPlugins-1.0.0.zip" $BASE/api/plugins/upload

# 4) 重载某个插件
curl -s -b cj.txt -X POST $BASE/api/plugins/Agent/reload

# 5) 改插件设置（只提交要改的键）
curl -s -b cj.txt -H 'Content-Type: application/json' \
     -d '{"NotifyToolCalls":"false"}' $BASE/api/plugins/Agent/settings

# 6) 执行宿主/插件命令（必须带前缀，WebUI 视为管理员）
curl -s -b cj.txt -H 'Content-Type: application/json' \
     -d '{"text":"!plugin list"}' $BASE/api/command

# 7) 读配置 / 改通道（改通道需重启生效）
curl -s -b cj.txt $BASE/api/config
curl -s -b cj.txt -H 'Content-Type: application/json' \
     -d '{"transport":"official","officialAppId":"1905767608"}' $BASE/api/config
```

---

## 4. 返回约定

- 业务结果多为 **HTTP 200 + `{"ok":true|false, …}`**；失败时带 `error` 文字（例如 `{"ok":false,"error":"仅支持 .zip（把插件目录打包成压缩包上传）"}`）。
- **401** 仅用于未认证的 `/api/*`；非 API 路径未认证是 **302 → `/login`**。
- 少数 GET 直接返回数组或原始文件流（`/api/plugins`、`/api/personas/files`、`/background`）。
- 日期时间统一字符串 `yyyy-MM-dd HH:mm:ss`；`uptimeSec` 为秒数。

---

## 5. 静态资源与热更新

WebUI 的资源**不在程序里内嵌**，而是每次请求从 `<exe旁>/WebUi/` 读盘：

1. `/app.js`、`/app.css` 由 `UiAssets` 清单里的模块**按序合并**而成（JS 拼接成单文件，避免模块顺序问题）；
2. 两个合并资源都带 `ETag` 与 `Cache-Control: no-cache`，`If-None-Match` 命中返回 **304**；
3. 页面（`index.html` / `login.html`）里的 `app.css?v=` / `app.js?v=` 会被替换成**资源指纹**（各文件 size/mtime 的 SHA256 前 8 位）；
4. 因此改完 `WebUi/` 下的文件**刷新浏览器即生效**（指纹变了会拉新资源），不需要重新构建或重启宿主。

例外：新增/删除 `UiAssets` 清单里的条目属于 C# 侧改动，需要重新构建宿主。
