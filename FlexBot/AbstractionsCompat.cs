using System.Runtime.CompilerServices;
using FlexBot.PluginApi;

// 契约层已迁出到独立程序集 FlexBot.Abstractions（命名空间 FlexBot.PluginApi 保持不变）。
// 这里为全部契约类型保留类型转发，使「针对旧整体宿主编译的插件 DLL」在二进制层面继续可用：
// 旧插件引用的是 FlexBot 程序集里的 FlexBot.PluginApi.*，运行时会经转发落到 FlexBot.Abstractions。
// 新插件应直接引用 FlexBot.Abstractions 项目（不再依赖宿主 exe）。
[assembly: TypeForwardedTo(typeof(IBotPlugin))]
[assembly: TypeForwardedTo(typeof(IBotContext))]
[assembly: TypeForwardedTo(typeof(IBotApi))]
[assembly: TypeForwardedTo(typeof(IBotConfig))]
[assembly: TypeForwardedTo(typeof(ILog))]
[assembly: TypeForwardedTo(typeof(IMessageHub))]
[assembly: TypeForwardedTo(typeof(IEventHub))]
[assembly: TypeForwardedTo(typeof(Handled))]
[assembly: TypeForwardedTo(typeof(CommandInfo))]
[assembly: TypeForwardedTo(typeof(CommandHandler))]
[assembly: TypeForwardedTo(typeof(PluginSettingDef))]
[assembly: TypeForwardedTo(typeof(ModelEndpoint))]
[assembly: TypeForwardedTo(typeof(IBotScheduler))]
[assembly: TypeForwardedTo(typeof(BotScheduler))]
[assembly: TypeForwardedTo(typeof(PluginKeyValueStore))]
[assembly: TypeForwardedTo(typeof(SharedHttp))]
[assembly: TypeForwardedTo(typeof(BotJson))]
[assembly: TypeForwardedTo(typeof(BotHelpers))]
[assembly: TypeForwardedTo(typeof(Msg))]
