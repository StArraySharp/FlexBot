namespace FlexBot.PluginApi;

/// <summary>官方通道的"指令"定义（注册到官方指令面板/快捷菜单用）。
/// OnlyAdmin 对应官方 CommandPanelItem.OnlyAdmin：非管理员在客户端就看不到该指令。</summary>
public sealed record BotCommandSpec(string Name, string Description, string Usage, bool OnlyAdmin);

/// <summary>
/// 官方通道的互动回调（消息按钮 / 单聊快捷菜单 / 指令）。
/// 通道实现负责：把互动翻译成命令文本、并在调用本回调前/后完成官方要求的"3 秒内响应"。
/// OneBot 通道永不触发本事件。
/// </summary>
/// <param name="Text">命令文本（含前缀，如 "!ping 3"）</param>
/// <param name="UserId">发起者身份（已按绑定表解析：绑过就是 QQ 号）</param>
/// <param name="GroupId">群身份；单聊为 null</param>
/// <param name="IsGroup">是否群聊场景</param>
/// <param name="Id">官方 interaction id（仅用于日志/审计）</param>
public sealed record BotInteraction(string Text, long UserId, long? GroupId, bool IsGroup, string Id);
