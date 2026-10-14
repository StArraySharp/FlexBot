using System;
using System.Threading.Tasks;

namespace FlexBot;

internal sealed class CommandDispatcher
{
    private readonly PluginManager _plugins;
    private readonly HostConfig _config;

    public CommandDispatcher(PluginManager plugins, HostConfig config)
    {
        _plugins = plugins;
        _config = config;
    }

    /// <summary>
    /// 命令分发（② 拆分：原为 Program 的局部函数 TryDispatchCommandAsync）。只依赖 plugins 与 config，
    /// 注意：它的构造仍需要真实的 PluginManager 与 HostConfig（两者都还不是接口），
    /// 所以"可单测"这一步**尚未真正解锁** —— 需先为插件注册表抽一个最小接口。此处的收益是结构清晰与职责归位。</summary>
    public async Task<bool> DispatchAsync(string text, bool isAdmin, Func<string, Task> reply)
        {
            var prefix = Program.CurrentPrefix(_plugins, _config);
            if (string.IsNullOrEmpty(prefix) || !text.StartsWith(prefix, StringComparison.Ordinal))
                return false;

            var body = text[prefix.Length..].TrimStart();
            if (body.Length == 0) return false;
            var sp = body.IndexOf(' ');
            var cmdName = (sp < 0 ? body : body[..sp]).ToLowerInvariant();
            var args = sp < 0 ? "" : body[(sp + 1)..].Trim();

            // 宿主内置命令：plugin
            if (cmdName == "plugin")
            {
                if (!isAdmin) { await reply("仅管理员可用"); return true; }
                await reply(await _plugins.HandleCommandAsync(text));
                return true;
            }
            // 宿主内置命令：session（会话 id 与记忆组；关联/分离全部写审计）
            if (cmdName == "session")
            {
                if (!isAdmin) { await reply("仅管理员可用"); return true; }
                await reply(SessionRegistry.HandleCommand(args) ?? "会话注册表未启用");
                return true;
            }
            // 宿主内置命令：help / 帮助
            if (cmdName is "help" or "帮助")
            {
            await reply(Program.BuildHelpText(prefix, _plugins));
                return true;
            }

            // 插件注册的命令
            var reg = _plugins.FindCommand(cmdName);
            if (reg is null) return false;
            if (!isAdmin) { await reply($"命令 {prefix}{cmdName} 仅管理员可用"); return true; }
            try
            {
                var result = await reg.Value.Handler(args);
                if (!string.IsNullOrEmpty(result)) await reply(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cmd] {prefix}{cmdName} 执行异常: {ex.Message}");
                await reply($"命令执行出错: {ex.Message}");
            }
            return true;
        }
}
