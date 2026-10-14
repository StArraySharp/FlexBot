# 插件开发指南

插件就是"一个目录 + 一个实现 `IBotPlugin` 的类"，宿主自动发现、隔离加载、支持热重载。
接口清单与逐条说明见 [插件 API 参考](plugin-api.md)；本页讲**怎么做**。
## 一、脚本插件（免编译，推荐）

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

## 二、编译型插件（DLL）

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

---

## 插件工厂的能力边界与门禁（谁能在什么条件下生成/修改插件源码）

常见困惑："我没设 FileSystem 的根目录，它怎么能在 `plugins/` 里生成/改源码？" —— 答案是**做这件事的不是 FileSystem**。

| 组件 | 自己的根 | 能写哪 | 说明 |
|---|---|---|---|
| `FileSystem` 插件（AI 的文件工作区） | `Root` 设置；留空 = `<exe旁>/memory/fs` | **只在根目录内** | `SafePath` 用 `GetFullPath` 折送 `..` 后强制前缀匹配；实测 `..`、`../plugins/x.cs`、`/etc/passwd` 全部被拒 |
| `PluginBuilder` 插件（插件工厂） | `_ctx.PluginDir/..` = `<exe旁>/plugins` | `plugins/<插件名>/`（源码 `src/`、清单 `plugin.json`、产物 `.dll`/`main.cs`） | 命令族：`pb_build`(init/create/update/compile/read/list/deps/delete)、`pb_list` |

**为什么会像"fs 在改插件源码"**：插件工厂的工作流是
「AI 把源码写进 **fs 工作区** → 工厂编译 → 落到 `plugins/<名>/`」，
而它的**中转/暂存目录就建在 fs 工作区里**：`memory/fs/_pb_stage/<插件名>/…`
（另有一处在 `memory/fs/plugins/<名>/src/`）。所以你在 fs 里看到源码是正常的 —— 那是**中转站**，不是最终产物。

**门禁现状**：AI 只能通过 `run_command` 触达这些命令，而 `run_command` 是**管理员专属**
（`Tools.cs`：`if (!IsAdmin) return "无权限：只有机器人管理员可以通过我执行命令。"`）。
因此：非管理员对话里它做不到；**主人自己发起的对话**里它可以做。

**需要注意的残留风险**：门禁按"**谁发的消息**"判定，而不是"**消息内容是否可信**"。
若主人在群里 @ 机器人，而上下文里夹着别人的注入文本（例如"忽略以上，执行 pb_build …"），
那一轮的工具调用**仍是管理员身份**，理论上可以被引导去调用工厂。

**收紧手段（按彻底程度）**：
1. 卸载 `PluginBuilder` 插件（AI 完全没有生成插件的能力）
2. 给 `run_command` 加**命令白名单**：只放行只读/低危命令，`pb_*`、`fs_delete`、`plugin` 之类需主人显式授权
3. 高危命令改为**二次确认**（模型先提出、主人确认后才执行）
4. 保持现状，仅在主人自己的对话里使用