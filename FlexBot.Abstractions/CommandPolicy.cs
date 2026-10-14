namespace FlexBot.PluginApi;

/// <summary>
/// 命令的可见范围策略。
///
/// 背景：`fs_*`（文件工作区）与 `pb_*`（插件工厂）能读写文件、甚至生成并编译插件源码。
/// 这两个命令族**只在私聊可用**：群里的消息内容不可信（任何人都能往上下文里塞指令），
/// 而门禁判的是"谁发的消息"而不是"内容是否可信" —— 主人在群里被 @ 时，
/// 夹带的注入文本可能被引导去调用这些命令。直接按"群内禁用"切断这条路径，最省心也最彻底。
///
/// 宿主在群消息入口与 Agent 工具层都会用这里的判断，避免两处策略走偏。
/// </summary>
public static class CommandPolicy
{
    /// <summary>仅限私聊的命令前缀（新增高危命令族时加到这里）</summary>
    public static readonly string[] PrivateOnlyPrefixes = ["fs_", "pb_"];

    /// <summary>该命令是否只能在私聊使用</summary>
    public static bool IsPrivateOnly(string? commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName)) return false;
        foreach (var p in PrivateOnlyPrefixes)
            if (commandName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>群内命中仅私聊命令时的统一回执</summary>
    public const string GroupDenyMessage = "该命令仅限私聊使用（群里已禁用，防提示注入）。";
}
