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

## 快速开始

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

启动后控制台会打印 WebUI 地址（本机地址 `http://127.0.0.1:<端口>`；实际监听 `0.0.0.0`，局域网可达），浏览器打开即可管理。登录失败会按来源 IP 指数退避（前 2 次立即返回，之后 2/4/8… 秒，上限 5 分钟）。

### 模型配置

AI 模型配置在 WebUI → 插件 → Agent → 设置（存储于 `plugins/Agent/settings.json`，保存即热应用）：

- **ApiKey / BaseUrl / Model**：任意 OpenAI 兼容端点（DeepSeek、智谱、OpenAI 等）
- **FallbackModels**：主模型失败时按序回落的备用链
- **Personas**：多套人格提示词，`personas/*.md` 随设置一并保存
- **GroupChatEnabled**：群聊 AI 回复全局开关（单群粒度用 `!nollm <群号>`）
- **NameKeywords / ReplyProbability / ImageReplyProbability**：唤醒词与概率回复

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

## 插件开发

### 一、脚本插件（免编译，推荐）

把 `*.cs` 丢进 `plugins/<名称>/` 即可——宿主加载时用 Roslyn **在内存中编译**，改完执行
`!plugin reload <名称>` 就生效，不需要 csproj、不需要 `dotnet build`、不需要重启。

```
plugins/天气/
  main.cs         目录下全部 *.cs 参与编译（文件名随意，多文件/partial 都支持）
  plugin.json     可选：Depends 依赖、Version、Description、Settings（WebUI 设置表单）、NuGet 依赖
  libs/           第三方 DLL：由 plugin.json 的 NuGet 字段自动铺入，也可手工放置
  settings.json   宿主写入（WebUI 保存设置）
  .cache/         宿主写入：编译产物缓存 + NuGet 下载缓存（可安全删除）
```

第三方 NuGet 依赖只需在 `plugin.json` 写一行，宿主会在**加载插件时自动解析**（下载含传递依赖的包 →
挑最合适的 `lib/{tfm}` 资产铺进 `libs/` → 编译期当引用、运行期由 ALC 从影子目录解析）：

```json
{ "NuGet": ["Newtonsoft.Json@13.0.4"] }
```

不需要 csproj、不需要 dotnet SDK、不需要任何外部脚本。手动触发：`!plugin nuget <插件名>`。
依赖源可在 `config.json` 的 `NuGetFeed` 指定（默认官方 flat-container）。

生成/改动历史：每个插件有一份追加式 `plugins/<名>/generation.log`（时间 / 来源 / 动作 / 结果 / 诊断），
用 `!pb_build log <名>` 查看；`plugin.json` 的 `Build` 字段记录最近一次构建状态。
插件加载失败时诊断会直接从 `!plugin load` 的回复里带出来（AI 走 `pb_create` 也能看到 CS 错误）。

AI 生成审计：Agent 每次调 `pb_create` / `pb_update` 都会把**完整工具调用参数**（工具名、模型、调用者、
群号、时间、同轮助手文本）与**本次实际参与编译的源码**归档为 `plugins/<名>/generation.llm.jsonl`，
用 `!pb_build llm <名>` 看台账、`!pb_build llm <名> <序号>` 展开完整记录。
`plugin.json` 的 `Provenance` 字段据此标注来源，WebUI 插件卡片上显示徽标：

- `ai` → 「AI 生成」（tooltip 带模型名）
- `manual` → 「手写」（由 pb_build 手工生成，有生成日志）
- 无标注（内置插件 / 手工放入 / zip 安装）→ 不显示徽标

```csharp
// plugins/天气/main.cs
using FlexBot.PluginApi;

namespace 天气;

public sealed class WeatherPlugin : IBotPlugin
{
    public string Name => "天气";
    public string Version => "1.0.0";
    public string Description => "问天气";

    public Task OnLoadAsync(IBotContext ctx)
    {
        ctx.RegisterCommand("weather", "查天气", a => Task.FromResult($"天气：晴（{a}）"));
        ctx.Messages.OnGroup(async e =>
        {
            if (!e.Message.PlainText.Contains("天气")) return Handled.Continue;
            await ctx.Api.SendGroupMsgAsync(e.Message.GroupId ?? 0, "查一下…");
            return Handled.Stop;
        }, priority: 100, tag: Name);
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync() => Task.CompletedTask;   // 必须停掉自己起的一切后台活动
}
```

隐式 using（脚本里无需重复写）：`System`、`System.Collections.Generic`、`System.IO`、`System.Linq`、
`System.Net.Http`、`System.Threading`、`System.Threading.Tasks`。
脚本只能看到 `FlexBot.Abstractions` 暴露的契约（宿主内部类型不可见），可用能力见
`samples/csx-json/README.md`，或在聊天里用 `!pluginapi` 直接查契约源码。
编译失败时诊断会精确到 `文件(行,列)`写进宿主日志，并从 `!plugin load` / `pb_build script` 的回复带出。

不参与编译的目录：点开头的目录（如 `.cache/`）、`libs/`、`backups/`、`bin/`、`obj/`。

### 二、编译型插件（DLL）

需要第三方 NuGet、或想分发独立 dll 时使用：实现 `FlexBot.PluginApi.IBotPlugin`，输出到
`plugins/<名称>/<名称>.dll` 即被自动发现。两类插件共用同一套加载/卸载/热重载机制；
也可以交给插件工厂（`!pb_build init|write|create|update`，默认宿主内存编译，不需要 dotnet SDK）。

```csharp
using FlexBot.PluginApi;

public sealed class MyPlugin : IBotPlugin
{
    public string Name => "MyPlugin";
    public string Version => "1.0.0";
    public string Description => "示例插件";

    public Task OnLoadAsync(IBotContext ctx)
    {
        ctx.RegisterCommand("hello", "问好", _ => Task.FromResult("world"));
        ctx.Messages.OnGroup(async e =>
        {
            await ctx.Api.SendGroupMsgAsync(e.Message.GroupId!.Value, "hi");
            return Handled.Continue;
        }, priority: 100, tag: Name);
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync() => Task.CompletedTask;
}
```

csproj 模板（关键点：引用 `FlexBot.Abstractions` 且 `ExcludeAssets="runtime"` 不拷贝、输出到宿主 `plugins/`、自带 NuGet 依赖）：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>MyPlugin</RootNamespace>
    <AssemblyName>MyPlugin</AssemblyName>
    <OutDir>..\..\FlexBot\bin\$(Configuration)\net10.0\plugins\MyPlugin\</OutDir>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xxx" Version="x.y.z" />
    <!-- 契约层：宿主与插件共享同一份程序集，运行时由 ALC 回落宿主，故不随插件拷贝 -->
    <ProjectReference Include="..\..\FlexBot.Abstractions\FlexBot.Abstractions.csproj" ReferenceOutputAssembly="true" ExcludeAssets="runtime" />
    <ProjectReference Include="..\..\FlexBot.OneBotLib\FlexBot.OneBotLib.csproj" Private="false" />
  </ItemGroup>
</Project>
```

插件间依赖：在插件目录写 `plugin.json`：`{"Depends": ["FileSystem"]}`，加载时自动先载依赖并共享类型。

`IBotContext` 能力：消息/事件订阅（`Messages`/`Events`）、OneBot API（`Api`）、配置与设置（`Config`/`GetSetting`）、
定时器（`Scheduler`）、KV 存储（`KV`）、共享 HttpClient（`Http`）、跨插件命令调用（`TryInvokeCommandAsync`）、
统一编译服务（`Compiler`，脚本与插件共用宿主那一个 Roslyn，不必自带编译器）。
完整接口源码可用 `!pluginapi all`（或 `!pluginapi member IBotContext`）在线反编译查看。

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
