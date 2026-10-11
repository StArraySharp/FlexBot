# FlexBot 文档

| 文档 | 内容 |
|---|---|
| [plugin-api.md](plugin-api.md) | 插件开发参考：`IBotPlugin` / `IBotContext` / `IBotApi` 契约、消息模型、消息与事件 Hub、内置服务、设置项、两个通道的能力差异 |
| [http-api.md](http-api.md) | 宿主 HTTP API：认证方式、全部端点、请求/响应示例、静态资源与热更新语义 |

面向最终用户的使用说明见仓库根目录的 [`README.md`](../README.md)；发布包内另附 `使用说明.txt`。

---

## 5 分钟写出第一个插件

### 1. 最小可运行插件（编译型）

一个插件 = 一个实现 `IBotPlugin` 的类，放在 `plugins/<插件名>/` 目录里：

```csharp
using FlexBot.PluginApi;          // IBotPlugin / IBotContext / IBotApi / BotHelpers / Handled
using OneBotLib.MessageSegment;  // MessageSegment（发图/at/回复等）
using OneBotLib.Models;          // MessageObject / SenderInfo

public sealed class HelloPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;

    public string Name => "Hello";
    public string Version => "1.0.0";
    public string Description => "示例：被 @ 时回一句话，并注册一个命令";

    public Task OnLoadAsync(IBotContext context)
    {
        _ctx = context;

        // 群消息（优先级越高越先拿到；返回 Handled.Stop 表示不再往下传）
        context.Messages.OnGroup(async e =>
        {
            if (!BotHelpers.IsAtMe(e.Message, context.Api.SelfId)) return Handled.Continue;
            await context.Api.SendGroupMsgAsync(e.Message.GroupId!.Value,
                BotHelpers.Quote(e.Message.MessageId, "你好，我是 FlexBot。"));
            return Handled.Stop;
        });

        // 注册命令：!hello [名字]
        context.RegisterCommand("hello", "打个招呼", args =>
            Task.FromResult($"你好，{args.Trim() switch { "" => "陌生人", var n => n }}！"), "hello [名字]");

        return Task.CompletedTask;
    }

    public Task OnUnloadAsync() => Task.CompletedTask;   // 取消订阅由宿主按 RegisterCommand/Hub 的用法自动清理
}
```

编译成 DLL 放进 `plugins/Hello/`，或者把插件目录打包成 zip 在 WebUI「插件页 → 添加插件」里上传（会一次性安装目录内的多个插件）。

### 2. 脚本插件（免编译）

`plugins/<名>/main.csx` 用同样的契约；宿主持有 Roslyn，第一次加载时内存编译并缓存（改文件后重新加载即重新编译）：

```csharp
using FlexBot.PluginApi;
using OneBotLib.Models;

public sealed class MyScriptPlugin : IBotPlugin
{
    public string Name => "MyScript";
    public string Version => "1.0.0";
    public string Description => "csx 脚本插件";
    public Task OnLoadAsync(IBotContext ctx)
    {
        ctx.Log.Info("脚本插件已加载");
        ctx.Scheduler.Every(TimeSpan.FromMinutes(10), () => { ctx.Log.Info("心跳"); return Task.CompletedTask; }, "心跳");
        return Task.CompletedTask;
    }
    public Task OnUnloadAsync() => Task.CompletedTask;
}
```

需要第三方 NuGet 时，在 `plugin.json` 的 `NuGet` 里声明，宿主的解析器会把包铺进该插件的 `libs/`（编译期与运行期都从那里取，插件作者不需要任何外部工具）。示例见 [`samples/csx-json`](../samples/csx-json)。

### 3. 目录约定

```
plugins/
└─ <插件名>/
   ├─ <插件名>.dll 或 main.csx   # 入口程序集 / 脚本
   ├─ plugin.json                # 清单（可选，但推荐）
   ├─ settings.json              # 设置值（由 WebUI「插件设置」写入，宿主维护）
   ├─ libs/                      # 该插件自带的第三方依赖（NuGet 自动铺或手工放入）
   └─ data/                      # 插件私有数据（ctx.DataDir 指向这里；KV 存 kv.json）
```

`plugin.json` 字段：

| 字段 | 说明 |
|---|---|
| `Depends` | 依赖的其它插件名数组；被依赖插件先加载，实例从对方的 ALC 借用（类型同一性，随对方卸载） |
| `Version` / `Description` | 展示信息（已加载时以插件类里的值为准） |
| `Settings` | 设置项声明；插件类未实现 `SettingDefs` 时用它（脚本插件的主要配置入口） |
| `NuGet` | 第三方 NuGet 依赖（自动铺进 `libs/`） |
| `Provenance` | 来源标注（`ai` / `manual`），由 PluginBuilder 写入，用于插件页的来源徽标 |

### 4. 设置项类型

`PluginSettingDef(Key, Label, Type, Default, Description, Options)` 的 `Type` 取值：

| Type | WebUI 呈现 | 值的形式 |
|---|---|---|
| `text` | 单行输入 | 字符串 |
| `number` | 数字输入 | 字符串（读取时用 `ctx.GetSetting<int>(...)`） |
| `bool` | 开关 | `"true"` / `"false"` |
| `password` | 密码框（不回显原值；留空=不修改，`-` = 清空） | 字符串 |
| `select` | 下拉框（用 `Options`） | 字符串 |
| `models` | 备用模型卡片列表 | JSON 数组字符串 `[{"baseUrl","model","apiKey"}]` |
| `personas` | 人格卡片列表（单选启用） | JSON 数组字符串 `[{"name","enabled","file"}]` |

保存后宿主调用 `OnSettingsChangedAsync()`；需要重建内部状态（如重建模型客户端）的插件在这里做。JSON 与 `PluginSettingDef` 都允许注释与尾逗号。

### 5. 两个通道的能力差异（务必知道）

宿主有两条通道，插件只面向 `IBotApi` 编程；切换通道靠配置，**同一时刻只有一条是活的**：

| 能力 | OneBot（LLBot / NapCat） | QQ 官方机器人开放平台 |
|---|---|---|
| 收群消息 / 单聊 | ✓ | ✓（群内需被 @；群主可开全量消息） |
| 发文本、被动回复、主动消息 | ✓ | ✓（主动群消息有每日额度，单聊有窗口限制） |
| 撤回自己的消息 | ✓ | ✓ |
| 群成员列表 | ✓ | ✓ |
| 历史消息 / 合并转发 / 戳一戳 / 好友列表 / 群名片 / 群文件 / 禁言 / 踢人 / 加群审批 | ✓ | ✗（官方开放平台不提供，调用返回 `ApiResult.Fail("官方通道不支持：…")`） |
| 图片/语音/视频消息段 | ✓ | ○（当前降级为文本占位，图片理解可用） |
| 身份映射 | QQ 号即用户 id | openid 与 QQ 号需 `!bind qq <QQ号> <口令>` 绑定；`IBotApi` 里看到的仍是合成数字 id |

⇒ 插件若依赖上表中官方通道不支持的能力，请用 `ApiResult.Success` 判断并在失败时降级，别假设一定可用。
