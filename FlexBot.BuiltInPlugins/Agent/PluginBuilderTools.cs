using System.ComponentModel;
using System.Text.Json;
using FlexBot.PluginApi;
using Microsoft.Extensions.AI;

namespace AgentPlugin;

/// <summary>
/// PluginBuilder 工具桥：把"AI 生成插件"能力暴露给 Agent。
///
/// 流程：源码经 fs 沙箱暂存（fs_write 多行长文本不适合命令行）→ pb_build 命令装配/编译/加载。
/// 暂存约定必须与 PluginBuilder 一致：_pb_stage/&lt;插件名&gt;/&lt;文件名&gt;.cs
/// 命令通道由宿主分发，天然跨插件 ALC，无类型同一性问题。
///
/// 默认走脚本通道（免编译、免 csproj、无需 dotnet SDK）；需要第三方 NuGet 时用 kind=dll 走编译型。
///
/// 审计：每次生成都会额外写一份旁车 _pb_stage/&lt;名&gt;/.llm.json，内容为这次工具调用的**完整参数**
/// （含全部源码）＋模型/调用者/同轮助手文本。PluginBuilder 会把它落进插件的 generation.log
/// 与 generation.llm.jsonl，并据此把插件标记为"AI 生成"。
/// </summary>
class PluginBuilderTools(
    IBotContext ctx,
    Func<long> callerUin,
    Func<string> assistantText,
    Func<long> groupId)
{
    // 身份必须实时读取，不能快照：Agent 的工具集是在 AgentService 构造时就 CreateAll() 建好的，
    // 而 CurrentCallerUin 要到每轮对话开始（AskAgentAsync）才写入 —— 快照会拿到 0 而恒判非管理员。
    // 判定基准是 AdminUins（主人 ∪ 追加管理员），即管理员级权限。
    private bool IsAdmin => ctx.Config.AdminUins.Contains(callerUin());

    // 拒绝时带上识别到的身份与管理员列表：万一对不上，一眼能看出是身份没同步还是配置不对
    private string Denied() =>
        $"仅管理员可用（识别到 QQ={callerUin()}；管理员列表=[{string.Join(",", ctx.Config.AdminUins)}]）";

    public List<AIFunction> Create()
    {
        return
        [
            AIFunctionFactory.Create(CreatePlugin, name: "pb_create", description:
                "生成新 C# 插件并立即加载（仅管理员）。默认免编译脚本通道：不需要 csproj、不需要编译命令，写完即生效。"
                + "name=插件名(字母开头), description=一句话说明, source=完整 C# 源码(实现 FlexBot.PluginApi.IBotPlugin), "
                + "kind=script(默认,推荐)|dll(仅当需要第三方 NuGet 包时用)"),
            AIFunctionFactory.Create(UpdatePlugin, name: "pb_update", description:
                "更新已有插件源码并热重载（仅管理员；自动备份旧版到 backups/；脚本型/编译型自动识别）。name=插件名, source=完整新源码"),
            AIFunctionFactory.Create(CompilePlugin, name: "pb_compile", description:
                "编译校验插件（不加载，仅管理员）：脚本型做纯校验并回喂 文件(行,列) 级诊断，编译型做构建验证。改完先自检再 pb_update 更省事。name=插件名"),
            AIFunctionFactory.Create(ReadPlugin, name: "pb_read", description: "读取生成插件的当前源码（修改前先看现状）。name=插件名"),
            AIFunctionFactory.Create(ListPlugins, name: "pb_list", description: "列出全部已生成插件（[csx] 脚本型 / [dll] 编译型，并标注 ai/manual 来源）"),
            AIFunctionFactory.Create(DeletePlugin, name: "pb_delete", description: "删除生成插件（卸载+清目录，含源码与备份；不可恢复，仅管理员）。name=插件名"),
        ];
    }

    [Description("生成新的 C# 机器人插件并加载。源码需实现 FlexBot.PluginApi.IBotPlugin（OnLoadAsync 里 RegisterCommand / 订阅 Messages/Events）。"
        + "默认 kind=script：宿主用 Roslyn 内存编译，零 csproj、零编译命令。")]
    async Task<string> CreatePlugin(
        [Description("插件名：字母开头，仅字母数字下划线（如 DiceRoll）")] string name,
        [Description("一句话功能说明（显示在插件列表）")] string description,
        [Description("完整 C# 源码（单文件，含 using 与命名空间；System/Linq/Task 等已隐式注入）")] string source,
        [Description("插件形态：script=免编译热插件（默认，推荐）；dll=编译型（需要第三方 NuGet 时用）")] string kind = "script")
    {
        if (!IsAdmin) return Denied();

        if (string.Equals(kind, "dll", StringComparison.OrdinalIgnoreCase))
        {
            // 编译型：init 生成骨架 → 写入 AI 源码覆盖骨架主文件 → create 构建加载
            var init = await ctx.TryInvokeCommandAsync("pb_build", $"init {name}");
            if (init is null) return "PluginBuilder 未响应";
            if (!init.StartsWith("骨架已生成") && !init.Contains("已存在")) return init;

            var stageErr = await StageSource(name, name + ".cs", source, "pb_create", "dll", description);
            if (stageErr is not null) return stageErr;

            var write = await ctx.TryInvokeCommandAsync("pb_build", $"write {name} {name}.cs");
            if (write is null) return "PluginBuilder 未响应";
            if (!write.StartsWith("已写入")) return write;

            return await ctx.TryInvokeCommandAsync("pb_build", $"create {name} {description}") ?? "PluginBuilder 未响应";
        }

        var err = await StageSource(name, "main.cs", source, "pb_create", "script", description);
        if (err is not null) return err;
        return await ctx.TryInvokeCommandAsync("pb_build", $"script {name} {description}") ?? "PluginBuilder 未响应";
    }

    [Description("更新插件源码：暂存新源码 → 自动备份旧版 → 重新编译 → 热重载生效。脚本型与编译型都支持。")]
    async Task<string> UpdatePlugin(
        [Description("插件名")] string name,
        [Description("完整的新版 C# 源码")] string source)
    {
        if (!IsAdmin) return Denied();

        // 先用 read 探测形态（返回头标明 脚本型/编译型），决定暂存文件名与是否需要 write
        var probe = await ctx.TryInvokeCommandAsync("pb_build", $"read {name}");
        var isCompiled = probe is not null && probe.Contains("编译型");

        var err = await StageSource(name, isCompiled ? name + ".cs" : "main.cs", source, "pb_update",
            isCompiled ? "dll" : "script", null);
        if (err is not null) return err;

        if (isCompiled)
        {
            var write = await ctx.TryInvokeCommandAsync("pb_build", $"write {name} {name}.cs");
            if (write is null) return "PluginBuilder 未响应";
            if (!write.StartsWith("已写入")) return write;
        }

        // pb_build update 会按形态自动分流（脚本型走免编译热重载）
        return await ctx.TryInvokeCommandAsync("pb_build", $"update {name}") ?? "PluginBuilder 未响应";
    }

    // 源码走 fs 沙箱暂存（命令行不适合长多行文本）；路径约定与 PluginBuilder 一致。
    // 审计走 pb_build audit + base64：fs_write 会把内容里的字面 \n 还原成真换行，明文 JSON 经它必坏；
    // 源码也不在这里传输 —— PluginBuilder 归档时直接取"被编译的那一份"，更准。
    private async Task<string?> StageSource(
        string name, string fileName, string source, string tool, string kind, string? description)
    {
        var w = await ctx.TryInvokeCommandAsync("fs_write", $"_pb_stage/{name}/{fileName} {source.Replace("\n", "\\n")}");
        if (w is null) return "FileSystem 插件未加载（PluginBuilder 需要它暂存源码）";
        if (w.StartsWith("拒绝") || w.StartsWith("操作失败")) return $"源码暂存失败: {w}";

        try
        {
            var meta = JsonSerializer.Serialize(new
            {
                tool,
                kind,
                description,
                name,
                file = fileName,
                model = ctx.GetSetting("Model", ""),
                baseUrl = ctx.GetSetting("BaseUrl", ""),
                caller = callerUin(),
                groupId = groupId(),
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                assistantText = assistantText() ?? ""
            });
            var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(meta));
            var r = await ctx.TryInvokeCommandAsync("pb_build", $"audit {name} {b64}");
            if (r is null || !r.StartsWith("AI 审计已记录"))
                ctx.Log.Warn($"AI 审计记录失败（不影响生成）: {r}");
        }
        catch (Exception ex)
        {
            ctx.Log.Warn($"AI 审计记录异常（不影响生成）: {ex.Message}");
        }
        return null;
    }

    async Task<string> CompilePlugin([Description("插件名")] string name)
    {
        if (!IsAdmin) return Denied();
        return await ctx.TryInvokeCommandAsync("pb_build", $"compile {name}") ?? "PluginBuilder 未响应";
    }

    async Task<string> ReadPlugin([Description("插件名")] string name) =>
        await ctx.TryInvokeCommandAsync("pb_build", $"read {name}") ?? "PluginBuilder 未响应";

    async Task<string> ListPlugins() =>
        await ctx.TryInvokeCommandAsync("pb_build", "list") ?? "PluginBuilder 未响应";

    async Task<string> DeletePlugin([Description("插件名")] string name)
    {
        if (!IsAdmin) return Denied();
        return await ctx.TryInvokeCommandAsync("pb_build", $"delete {name}") ?? "PluginBuilder 未响应";
    }
}
