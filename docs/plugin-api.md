# 插件 API 参考

契约程序集：**`FlexBot.Abstractions`**，命名空间 **`FlexBot.PluginApi`**（插件只需引用这个程序集；宿主程序集与 `OneBotLib` 由宿主提供，不要在插件里自带副本）。
消息模型来自程序集 **`FlexBot.OneBotLib`**，命名空间 `OneBotLib.Models`（`MessageObject` / `SenderInfo` 等）、`OneBotLib.MessageSegment`（`MessageSegment`）、`OneBotLib.Events`（事件参数）。

```csharp
using FlexBot.PluginApi;
using OneBotLib.MessageSegment;
using OneBotLib.Models;
```

---

## 1. IBotPlugin

宿主按目录扫描插件，找实现 `IBotPlugin` 的类型并实例化（需要公开无参构造函数）。

```csharp
public interface IBotPlugin
{
    string Name { get; }              // 插件名（唯一；同时是目录名）
    string Version { get; }           // 展示用版本
    string Description { get; }       // 一行说明（插件页显示）
    Task OnLoadAsync(IBotContext context);   // 加载：注册消息订阅/命令/定时任务
    Task OnUnloadAsync();                    // 卸载：释放自己持有的资源
    IReadOnlyList<PluginSettingDef> SettingDefs => [];   // 设置项声明（可省）
    Task OnSettingsChangedAsync() => Task.CompletedTask; // 设置保存后回调（可省）
}
```

- `OnLoadAsync` 抛异常 = 加载失败（插件页显示为未加载；Agent 插件缺 ApiKey 时就是这种预期失败）。
- 卸载时宿主会先卸载 ALC；请在 `OnUnloadAsync` 里停掉自己的线程/计时器，别再引用宿主对象。
- **不要**自己 `Task.Run` 长期循环而不留取消通道：宿主卸载后延续会跳进已释放的代码段。

---

## 2. IBotContext

```csharp
public interface IBotContext : IDisposable
{
    IBotApi Api { get; }               // 发消息/群操作/查询
    IBotConfig Config { get; }         // 宿主配置快照（主人、目录、命令前缀、模型参数…）
    string PluginDir { get; }          // plugins/<名>/ 的绝对路径
    string DataDir { get; }            // plugins/<名>/data/（已创建）
    IMessageHub Messages { get; }      // 消息订阅
    IEventHub Events { get; }          // 原始事件订阅
    ILog Log { get; }                  // 写宿主日志
    IBotScheduler Scheduler { get; }   // 定时任务
    PluginKeyValueStore KV { get; }    // 持久化键值（data/kv.json）
    SharedHttp Http { get; }           // 共享 HttpClient
    ICompilerService Compiler { get; } // Roslyn 编译服务（脚本插件用）

    T GetSetting<T>(string key, T? defaultValue = default);
    IReadOnlyDictionary<string, object?> GetAllSettings();
    IDisposable RegisterCommand(string name, string description, CommandHandler handler, string usage = "");
    Task<string?> TryInvokeCommandAsync(string name, string args = "");   // 触发别的插件注册的命令
    IReadOnlyList<CommandInfo> ListCommands();
}

public delegate Task<string> CommandHandler(string args);   // 返回值即回给用户的内容
public sealed record CommandInfo(string Name, string Description, string PluginName, string Usage = "");
```

命令名**不含前缀**（前缀由 `IBotConfig.CommandPrefix` 决定，默认 `!`）；`RegisterCommand` 返回的 `IDisposable` 在释放时注销该命令。

`IBotConfig` 关键成员：

```csharp
long OwnerUin { get; }                          // 主人 QQ（最高权限）
IReadOnlyCollection<long> AdminUins { get; }    // 主人 + 额外管理员
string MemoryDir { get; }                       // 记忆/运行数据根目录
string CommandPrefix { get; }                   // 命令前缀（默认 "!"）
string ActivePersonaInstructions { get; }       // 当前启用的人格提示词
string ApiKey { get; }  string BaseUrl { get; }  string Model { get; }
bool IsVisionModel { get; }                     // 主模型是否支持图片
IReadOnlyList<ModelEndpoint> FallbackModels { get; }
int MaxContextTurns { get; } int MaxMsgChars { get; } int MaxImageBytes { get; } int GroupHistoryCount { get; }
```

---

## 3. IBotApi

所有方法返回 `ApiResult` / `ApiResult<T>`：`bool Success`、`string ErrorMessage`（失败原因，官方通道的不支持能力会写明"官方通道不支持：…"），`ApiResult<T>` 另有数据载荷。

| 方法 | 说明 | 官方通道 |
|---|---|---|
| `SendPrivateMsgAsync(long userId, object message)` | 发私聊（`message` 可传字符串或 `List<MessageSegment>`） | ✓ |
| `SendGroupMsgAsync(long groupId, object message)` | 发群消息 | ✓ |
| `SendGroupForwardMsgAsync(long groupId, List<ForwardNode>)` | 群合并转发 | ✗ |
| `SendPrivateForwardMsgAsync(long userId, List<ForwardNode>)` | 私聊合并转发 | ✗ |
| `DeleteMsgAsync(long messageId)` | 撤回 | ✓（只能撤回自己发的） |
| `SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set = true)` | 贴表情回应 | ✗ |
| `SetGroupBanAsync(long groupId, long userId, long duration = 0)` | 禁言（0=解除） | ✗ |
| `SetGroupWholeBanAsync(long groupId, long duration = 0)` | 全员禁言 | ✗ |
| `SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest = false)` | 踢人 | ✗ |
| `SetGroupCardAsync(long groupId, long userId, string? card = null)` | 改群名片 | ✗ |
| `SetGroupSpecialTitleAsync(long groupId, long userId, string? title = null)` | 设专属头衔 | ✗ |
| `SendGroupNoticeAsync(long groupId, string content, string? image = null)` | 发群公告 | ✗ |
| `SetGroupLeaveAsync(long groupId, bool isDismiss = false)` | 退群/解散 | ✗ |
| `GetGroupInfoAsync(long groupId)` | 群信息 | ✗ |
| `GetFriendListAsync()` | 好友列表 | ✗ |
| `UploadGroupFileAsync(long groupId, string file, string name, string? folder = null)` | 传群文件 | ✗ |
| `SetFriendAddRequestAsync(string flag, bool approve = true, string? remark = null)` | 处理好友请求 | ✗ |
| `SetGroupAddRequestAsync(string flag, string subType, bool approve = true, string? reason = null)` | 处理加群请求 | ✗ |
| `GetMsgAsync(long messageId)` | 单条消息 | ✗ |
| `GetGroupMsgHistoryAsync(long groupId, long? messageId = null, int count = 20)` | 群历史 | ✗ |
| `GetGroupMemberListAsync(long groupId)` | 群成员列表 | ✓ |
| `GroupPokeAsync(long groupId, long userId)` | 群戳一戳 | ✗ |
| `GetLoginInfoAsync()` | 登录号信息 | ✓ |
| `CallApiAsync(string action, Dictionary<string, object>? parameters = null)` | 透传任意 OneBot 动作（官方通道返回失败） | ✗ |
| `long SelfId { get; }` | 机器人自身 id | ✓ |

**降级建议**：先判断 `Success`，失败时回一句人话或改走别的路径；不要因为官方通道不支持就让整个功能崩掉。

---

## 4. 消息模型

```csharp
public class MessageObject          // 收到的消息
{
    long SelfId, MessageId, RealId, MessageSeq, Time;
    long? UserId, GroupId;          // 群消息有 GroupId；单聊 GroupId 为 null
    string MessageType;             // "group" / "private"
    string RawMessage;              // 原始 CQ 文本（OneBot）
    List<MessageSegmentData> MessageSegments;   // 结构化段（text/at/image/face…）
    SenderInfo Sender;
}

public class SenderInfo
{
    long UserId; string Nickname, Card, Sex, Area, Level, Role; int Age;
}
```

发消息时的消息段（`FlexBot.OneBotLib.MessageSegment`，`object message` 可传 `string` 或 `List<MessageSegment>`）：

| 常用（两条通道均可用文本表达） | 仅 OneBot 通道有效（官方通道会被忽略/降级） |
|---|---|
| `Text(string)`、`At(long)`、`AtAll()`、`AtWithNick(long, string?)`、`Image(string file, …)`、`Face(int)`、`Reply(long messageId)`、`Mention(string)`、`Emoji(string)`、`Link(string url, string text)` | `Record` / `Video` / `Node`（合并转发节点）/ `Forward` / `Xml` / `Json` / `Location` / `Share` / `Contact` / `ContactGroup` / `Dice` / `Rps` / `Shake` / `Poke` / `Anonymous` / `Music` / `MusicCustom` / `File` / `Mface` / `Markdown` / `MiniApp` / `Ark` / `Keyboard` / `KeyboardButton` / `RichText` / `Gift` / `Card` / `Enotify` / `Bubble` / `Tts` / `MultiMsg` / `LongMsg` / `InlineKeyboard` |

`BotHelpers`（`FlexBot.Abstractions`）——常用判定与组装：

| 成员 | 用途 |
|---|---|
| `PlainText(MessageObject)` / `MsgToText(object)` | 取出纯文本（丢掉 at/图片等） |
| `StripMentions(string)` | 去掉文本里的 @ 片段 |
| `HasText(MessageObject)` | 是否含文本 |
| `GetAtTargets(MessageObject)` | 被 @ 的 QQ 号列表 |
| `IsAtMe` / `IsMentionedMe` / `IsAtAll` / `MentionsOthers(MessageObject, long selfId)` | @ 相关判定 |
| `At(long)` / `TextWithAt(string, params long[])` | 组装消息段 |
| `GetReplyToId(MessageObject)` | 取被引用消息 id |
| `GetQuotedMessageAsync(IBotApi, MessageObject)` / `GetQuotedTextAsync(…)` | 取被引用的那条消息/文本 |
| `Quote(long messageId, string text)` | 组装「引用 + 文本」消息段（等价 `Msg.Quote`） |
| `FindMembersAsync(IBotApi, long groupId, string keyword, int limit)` | 按昵称/名片模糊找成员 |
| `GetMemberNameAsync(IBotApi, long groupId, long userId)` | 取群名片或昵称 |
| `GetMemberListTextAsync(IBotApi, long groupId, int max = 200)` | 成员列表文本 |
| `GetRecentGroupTextAsync(IBotApi, long groupId, int count = 20)` | 近期群消息文本（上下文用） |

`Msg` 静态类目前提供 `Msg.Quote(long messageId, string text)`。

---

## 5. 消息与事件订阅

```csharp
public enum Handled { Continue, Stop }   // Stop = 不再传给优先级更低的订阅者

public interface IMessageHub
{
    IDisposable OnPrivate(Func<PrivateMessageEventArgs, Task<Handled>> handler, int priority = 0, string? tag = null);
    IDisposable OnGroup  (Func<GroupMessageEventArgs,   Task<Handled>> handler, int priority = 0, string? tag = null);
}

public interface IEventHub
{
    // 订阅宿主原始事件（连接状态、群成员变动、戳一戳、加群请求…）：事件参数类型来自 FlexBot.OneBotLib
    IDisposable On<TEvent>(Func<TEvent, Task> handler, string? tag = null) where TEvent : EventArgs;
}
```

- `priority` 越大越先执行；`tag` 便于调试与批量注销。
- 返回 `Handled.Stop` 会阻止后续订阅者（以及宿主的默认处理）看到这条消息——**只在确实想"吃掉"消息时用**。
- 返回的 `IDisposable` 释放即取消订阅；插件卸载时宿主会一并清理。

---

## 6. 内置服务

| 服务 | 成员 | 说明 |
|---|---|---|
| `IBotScheduler`（`ctx.Scheduler`） | `Every(TimeSpan, Func<Task>, string? name)`、`DailyAt("HH:mm", …)`、`After(TimeSpan, …)` | 都返回 `IDisposable`（释放即取消）。`Every` 最小间隔 1 秒；任务异常被宿主捕获并写日志，不会中断调度 |
| `PluginKeyValueStore`（`ctx.KV`） | `Get<T>(key, default)`、`Set<T>(key, value)`、`Delete(key)`、`Keys` | 存 `plugins/<名>/data/kv.json`，键名大小写不敏感，写入 2 秒防抖合并落盘 |
| `SharedHttp`（`ctx.Http`） | `Client`、`GetStringAsync`、`GetByteArrayAsync`、`GetAsync`、`PostAsync` | 进程级共享 `HttpClient`（60s 超时、自动解压、最多 16 连接）。不要 `new HttpClient()` |
| `ICompilerService`（`ctx.Compiler`） | 内存编译 C# 源码，返回 `PluginCompileResult(bool Ok, byte[]? AssemblyBytes, byte[]? SymbolBytes, string Diagnostics, …)` | 脚本插件与 PluginBuilder 在用；完整签名见 `FlexBot.Abstractions/ICompilerService.cs` |
| `ILog`（`ctx.Log`） | `Info` / `Warn` / `Error(string)` / `Error(string, Exception)` | 写入宿主日志（WebUI 日志页可见） |

---

## 7. 宿主级契约（一般插件用不到）

| 类型 | 说明 |
|---|---|
| `IBotTransport : IBotApi, IAsyncDisposable` | 通道抽象：`TransportName`、`ConnectAsync(url, token)`、`DisconnectAsync()`、`SyncCommandPanelsAsync(...)`、`OnInteraction` 与全部消息事件。宿主按配置在 OneBot 与官方通道间二选一 |
| `BotCommandSpec(Name, Description, Usage, OnlyAdmin)` | 对外注册命令的规格（官方通道的"指令面板"用） |
| `BotInteraction(Text, UserId, GroupId, IsGroup, Id)` | 官方通道的按钮/快捷菜单回调（宿主会做 3 秒内 ack 并派发为命令） |
| `IIdentityBinder` | 官方通道的 openid ↔ QQ 号绑定（`!bind qq <QQ号> <口令>`，绑定表持久化） |

---

## 8. 线程与生命周期注意事项

1. **不要跨卸载持有宿主对象**：卸载后 ALC 会被回收，残留的引用会导致 `ObjectDisposedException` 或更隐蔽的崩溃。
2. **在途请求要能取消**：长任务用 `CancellationTokenSource` + `OnUnloadAsync` 里 `Cancel()`；宿主会等一段时间的在途任务归零。
3. **共享状态要用并发安全的容器或 `AsyncLocal`**：工具集这类"进程内单份"的对象上挂可变字段，会在多人并发时串号（Agent 插件就用 `AsyncLocal` 做轮内隔离）。
4. **异常处理**：消息处理里抛出的异常由宿主捕获记日志，但会导致该条消息的后续订阅者看不到它——自己 try/catch 更稳。
5. **`ctx` 与设置**：`GetSetting<T>` 读的是缓存值；设置页保存后会先写文件再回调 `OnSettingsChangedAsync()`，在那里刷新自己的派生状态（例如重建模型客户端）。
