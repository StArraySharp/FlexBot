using System.Text;
using System.Runtime.InteropServices;
using FlexBot;
using FlexBot.PluginApi;
using FlexBot.WebUi;
using FlexBotWebUi = FlexBot.WebUi.WebUi;   // 类名与命名空间同名，这里消歧
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Events;

namespace FlexBot;

/// <summary>② 拆分：从 Program.cs 提升出来的纯函数（原为局部函数）。
/// 做法是"传参化"——原先捕获的 plugins 改成显式参数，逻辑一字未改。</summary>
internal static partial class Program
{
    internal static string BuildHelpText(string prefix, PluginManager plugins)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"可用命令（前缀 {prefix}）：");
            sb.AppendLine($"{prefix}help - 显示本帮助");
            sb.AppendLine($"{prefix}plugin [list|load|unload|reload] - 插件管理");
                sb.AppendLine($"{prefix}bind qq <QQ号> <口令> - 官方通道：把当前身份绑定到 QQ 号（口令在宿主控制台）");
            foreach (var c in plugins.GetCommands())
            {
                var usage = string.IsNullOrEmpty(c.Usage) ? c.Name : c.Usage;
                sb.AppendLine($"{prefix}{usage} - {c.Description}（{c.PluginName}）");
            }
            return sb.ToString().TrimEnd();
        }
    internal static string CurrentPrefix(PluginManager plugins, HostConfig config) =>
            plugins.GetPluginSettingString("Admin", "CommandPrefix") is { Length: > 0 } p ? p
                : string.IsNullOrEmpty(config.CommandPrefix) ? "!" : config.CommandPrefix;
}
