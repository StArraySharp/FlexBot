using System.ComponentModel;
using FlexBot.PluginApi;
using Microsoft.Extensions.AI;

namespace AgentPlugin;

/// <summary>
/// PluginBuilder 工具桥：把"AI 生成插件"能力暴露给 Agent。
/// 流程：源码经 fs 沙箱暂存（fs_write 多行长文本不适合命令行）→ pb_build 命令装配+编译+加载。
/// 命令通道由宿主分发，天然跨插件 ALC，无类型同一性问题。
/// </summary>
class PluginBuilderTools(IBotContext ctx)
{
    private bool IsOwner(long uin) => ctx.Config.AdminUins.Contains(uin);
    public long CurrentCallerUin; // 由 BotTools 每轮同步

    public List<AIFunction> Create()
    {
        return
        [
            AIFunctionFactory.Create(CreatePlugin, name: "pb_create", description: "生成新 C# 插件并编译加载（仅主人）。name=插件名(字母开头), description=一句话说明, source=完整 C# 源码(实现 PluginApi.IBotPlugin)"),
            AIFunctionFactory.Create(UpdatePlugin, name: "pb_update", description: "更新已有生成插件的源码并重编译热重载（仅主人；自动备份旧版到 backups/）。name=插件名, source=完整新源码"),
            AIFunctionFactory.Create(CompilePlugin, name: "pb_compile", description: "重新编译生成插件（只编译验证，不加载）（仅主人）。name=插件名"),
            AIFunctionFactory.Create(ReadPlugin, name: "pb_read", description: "读取生成插件的当前源码（修改前先看现状）。name=插件名"),
            AIFunctionFactory.Create(ListPlugins, name: "pb_list", description: "列出全部已生成插件与编译状态"),
            AIFunctionFactory.Create(DeletePlugin, name: "pb_delete", description: "删除生成插件（卸载+清目录，含源码与备份；不可恢复，仅主人）。name=插件名"),
        ];
    }

    [Description("生成新的 C# 机器人插件：写源码 → Roslyn 编译 → 自动加载。源码需实现 PluginApi.IBotPlugin 接口（OnLoadAsync 里 RegisterCommand/订阅消息）。")]
    async Task<string> CreatePlugin(
        [Description("插件名：字母开头，仅字母数字下划线（如 DiceRoll）")] string name,
        [Description("一句话功能说明（显示在插件列表）")] string description,
        [Description("完整 C# 源码（单文件，含 using 与命名空间）")] string source)
    {
        if (!IsOwner(CurrentCallerUin)) return "仅主人可用";
        return await StageAndBuildAsync("create", name, description, source);
    }

    [Description("更新生成插件源码：自动备份旧版 → 写新源码 → 重编译 → 热重载生效。")]
    async Task<string> UpdatePlugin(
        [Description("插件名")] string name,
        [Description("完整的新版 C# 源码")] string source)
    {
        if (!IsOwner(CurrentCallerUin)) return "仅主人可用";
        return await StageAndBuildAsync("update", name, null, source);
    }

    // 源码走 fs 沙箱暂存（命令行不适合长多行文本），PluginBuilder 从固定暂存路径读取
    private async Task<string> StageAndBuildAsync(string action, string name, string? description, string source)
    {
        var stageRel = $"_pb_stage/{name}.cs";
        var w = await ctx.TryInvokeCommandAsync("fs_write", $"{stageRel} {source.Replace("\n", "\\n")}");
        if (w is null) return "FileSystem 插件未加载（PluginBuilder 需要它暂存源码）";
        if (w.StartsWith("拒绝") || w.StartsWith("操作失败")) return $"源码暂存失败: {w}";

        var cmd = description is null ? $"{action} {name}" : $"{action} {name} {description}";
        return await ctx.TryInvokeCommandAsync("pb_build", cmd) ?? "PluginBuilder 未响应";
    }
    async Task<string> CompilePlugin([Description("插件名")] string name)
    {
        if (!IsOwner(CurrentCallerUin)) return "仅主人可用";
        return await ctx.TryInvokeCommandAsync("pb_build", $"compile {name}") ?? "PluginBuilder 未响应";
    }

    async Task<string> ReadPlugin([Description("插件名")] string name) =>
        await ctx.TryInvokeCommandAsync("pb_build", $"read {name}") ?? "PluginBuilder 未响应";

    async Task<string> ListPlugins() =>
        await ctx.TryInvokeCommandAsync("pb_build", "list") ?? "PluginBuilder 未响应";

    async Task<string> DeletePlugin([Description("插件名")] string name)
    {
        if (!IsOwner(CurrentCallerUin)) return "仅主人可用";
        return await ctx.TryInvokeCommandAsync("pb_build", $"delete {name}") ?? "PluginBuilder 未响应";
    }
}
