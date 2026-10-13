using FlexBot;
using FlexBot.PluginApi;
using Xunit;

namespace FlexBot.Tests;

/// <summary>
/// 只测"最容易回归、代价最高"的几处：配置机密加解密、插件名安全校验、消息去重、配置落盘形态。
/// 这些逻辑一旦回归，后果分别是：密钥解不开、路径穿越、重复回复、机密以明文落盘。
/// </summary>
public class CoreTests
{
    // ---------- SecretBox ----------

    [Fact]
    public void SecretBox_RoundTrip()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"fb-key-{Guid.NewGuid():N}.txt");
        try
        {
            SecretBox.Init(keyPath);
            Assert.True(SecretBox.IsReady);
            Assert.True(File.Exists(keyPath));

            const string plain = "sk-1234567890abcdefghijklmnopqrstuv";
            var enc = SecretBox.Encrypt(plain);
            Assert.StartsWith(SecretBox.Prefix, enc);
            Assert.NotEqual(plain, enc);
            Assert.Equal(plain, SecretBox.Decrypt(enc));

            // 幂等：已是密文不再套一层
            Assert.Equal(enc, SecretBox.Encrypt(enc));
            // 非密文原样返回（兼容旧明文配置）
            Assert.Equal("plain-text", SecretBox.Decrypt("plain-text"));
            Assert.Equal("", SecretBox.Encrypt(""));
            // 掩码不回显原文
            Assert.DoesNotContain("abcdef", SecretBox.Mask(plain));
        }
        finally { if (File.Exists(keyPath)) File.Delete(keyPath); }
    }

    [Fact]
    public void SecretBox_WrongKey_DecryptsToEmpty()
    {
        var keyA = Path.Combine(Path.GetTempPath(), $"fb-key-a-{Guid.NewGuid():N}.txt");
        var keyB = Path.Combine(Path.GetTempPath(), $"fb-key-b-{Guid.NewGuid():N}.txt");
        try
        {
            SecretBox.Init(keyA);
            var enc = SecretBox.Encrypt("secret-value");

            SecretBox.Init(keyB); // 换一把钥匙
            Assert.Equal("", SecretBox.Decrypt(enc));   // 解不开时返回空串，绝不把密文当明文用
        }
        finally
        {
            if (File.Exists(keyA)) File.Delete(keyA);
            if (File.Exists(keyB)) File.Delete(keyB);
        }
    }

    // ---------- 插件名安全 ----------

    [Theory]
    [InlineData("Agent", true)]
    [InlineData("我的插件", true)]
    [InlineData("a-b_c.d", true)]
    [InlineData("..", false)]
    [InlineData("../etc", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData(".hidden", false)]
    [InlineData("C:", false)]
    [InlineData("a<b", false)]
    [InlineData("", false)]
    public void IsSafePluginName_RejectsTraversalAndSeparators(string name, bool expected)
        => Assert.Equal(expected, PluginManager.IsSafePluginName(name));

    [Fact]
    public void IsSafePluginName_RejectsOverlongAndControlChars()
    {
        Assert.False(PluginManager.IsSafePluginName(new string('a', 200)));
        Assert.False(PluginManager.IsSafePluginName("bad\u0001name"));
    }

    // ---------- 消息去重 ----------

    [Fact]
    public void MessageDedup_SameKeyOnlyOnce()
    {
        var key = $"test:{Guid.NewGuid():N}";
        Assert.False(MessageDedup.IsDuplicate(key));   // 第一次：新消息
        Assert.True(MessageDedup.IsDuplicate(key));    // 第二次：重复（会被丢弃）
        Assert.True(MessageDedup.IsDuplicate(key));

        // 不同 id 不受影响（用户先后发两条同样内容的消息是两个 id）
        var other = $"test:{Guid.NewGuid():N}";
        Assert.False(MessageDedup.IsDuplicate(other));
    }

    // ---------- 配置落盘形态 ----------

    [Fact]
    public void HostSettings_SaveEncryptsSecrets_LoadDecrypts()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"fb-key-cfg-{Guid.NewGuid():N}.txt");
        var cfgPath = Path.Combine(Path.GetTempPath(), $"fb-cfg-{Guid.NewGuid():N}.json");
        try
        {
            SecretBox.Init(keyPath);
            // LoadFrom 会把 FilePath 设好并写入一次（不存在则用默认值）
            var s = HostSettings.LoadFrom(cfgPath);
            s.OfficialAppSecret = "QQBOT-SECRET-abcdefghijklmnop";
            s.Token = "onebot-token-value";
            s.Save();

            // 磁盘上必须是密文，且不出现明文
            var raw = File.ReadAllText(cfgPath);
            Assert.Contains(SecretBox.Prefix, raw);
            Assert.DoesNotContain("QQBOT-SECRET-abcdefghijklmnop", raw);
            Assert.DoesNotContain("onebot-token-value", raw);

            // Load 之后运行时拿到的是明文（注意 Load 会顺手再 Save 一次，属预期）
            var reloaded = HostSettings.LoadFrom(cfgPath);
            Assert.Equal("QQBOT-SECRET-abcdefghijklmnop", reloaded.OfficialAppSecret);
            Assert.Equal("onebot-token-value", reloaded.Token);
        }
        finally
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
            if (File.Exists(cfgPath)) File.Delete(cfgPath);
        }
    }
}
