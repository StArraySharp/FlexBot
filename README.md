# FlexBot

基于 OneBot 11 协议的 QQ 群机器人框架：.NET 10 宿主 + 可热重载的插件体系 + WebUI 管理后台。

## 特性

- **模块化插件**：每个插件独立目录、独立 AssemblyLoadContext 隔离，支持加载/卸载/热重载，依赖自动拓扑排序
- **脚本插件（csx）**：把 `*.cs` 丢进 `plugins/<名称>/` 即生效——宿主用 Roslyn 在内存里编译，零 csproj、零 SDK、零重启；
  `plugin.json` 写一行 `NuGet` 即自动解析第三方依赖（含传递依赖），全程无外部脚本
- **AI 智能体**（Agent 插件）：流式分段回复、工具调用（联网搜索/看图/文件工作区/电脑控制/记忆）、多人格、备用模型回落、自动对话总结
- **AI 生成插件**：群里直接让 AI 生成/修改/删除插件（`pb_create` / `pb_update`），失败诊断（精确到 `文件(行,列)`）自动回喂给模型；每次生成都留完整审计台账与来源标注
- **WebUI**：ASP.NET Core Minimal API + 无构建前端（静态资源原样放在 `<exe旁>/WebUi/`，**改完 CSS/JS 刷新浏览器即生效，无需重建/重启**），插件管理（含刷新与 zip 安装）、配置、日志、模型测试一站式
- **电脑控制**（PCControl 插件）：进程/截屏/剪贴板/鼠标/键盘/电源，PowerShell 远程执行
- **ILSpy 反编译查询**（PluginApi 插件）：聊天里直接查契约层源码，支持**成员级**查询（`member`）与成员搜索
- **插件工厂**（PluginBuilder 插件）：脚本型免编译直载；编译型默认宿主内存编译（无需 dotnet SDK），可切 `dotnet` 后端

## 解决方案结构

| 项目 | 说明 |
|---|---|
| `FlexBot/` | 宿主（Exe，net10.0）：BotClient、PluginManager、路由、WebUI、Photino 窗口、csx 脚本编译器、NuGet 解析 |
| `FlexBot.Abstractions/` | 插件契约层（独立程序集 `FlexBot.Abstractions`，命名空间 `FlexBot.PluginApi`）：`IBotPlugin`、`IBotContext`、`IBotApi`、Hub、调度器、KV 存储、编译服务 |
| `FlexBot.OneBotLib/` | OneBot 11 协议库（net8.0，可独立打包）：WebSocket 客户端、事件、消息段 |
| `FlexBot.BuiltInPlugins/*` | 内置插件（每个独立项目，输出到宿主 `plugins/`） |
| `samples/csx-json/` | csx 脚本插件示例：NuGet 自动解析 + AI 审计/来源标注 |

### 内置插件

| 插件 | 功能 | 主要命令 |
|---|---|---|
| **Agent** | AI 对话（私聊/群聊 @、图片理解、联网搜索、记忆、人格、撤回即停）；插件工厂工具 | 被 @ 或叫名字触发 |
| **Admin** | 主人功能：定时计划、禁用群 LLM、关注群、戳一戳、群名片通知 | `ping` `api` `timer` `cron` `nollm` `watch` 等 |
| **PCControl** | 控制宿主 Windows | `pc status\|exec\|ps\|kill\|open\|screenshot\|clip\|mouse\|key\|lock\|sleep\|shutdown\|restart\|cancel` |
| **FileSystem** | 给 AI 用的沙箱文件工作区 | `fs_read` `fs_write` `fs_search` 等 |
| **PluginBuilder** | 生成/热载新插件 + 生成日志 + AI 审计台账 | `pb_build`（见下）、`pb_list` |
| **PluginApi** | ILSpy 反编译查询契约层源码 | `pluginapi` / `papi` |
| **AOnline** | 在线状态/签到相关 | - |
| **RedPacketWatch** | 群红包监听并私聊通知主人 | - |

`pb_build` 动作一览（脚本型免编译；编译型默认宿主内存编译，可切 `dotnet`）：

```
── 脚本型（推荐）──
script-init <名>  script <名> <描述>  script-update <名>
── 编译型 ──
init <名>  write <名> <相对路径>  create <名> <描述>  update <名>  compile <名>
── 通用 ──
read <名>  readsrc <名> <相对路径>  log <名> [行数]  llm <名> [序号]  deps <名> <A,B>  list  delete <名>
```

## 下载与运行

### 1. 下载哪个

到 Releases 页面下载**两个**文件（都要下）：平台包 + 内置插件包。平台包已自包含 .NET 运行时，**不需要**另装 .NET。

| 平台 | 平台包 |
|---|---|
| Windows x64 | `FlexBot-<版本>-win-x64.zip` |
| Linux x64 / arm64 | `FlexBot-<版本>-linux-x64.zip` / `-linux-arm64.zip` |
| macOS Intel / Apple Silicon | `FlexBot-<版本>-osx-x64.zip` / `-osx-arm64.zip` |
| 内置插件（平台无关） | `FlexBot-BuiltInPlugins-<版本>.zip` |

校验（可选）：`sha256sum -c SHA256SUMS.txt`；Windows 用 `certutil -hashfile <文件> SHA256` 逐个比对。

### 2. 解压并运行

默认会**打开一个桌面窗口**（它加载的就是 WebUI 页面），**关窗即退出程序**。没有图形界面的环境（服务器、容器、无 `DISPLAY`）会自动退化为无窗口模式，用浏览器访问控制台打印的 WebUI 地址即可；也可以显式指定：

| 参数 | 效果 |
|---|---|
| （无） | 有图形界面就开窗口，没有就退化为 WebUI |
| `--no-gui` | 强制无窗口：控制台常驻 + 浏览器访问 WebUI（后台/服务器用） |
| `--browser` | 直接用系统 Chrome/Edge 以「应用窗口」（无地址栏）打开 WebUI —— 本机 WebView2 渲染不出来时的最优解 |
| `--gui` | 强制重试原生 Photino 窗口（会清掉"WebView2 不可用"的记录） |
| `--no-web` | 连 WebUI 都不开（纯机器人，无管理页面） |

```bash
# Windows：解压后双击 FlexBot.exe
# Linux
chmod +x FlexBot && ./FlexBot
# macOS（包未签名，不去隔离会被 Gatekeeper 拦下）
xattr -dr com.apple.quarantine FlexBot && ./FlexBot
```

### 3. 首次配置

1. 控制台会打印 **WebUI 地址**，以及一个**随机登录密码**（明文只打印这一次，不写日志）
2. 浏览器打开该地址，用密码登录
3. 设置页填「**主人 QQ**」（必填，权限最高；不填则所有命令与私聊一律拒绝），建议顺手设置自己的登录密码
4. WebUI 默认监听 `0.0.0.0`（局域网可达）：**不要把端口直接暴露到公网**

### 4. 安装插件

平台包**不含 `plugins/`**（宿主首次启动会自动创建空目录）。插件在插件包里，两种装法：

- 推荐：WebUI → 插件页 → **添加插件** → 选 `FlexBot-BuiltInPlugins-<版本>.zip`（一次装 8 个）
- 或：把 zip 里的 `plugins/` 目录直接解压到宿主同级

> Agent 插件首次加载会因缺少 ApiKey 而失败，这是预期行为 —— 先在它的「设置」里填好模型信息再加载。

### 5. 接入 QQ（通道二选一）

在 WebUI 设置页的「连接 · 通道」里选择，保存后**重启生效**：

| 通道 | 说明 |
|---|---|
| **OneBot**（默认） | 需要 [NapCat](https://github.com/NapNeko/NapCatQQ) 或 [LLBot](https://github.com/LLOneBot/LuckyLilliaBot) 提供正向 WebSocket（默认 `ws://127.0.0.1:3001`）；在设置里填好启动命令后，宿主可一键拉起并自动重连 |
| **QQ 官方机器人开放平台** | 填 AppID / AppSecret（沙箱或正式环境）。官方接口能力是 OneBot 的子集，且用户身份是 openid，需要在 QQ 里执行一次 `!bind qq <QQ号> <控制台口令>` 完成绑定 |

### 6. 配置 AI 模型（可选）

AI 功能来自 **Agent 插件**：WebUI → 插件 → Agent → 设置，填 `ApiKey` / `BaseUrl` / `Model`（任意 OpenAI 兼容端点，如 DeepSeek、智谱），保存即热应用。没配模型时该插件按预期加载失败，其余功能不受影响。

## 命令速查

命令前缀默认 `!`（Admin 插件设置可改）：

```
!help                                     # 全部命令
!plugin list|load|unload|reload <名称>     # 插件管理
!plugin nuget <名称>                       # 手动（重新）解析该插件的 NuGet 依赖
!pb_build <动作> <名> […]                  # 插件工厂（见上表）
!pluginapi list|all|<类型名>|member <类型名> [词]|search <词>   # 前端源码查询（支持成员级）
!nollm <群号> / !unnollm <群号>            # 禁用/恢复某群的 AI 回复
!pc status|screenshot|...                 # 电脑控制
```

### 权限模型

| 身份 | 权限 |
|---|---|
| **主人**（`OwnerUin`） | 全部管理员权限；外加独占：`!全员` / `!随机100`、红包私聊通知 |
| **管理员**（主人 ∪ `AdminUins`） | 所有插件注册的命令；私聊机器人；AI 工具里的 `run_command`（可执行任意宿主/插件命令，含 `!plugin load`）、`send_group_msg`/`send_private_msg`/`send_image`/`download_and_send_file`、关注群增删、`pb_create`/`pb_update`/`pb_delete` 等 |
| **普通群员** | 在关注群里与 AI 对话；AI 的未门禁工具（联网搜索、记忆、沙箱文件读写）可用；不能触发任何前缀命令 |
| **WebUI** | 权限**高于管理员**：`/api/command` 直接进插件命令派发、不经宿主门禁，可加载任意插件、改配置。故登录密码**留空时首启自动生成并打印到控制台**（不会出现无密码状态），且不要把端口暴露到公网 |

宿主对**所有**插件注册命令统一做管理员校验（`Program.cs` 的命令分发），插件内部再对少数危险能力做二次判定；私聊消息仅管理员会被处理。

## 常见问题

- **页面白屏 / 500**：发布包缺少 `WebUi/` 目录（必须与 `FlexBot.dll` 同级）。启动日志会逐条列出缺失文件。
- **忘了 WebUI 密码**：删掉 `config.json` 里的 `WebUiPassword` 后重启，会重新生成随机密码并打印到控制台。
- **机器人不回应**：① 没填 `OwnerUin`（命令与私聊一律拒绝）；② OneBot 实现没在运行（看 WebUI 状态页与日志）；③ Agent 插件没配模型。
- **官方通道"能收不能做"**：禁言 / 踢人 / 历史消息 / 合并转发等官方开放平台不提供，逐条对照见 [插件 API 参考](docs/plugin-api.md) 的能力表。
- **群里有消息时电脑响提示音**：那是本机 QQ 客户端收到消息的通知音，与 FlexBot 无关（可在音量合成器里单独静音 `QQ.exe`）。
- **AI 一轮回复有好几条消息**：模型在工具调用前输出的文本会先发出来（把该插件的「工具调用提示行」关掉即可只保留文本）。
- **升级**：保留 `memory/` 与 `logs/` 目录，其余文件覆盖即可。

## 文档

- [安装、构建与部署](docs/setup.md)：从源码构建、配置项说明、构建注意事项、发布打包（release.sh / GitHub Actions）
- [插件开发指南](docs/plugin-dev.md)：脚本插件与编译型插件、NuGet 依赖、插件工厂与生成审计
- [插件 API 参考](docs/plugin-api.md)：`IBotPlugin` / `IBotContext` / `IBotApi` 契约、消息模型、Hub、内置服务、两条通道能力差异
- [宿主 HTTP API](docs/http-api.md)：认证方式、全部端点与请求响应、静态资源热更新语义
- [文档目录](docs/README.md)：5 分钟写出第一个插件 + 目录与清单约定

## 从源码构建

需要 .NET 10 SDK：

```bash
git clone <本仓库> && cd FlexBot
dotnet run --project FlexBot
```

构建、配置项与打包发布的完整说明见 [安装、构建与部署](docs/setup.md)。

## 许可

本项目以 **GNU Affero General Public License v3.0（AGPL-3.0）** 授权，完整条款见仓库根目录的 [`LICENSE`](LICENSE)。

选择 AGPL 的原因很直接：本项目是**网络服务型**软件——只分发二进制时 GPL 对"自己跑起来对外提供服务"的人没有约束力，只有 AGPL 才能要求"修改后对外提供服务的人同样开源"。

这意味着你可以自由使用、修改、自建部署；但如果你**修改后把它作为对外服务提供**，需要按 AGPL 公开你的修改。第三方插件通过公开接口调用本项目，实践上通常不被视为衍生作品（具体取决于法律解释）。

发布包（`FlexBot-<版本>-<rid>.zip`）内同时附带 `LICENSE` 与 `使用说明.txt`。

## 第三方库与外部依赖

### 直接依赖（编译进产物或随包分发）

| 库 | 用途 | 许可证 | 链接 |
|---|---|---|---|
| Photino.NET 4.0.16 | 桌面窗口（宿主可选用 Photino 界面） | Apache-2.0 | [GitHub](https://github.com/tryphotino/photino.NET) · [NuGet](https://www.nuget.org/packages/Photino.NET) |
| Microsoft.CodeAnalysis.CSharp 5.9.0 | csx 脚本插件的内存编译（Roslyn） | MIT | [GitHub](https://github.com/dotnet/roslyn) · [NuGet](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp) |
| QQBot.Net 0.2.0 | QQ 官方机器人开放平台通道（token / 网关 / 指令 / 互动） | MIT | [GitHub](https://github.com/gehongyan/QQBot.Net) · [文档](https://qqbotnet.dev/) |
| Microsoft Agent Framework（Microsoft.Agents.AI / .OpenAI）1.17.0 | Agent 插件的模型调用与工具循环 | MIT | [GitHub](https://github.com/microsoft/agent-framework) · [NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI) |
| ILSpy / ICSharpCode.Decompiler 11.0 | PluginApi 插件的反编译查询 | MIT | [GitHub](https://github.com/icsharpcode/ILSpy) · [NuGet](https://www.nuget.org/packages/ICSharpCode.Decompiler) |
| System.Drawing.Common 10.0.11 | PCControl 插件的截图处理 | MIT | [GitHub](https://github.com/dotnet/runtime) · [NuGet](https://www.nuget.org/packages/System.Drawing.Common) |
| Newtonsoft.Json 13.0.4（仅示例） | samples/csx-json 演示 NuGet 依赖自动解析到 libs/ | MIT | [GitHub](https://github.com/JamesNK/Newtonsoft.Json) · [NuGet](https://www.nuget.org/packages/Newtonsoft.Json) |

以上依赖全部为宽松许可（MIT / Apache-2.0），与本项目的 AGPL-3.0 兼容；分发时保留其版权与许可声明即可。

### 运行时外部依赖（未随包分发，需自行部署）

| 项目 | 说明 | 链接 |
|---|---|---|
| OneBot 11 标准 | 默认通道所遵循的协议规范 | [botuniverse/onebot-11](https://github.com/botuniverse/onebot-11) |
| LLBot（LuckyLillia） | OneBot 11 / Satori / Milky 实现，支持无界面 CLI 运行 | [LLOneBot/LuckyLilliaBot](https://github.com/LLOneBot/LuckyLilliaBot) |
| NapCat | 另一套 OneBot 11 实现（宿主同样支持） | [NapNeko/NapCatQQ](https://github.com/NapNeko/NapCatQQ) |
| QQ 官方机器人开放平台 | 官方通道的开放平台入口与开发文档 | [开放平台](https://bot.q.qq.com/open) · [文档](https://bot.q.qq.com/wiki/) |
| .NET 10 / ASP.NET Core | 宿主运行时（自包含发布包已内置，无需另装） | [dotnet/runtime](https://github.com/dotnet/runtime) · [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) |

感谢上述项目的作者与社区。宿主只通过标准协议或公开 HTTP API 与外部实现交互，这些项目均不属于本项目的一部分。
