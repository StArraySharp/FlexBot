# FlexBot

基于 OneBot 11 协议的 QQ 群机器人框架：.NET 10 宿主 + 可热重载的插件体系 + 内嵌 WebUI 管理后台。

## 特性

- **模块化插件**：每个插件独立目录、独立 AssemblyLoadContext 隔离，支持加载/卸载/热重载，依赖自动拓扑排序
- **AI 智能体**（Agent 插件）：流式分段回复、工具调用（联网搜索/看图/文件工作区/电脑控制/记忆）、多人格、备用模型回落、自动对话总结
- **内嵌 WebUI**：ASP.NET Core Minimal API + 单文件前端（嵌入宿主 DLL，磁盘零文件），插件管理、配置、日志、模型测试一站式
- **电脑控制**（PCControl 插件）：进程/截屏/剪贴板/鼠标/键盘/电源，PowerShell 远程执行
- **ILSpy 反编译查询**（PluginApi 插件）：聊天里直接查插件 API 源码
- **插件工厂**（PluginBuilder 插件）：对话式生成、编译、热加载新插件

## 解决方案结构

| 项目 | 说明 |
|---|---|
| `FlexBot/` | 宿主（Exe，net10.0）：BotClient、PluginManager、路由、WebUI、Photino 窗口 |
| `FlexBot/PluginApi/` | 插件契约层（编译进宿主，命名空间 `PluginApi`）：`IBotPlugin`、`IBotContext`、`IBotApi`、Hub、调度器、KV 存储 |
| `FlexBot.OneBotLib/` | OneBot 11 协议库（net8.0，可独立打包）：WebSocket 客户端、事件、消息段 |
| `FlexBot.BuiltInPlugins/*` | 内置插件（每个独立项目，输出到宿主 `plugins/`） |

### 内置插件

| 插件 | 功能 | 主要命令 |
|---|---|---|
| **Agent** | AI 对话（私聊/群聊 @、图片理解、联网搜索、记忆、人格、撤回即停） | 被 @ 或叫名字触发 |
| **Admin** | 主人功能：定时计划、禁用群 LLM、关注群、戳一戳、群名片通知 | `ping` `api` `timer` `cron` `nollm` `watch` 等 |
| **PCControl** | 控制宿主 Windows | `pc status\|exec\|ps\|kill\|open\|screenshot\|clip\|mouse\|key\|lock\|sleep\|shutdown\|restart\|cancel` |
| **FileSystem** | 给 AI 用的沙箱文件工作区 | `fs_read` `fs_write` `fs_search` 等 |
| **PluginBuilder** | 生成/编译/热载新插件 | `pb_build` `pb_list` |
| **PluginApi** | ILSpy 反编译查询插件 API 源码 | `pluginapi` / `papi` |
| **AOnline** | 在线状态/签到相关 | - |

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
  "OwnerUin": 10000,                 // 机器人主人 QQ（自动具有管理员权限）
  "AdminUins": [],                   // 额外管理员
  "NapCatCmd": "",                   // 可选：随宿主一键拉起 NapCat 的命令
  "WebUiPort": 0,                    // 0 = 自动分配空闲端口
  "WebUiPassword": ""                // 非空即启用 WebUI 登录；公网暴露必设
}
```

启动后控制台会打印 WebUI 地址（默认仅本机 `127.0.0.1`），浏览器打开即可管理。

### 模型配置

AI 模型配置在 WebUI → 插件 → Agent → 设置（存储于 `plugins/Agent/settings.json`，保存即热应用）：

- **ApiKey / BaseUrl / Model**：任意 OpenAI 兼容端点（DeepSeek、智谱、OpenAI 等）
- **FallbackModels**：主模型失败时按序回落的备用链
- **Personas**：多套人格提示词，`personas/*.md` 随设置一并保存
- **GroupChatEnabled**：群聊 AI 回复全局开关（单群粒度用 `!nollm <群号>`）
- **NameKeywords / ReplyProbability / ImageReplyProbability**：唤醒词与概率回复

## 命令速查

命令前缀默认 `!`（Admin 插件设置可改），插件命令仅管理员可用：

```
!help                                   # 全部命令
!plugin list|load|unload|reload <名称>   # 插件管理
!nollm <群号> / !unnollm <群号>          # 禁用/恢复某群的 AI 回复
!pc status|screenshot|...               # 电脑控制（见上表）
!pluginapi list|<类型名>|all|search      # 反编译查询插件 API
```

## 插件开发

实现 `PluginApi.IBotPlugin`，输出到 `plugins/<名称>/<名称>.dll` 即被自动发现：

```csharp
using PluginApi;

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

csproj 模板（关键点：引用宿主但 `Private="false"`、输出到宿主 `plugins/`、自带 NuGet 依赖）：

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
    <ProjectReference Include="..\..\FlexBot\FlexBot.csproj" ReferenceOutputAssembly="true" Private="false" />
    <ProjectReference Include="..\..\FlexBot.OneBotLib\FlexBot.OneBotLib.csproj" />
  </ItemGroup>
</Project>
```

插件间依赖：在插件目录写 `plugin.json`：`{"Depends": ["FileSystem"]}`，加载时自动先载依赖并共享类型。

`IBotContext` 能力：消息/事件订阅（`Messages`/`Events`）、OneBot API（`Api`）、配置与设置（`Config`/`GetSetting`）、定时器（`Scheduler`）、KV 存储（`KV`）、共享 HttpClient（`Http`）、跨插件命令调用（`TryInvokeCommandAsync`）。完整接口源码可用 `!pluginapi all` 在线反编译查看。

## 部署

- Windows：直接运行 `FlexBot.exe`（含 Photino 桌面窗口模式）
- Linux：`./start.sh` 启动，`watchdog.sh` 守护，`deploy-linux.sh` 一键部署

## 目录速览

```
FlexBot/
├─ Program.cs / BotClient.cs / PluginManager.cs / Routing.cs / Config.cs
├─ PluginApi/            # 插件契约（IBotPlugin、IBotContext、Hubs、服务）
├─ WebUi/                # 内嵌 WebUI（html/js/css 嵌入资源 + Minimal API）
└─ memory/               # 运行数据（构建后生成于输出目录 exe 旁）
FlexBot.OneBotLib/       # OneBot 11 协议库
FlexBot.BuiltInPlugins/  # 内置插件源码
```
