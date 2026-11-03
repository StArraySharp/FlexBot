using System.Text.Json;
using FlexBot.PluginApi;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// 设置 JSON 的大小写契约：WebUI 写入 camelCase，插件侧必须用 <see cref="BotJson.Setting"/> 读取。
///
/// 这是回归护栏 —— 曾因用默认（大小写敏感）选项反序列化，字段全部静默变 null：
/// 备用模型回落链与人格配置"保存成功但完全不生效"，且不报任何错。详见 BotJson.Setting 注释。
/// </summary>
public class SettingJsonTests
{
    private sealed class FbEntry
    {
        public bool? Enabled { get; set; }
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public string? ApiKey { get; set; }
    }

    private sealed class PersonaEntry
    {
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string? File { get; set; }
    }

    [Fact]
    public void 前端写入的camelCase能被正确读取()
    {
        var arr = JsonSerializer.Deserialize<List<FbEntry>>(
            """[{"enabled":false,"baseUrl":"http://x","model":"m","apiKey":"k"}]""", BotJson.Setting);
        var f = Assert.Single(arr!);
        Assert.False(f.Enabled);
        Assert.Equal("http://x", f.BaseUrl);
        Assert.Equal("m", f.Model);
        Assert.Equal("k", f.ApiKey);
    }

    [Fact]
    public void 旧配置缺少enabled字段时视为启用()
    {
        var arr = JsonSerializer.Deserialize<List<FbEntry>>(
            """[{"baseUrl":"http://x","model":"m"}]""", BotJson.Setting);
        // 可空是关键：null 表示"旧配置没有该字段"，ParseFallbacks 用 ?? true 判定为启用，
        // 若改成非空 bool，旧配置会被解析成 false 而全部停用。
        Assert.Null(arr![0].Enabled);
        Assert.True(arr[0].Enabled ?? true);
    }

    [Fact]
    public void 人格元数据同样能读()
    {
        var arr = JsonSerializer.Deserialize<List<PersonaEntry>>(
            """[{"name":"Kobe","enabled":true,"file":"kobe.md"}]""", BotJson.Setting);
        var p = Assert.Single(arr!);
        Assert.Equal("Kobe", p.Name);
        Assert.True(p.Enabled);
        Assert.Equal("kobe.md", p.File);
    }
}
