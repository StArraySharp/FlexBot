// ============================================================================
// JsonKit —— Newtonsoft.Json 工具（演示：第三方 NuGet 依赖由宿主自动解析）
//
// 本插件**不带任何 DLL**：依赖写在 plugin.json 的 NuGet 字段里
//   "NuGet": ["Newtonsoft.Json@13.0.4"]
// 宿主加载插件时自动从 NuGet 源下载（含传递依赖）并铺进 plugins/JsonKit/libs/，
// 随后编译期当引用、运行期由 ALC 从影子目录解析 —— 不需要 csproj、不需要 SDK、
// 也不需要任何外部脚本或手工拷 DLL。
//
// 用法：
//   群里任何人发：json {"a":1,"b":[1,2,3]}   → 美化并回报类型/深度
//   管理员命令：  !jsonver                    → 版本与程序集来源
//                 !jsonmin {"a": 1}           → 压缩
// ============================================================================
using FlexBot.PluginApi;
using OneBotLib.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JsonKit;

/// <summary>反序列化自检用的载荷</summary>
public sealed record Sample(string Name, int Age, List<string> Tags);

public sealed class JsonKitPlugin : IBotPlugin
{
    private IBotContext _ctx = null!;

    public string Name => "JsonKit";
    public string Version => "1.0.0";
    public string Description => "Newtonsoft.Json JSON 工具（依赖由宿主自动解析到 libs/）";

    public Task OnLoadAsync(IBotContext ctx)
    {
        _ctx = ctx;

        // 自检：序列化 → 反序列化 → 比对，确认 Newtonsoft 在插件的 ALC 里真的可用
        var src = new Sample("yg", 30, ["a", "b"]);
        var json = JsonConvert.SerializeObject(src);
        var back = JsonConvert.DeserializeObject<Sample>(json);
        var ok = back is not null && back.Name == src.Name && back.Age == src.Age && back.Tags.Count == 2;
        ctx.Log.Info($"Newtonsoft.Json {Ver()} 已就绪；自检 {json} → {(ok ? "OK" : "不一致")}");

        ctx.RegisterCommand("jsonver", "Newtonsoft.Json 版本与程序集来源", _ => Task.FromResult(
            $"Newtonsoft.Json {Ver()}\n程序集: {typeof(JsonConvert).Assembly.Location}"));
        ctx.RegisterCommand("jsonmin", "压缩 JSON", a => Task.FromResult(Transform(a, minify: true)));

        ctx.Messages.OnGroup(OnGroupAsync, priority: 46, tag: Name);
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync() => Task.CompletedTask;

    private async Task<Handled> OnGroupAsync(GroupMessageEventArgs e)
    {
        var text = (e.Message.PlainText ?? "").Trim();
        if (!text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) return Handled.Continue;

        var body = text[4..].Trim();
        if (body.Length == 0) return Handled.Continue;

        // 只对"看起来是 JSON"的内容回应，避免聊天里出现 json 字样就被刷屏
        var looksLikeJson = body.StartsWith('{') || body.StartsWith('[');
        string reply;
        if (looksLikeJson) reply = Transform(body, minify: false);
        else return Handled.Continue;

        await _ctx.Api.SendGroupMsgAsync(e.Message.GroupId ?? 0, Msg.Quote(e.Message.MessageId, reply));
        return Handled.Stop;
    }

    private string Transform(string body, bool minify)
    {
        if (string.IsNullOrWhiteSpace(body)) return "用法：json {\"a\":1}";
        if (body.Length > 8000) return "内容太长（上限 8000 字符）";
        try
        {
            var token = JToken.Parse(body);
            var text = token.ToString(minify ? Formatting.None : Formatting.Indented);
            var max = Math.Max(200, _ctx.GetSetting("MaxChars", 1500));
            if (text.Length > max) text = text[..max] + "\n…（已截断）";
            return $"{KindOf(token)} · 深度 {Depth(token)}\n{text}";
        }
        catch (JsonException ex)
        {
            return $"JSON 解析失败：{ex.Message}";
        }
    }

    private static string KindOf(JToken t) => t switch
    {
        JObject o => $"对象（{o.Count} 个字段）",
        JArray a => $"数组（{a.Count} 项）",
        _ => "标量值"
    };

    private static int Depth(JToken t) => t switch
    {
        JObject o => o.Count == 0 ? 1 : 1 + o.Properties().Max(p => Depth(p.Value)),
        JArray a => a.Count == 0 ? 1 : 1 + a.Max(Depth),
        _ => 1
    };

    private static string Ver() => typeof(JsonConvert).Assembly.GetName().Version?.ToString() ?? "?";
}
