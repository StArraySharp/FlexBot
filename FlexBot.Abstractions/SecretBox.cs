using System.Security.Cryptography;
using System.Text;

namespace FlexBot.PluginApi;

/// <summary>
/// 跨平台的配置机密加密（AES-256-GCM + 本机密钥文件）。
///
/// 落地格式：<c>enc:v1:base64(nonce(12) | tag(16) | ciphertext)</c>；
/// 不带该前缀的值一律按**明文**处理，因此旧配置文件无需迁移脚本，首次启动会顺手加密。
///
/// 密钥默认在 <c>&lt;exe旁&gt;/.secret.key</c>（首次自动生成 32 字节随机密钥；类 Unix 上尽力设为仅属主可读写）。
/// 定位说明：这不是"防本机管理员"的机制（密钥就在旁边），目的是让机密**不要以明文散落**在
/// 配置文件、备份包、截图、git 历史与日志里。密钥文件丢失会导致已加密的值不可恢复（需重新填一次）。
///
/// 只用 BCL，Windows / Linux / macOS 行为一致。
/// </summary>
public static class SecretBox
{
    /// <summary>加密值前缀；改格式时递增版本号（v2…），旧值仍按旧版解密。</summary>
    public const string Prefix = "enc:v1:";

    private const int KeyBytes = 32;   // AES-256
    private const int NonceBytes = 12; // GCM 推荐 96 bit
    private const int TagBytes = 16;   // 128 bit

    private static readonly object _lock = new();
    private static byte[]? _key;
    private static bool _warned;

    /// <summary>是否已载入密钥（未载入时 Encrypt 直通明文、Decrypt 拒绝密文）</summary>
    public static bool IsReady { get { lock (_lock) return _key is not null; } }

    /// <summary>密钥文件路径（Init 之后才有值）</summary>
    public static string? KeyPath { get; private set; }

    /// <summary>
    /// 载入或生成密钥。宿主启动时调用一次；失败也不抛异常（加密降级为明文直通，功能不受影响）。
    /// </summary>
    public static void Init(string keyFilePath)
    {
        lock (_lock)
        {
            KeyPath = keyFilePath;
            try
            {
                if (File.Exists(keyFilePath))
                {
                    var b64 = File.ReadAllText(keyFilePath).Trim();
                    var key = Convert.FromBase64String(b64);
                    if (key.Length == KeyBytes) { _key = key; return; }
                    Console.WriteLine($"[secret] 密钥长度异常（{key.Length} 字节，应为 {KeyBytes}），将重新生成（原密文需重填）");
                }

                var fresh = RandomNumberGenerator.GetBytes(KeyBytes);
                Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
                File.WriteAllText(keyFilePath, Convert.ToBase64String(fresh) + Environment.NewLine);
                TryRestrictPermissions(keyFilePath);
                _key = fresh;
                Console.WriteLine($"[secret] 已生成配置加密密钥: {keyFilePath}（勿删除，删了已加密的机密需要重新填一次）");
            }
            catch (Exception ex)
            {
                _key = null;
                Console.WriteLine($"[secret] 密钥初始化失败，机密将以明文保存: {ex.Message}");
            }
        }
    }

    /// <summary>尽力把密钥文件设成仅属主可读写（Windows 上不支持则跳过，靠目录权限）</summary>
    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* 文件系统不支持就算了 */ }
    }

    public static bool IsEncrypted(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>加密；空值或已是密文时原样返回；未载入密钥时明文直通（不阻断功能）</summary>
    public static string Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsEncrypted(plain)) return plain ?? "";
        byte[] key;
        lock (_lock) { if (_key is null) return plain!; key = _key; }

        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var src = Encoding.UTF8.GetBytes(plain);
            var cipher = new byte[src.Length];
            var tag = new byte[TagBytes];
            using (var gcm = new AesGcm(key, TagBytes))
                gcm.Encrypt(nonce, src, cipher, tag, null);

            var packed = new byte[NonceBytes + TagBytes + cipher.Length];
            Buffer.BlockCopy(nonce, 0, packed, 0, NonceBytes);
            Buffer.BlockCopy(tag, 0, packed, NonceBytes, TagBytes);
            Buffer.BlockCopy(cipher, 0, packed, NonceBytes + TagBytes, cipher.Length);
            return Prefix + Convert.ToBase64String(packed);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[secret] 加密失败，按明文保存: {ex.Message}");
            return plain!;
        }
    }

    /// <summary>
    /// 解密；非密文原样返回。**失败返回空串**（不把密文当明文用），并留一条告警。
    /// </summary>
    public static string Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored ?? "";
        if (!IsEncrypted(stored)) return stored;

        byte[] key;
        lock (_lock) key = _key!;
        if (key is null)
        {
            if (!_warned) { _warned = true; Console.WriteLine("[secret] 收到密文但密钥未载入，无法解密（检查 .secret.key 是否存在）"); }
            return "";
        }

        try
        {
            var packed = Convert.FromBase64String(stored[Prefix.Length..]);
            if (packed.Length < NonceBytes + TagBytes) throw new FormatException("密文长度不足");
            var nonce = packed.AsSpan(0, NonceBytes);
            var tag = packed.AsSpan(NonceBytes, TagBytes);
            var cipher = packed.AsSpan(NonceBytes + TagBytes);
            var plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(key, TagBytes))
                gcm.Decrypt(nonce, cipher, tag, plain, null);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[secret] 解密失败（密钥可能已更换），该值按空处理: {ex.Message}");
            return "";
        }
    }

    /// <summary>给界面遮挡展示用：留尾 4 位（密文则完全遮挡）</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (IsEncrypted(value)) return "••••••••";
        return value.Length <= 4 ? "••••" : "••••" + value[^4..];
    }
}
