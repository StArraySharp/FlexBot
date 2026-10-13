# 安装、构建与部署

本页面向**从源码构建/部署**的场景；最终用户照着 [README](../README.md) 的「下载与运行」即可，不需要 .NET SDK。

## 目录

- [前置条件](#前置条件)
- [从源码构建运行](#从源码构建运行)
- [模型配置](#模型配置)
- [部署](#部署)
- [发布打包](#发布打包)
- [目录速览](#目录速览)
## 从源码构建运行

### 前置

- .NET 10 SDK
- OneBot 11 实现（推荐 [NapCat](https://github.com/NapNeko/NapCatQQ)），开启正向 WebSocket 服务端（默认 `ws://127.0.0.1:3001`）
- [LLBot](https://github.com/LLOneBot/LLOneBot)（可选）：另一 OneBot 实现，与 NapCat 二选一或并存；配置 `LLBotCmd` 后随宿主一键拉起，日志自动隔离到独立文件

### 运行

```bash
git clone <本仓库>
cd FlexBot
dotnet run --project FlexBot
```

首次启动自动生成 `config.json`（exe 旁），按需修改后重启：

```jsonc
{
  "WsUrl": "ws://127.0.0.1:3001",   // OneBot WebSocket 地址
  "Token": "",                       // OneBot 访问令牌（可为空）
  "OwnerUin": 10000,                 // 机器人主人 QQ（自动具有管理员权限）；不填则视为"无管理员"，命令与私聊一律拒绝
  "AdminUins": [],                   // 额外管理员（主人恒在其中，无需重复填写）
  "NapCatCmd": "",                   // 可选：随宿主一键拉起 NapCat 的命令
  "NuGetFeed": "https://api.nuget.org/v3-flatcontainer",  // 插件 NuGet 依赖源（可换私有源/镜像）
  "WebUiPort": 0,                    // 0 = 自动分配空闲端口
  "WebUiPassword": ""                // 留空则首次启动自动生成随机密码并打印到**控制台**（明文只打印一次，不写日志）；WebUI 绑定全部网卡，务必不要把端口暴露到公网
}
```

运行模式（默认开窗口，无图形界面自动退化为 WebUI）：

| 参数 | 效果 |
|---|---|
| （无） | 有可用图形界面就打开 Photino 桌面窗口（内容即 WebUI 页面），关窗即退出；Windows 服务/Session 0、Linux 无 `DISPLAY`/`WAYLAND_DISPLAY` 时自动退化 |
| `--no-gui` / `--headless` | 强制无窗口：控制台常驻 + 浏览器访问 WebUI |
| `--browser` | 用系统 Chrome/Edge 以 `--app=` 应用窗口打开 WebUI（无地址栏） |
| `--gui` | 强制重试原生 Photino 窗口；创建失败仍回退无窗口，不杀进程 |
| `--no-web` | 不开 WebUI（纯机器人） |

探测逻辑（`FlexBot/WebUi/GuiSupport.cs`）保守判断：Windows 用 `Environment.UserInteractive`，macOS 默认可用，Linux 看 `DISPLAY`/`WAYLAND_DISPLAY`；不确定时按"没有"处理。
启动后控制台会打印 WebUI 地址（本机地址 `http://127.0.0.1:<端口>`；实际监听 `0.0.0.0`，局域网可达），浏览器打开即可管理。登录失败会按来源 IP 指数退避（前 2 次立即返回，之后 2/4/8… 秒，上限 5 分钟）。

### 模型配置

AI 模型配置在 WebUI → 插件 → Agent → 设置（存储于 `plugins/Agent/settings.json`，保存即热应用）：

- **ApiKey / BaseUrl / Model**：任意 OpenAI 兼容端点（DeepSeek、智谱、OpenAI 等）
- **FallbackModels**：主模型失败时按序回落的备用链
- **Personas**：多套人格提示词，`personas/*.md` 随设置一并保存
- **GroupChatEnabled**：群聊 AI 回复全局开关（单群粒度用 `!nollm <群号>`）
- **NameKeywords / ReplyProbability / ImageReplyProbability**：唤醒词与概率回复


## 部署

- Windows：直接运行 `FlexBot.exe`（含 Photino 桌面窗口模式）
- Linux：`dotnet publish FlexBot -c Release -r linux-x64` 后运行 `dotnet FlexBot.dll`（仓库不带启动/守护脚本，按需自备 systemd unit 或 supervisor）
- 发布包**必须带上 `WebUi/` 目录**（与 `FlexBot.dll` 同级）：WebUI 资源现在就放在那里，宿主每次请求都从磁盘读。缺了会在启动日志里逐条列出缺失文件，页面返回 500

## 目录速览

```
FlexBot/
├─ Program.cs / BotClient.cs / PluginManager.cs / Routing.cs / Config.cs
├─ Scripting/            # csx 脚本编译前端（Roslyn 内存编译 + 指纹缓存）+ NuGet 依赖解析
├─ WebUi/                # WebUI 源码（构建/发布时原样拷到 <输出>/WebUi/；改完刷新即生效）+ Minimal API
└─ memory/               # 运行数据（构建后生成于输出目录 exe 旁）
FlexBot.Abstractions/    # 插件契约（IBotPlugin、IBotContext、IBotApi、Hub、服务）
FlexBot.OneBotLib/       # OneBot 11 协议库
FlexBot.BuiltInPlugins/  # 内置插件源码
samples/csx-json/        # csx 脚本插件示例（NuGet 依赖 + 生成审计）
```


## 发布打包

```bash
./release.sh [版本号] [输出根目录]     # 默认 1.0.0 → publish/release/<版本>/
```

产物：5 个平台自包含包（RID 命名 `FlexBot-<版本>-<rid>.zip`：`win-x64` / `linux-x64` /
`linux-arm64` / `osx-x64` / `osx-arm64`）+ `FlexBot-BuiltInPlugins-<版本>.zip`（平台无关）
+ `SHA256SUMS.txt`（校验）。每个包内附 `LICENSE`、`THIRD-PARTY-NOTICES.txt`、`使用说明.txt`。

CI 打包：仓库 → Actions → **发布打包** → Run workflow（手填版本号；勾选 `create_release`
会建/更新 `v<版本>` 的 Release 并把产物作为附件上传，正文取 `RELEASE_NOTES.md`）。

## 构建注意（踩过的坑）

- **宿主正在运行时构建会失败**：`MSB3021/MSB3027 文件被 FlexBot 锁定`。先停掉宿主（和 LLBot/node）再构建，
  或把输出旁路到临时目录：`dotnet build FlexBot.slnx -c Debug -o <临时目录>`。
- **插件工程的输出目录**指向宿主 `plugins/`（见各内置插件 csproj 的 `OutDir`），所以插件与宿主一起构建即可。
- WebUI 资源在 `FlexBot/WebUi/`，构建/发布时原样拷到 `<输出>/WebUi/`；**改这些文件不需要重新构建**（见下）。
- `*.sh` 在 `.gitattributes` 里固定为 LF，CI（Linux）里用 `bash release.sh` 调用，避免可执行位/CRLF 问题。
