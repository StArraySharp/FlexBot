using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AOnlinePlugin;

/// <summary>
/// ALTCHA 工作量证明（PoW）求解器。
/// 流程：challenge.parameters（PBKDF2/SHA-256, nonce, salt, cost, keyLength, keyPrefix）
///   password = nonce(bytes) + counter(uint32 BE)
///   迭代 cost 次：data[i] = SHA256(data[i-1])[..keyLength]，data[0] = SHA256(salt || password)[..keyLength]
///   直到 derivedKey 前 keyPrefix 长度字节 == keyPrefix bytes
/// payload = Base64( {"parameters":…(原样),"signature":…,"solution":{counter,derivedKey,time}} )
/// </summary>
static class Altcha
{
    public sealed class Challenge
    {
        public string Algorithm = "SHA-256"; // 从 "PBKDF2/SHA-256" 尾段解析
        public string Nonce = "";
        public string Salt = "";
        public int Cost;
        public int KeyLength = 32;
        public string KeyPrefix = "";
    }

    public sealed class Solution
    {
        public uint Counter;
        public string DerivedKey = "";
        public double Time;
    }

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNameCaseInsensitive = true, // 服务器发 camelCase（keyLength/keyPrefix）
    };

    private sealed class ChallengeDto
    {
        public ParametersDto Parameters { get; set; } = new();
        public string Signature { get; set; } = "";
    }

    private sealed class ParametersDto
    {
        public string Algorithm { get; set; } = "SHA-256";
        public string Nonce { get; set; } = "";
        public string Salt { get; set; } = "";
        public int Cost { get; set; } = 1;
        public int KeyLength { get; set; } = 32;
        public string KeyPrefix { get; set; } = "";
    }

    private sealed class PayloadDto
    {
        // 外层键名 challenge（值 = parameters 对象 + signature 平级在内）
        public ParametersDto Challenge { get; set; } = new();
        public string Signature { get; set; } = "";
        public SolutionDto? Solution { get; set; }
    }

    private sealed class SolutionDto
    {
        public uint Counter { get; set; }
        public string DerivedKey { get; set; } = "";
        public double Time { get; set; }
    }

    /// <summary>从 API 响应解析挑战参数；格式不对返回 null。</summary>
    public static Challenge? TryParseChallenge(string responseBody)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ChallengeDto>(responseBody, JsonOpt);
            if (dto is null || dto.Parameters.Nonce.Length == 0) return null;
            // algorithm 形如 "PBKDF2/SHA-256"，取 hashing 算法名
            var algo = dto.Parameters.Algorithm;
            var slash = algo.IndexOf('/');
            if (slash >= 0) algo = algo[(slash + 1)..].Replace("-", "");
            return new Challenge
            {
                Algorithm = algo switch { "SHA512" => "SHA512", "SHA384" => "SHA384", _ => "SHA256" },
                Nonce = dto.Parameters.Nonce,
                Salt = dto.Parameters.Salt,
                Cost = Math.Max(1, dto.Parameters.Cost),
                KeyLength = dto.Parameters.KeyLength > 0 ? dto.Parameters.KeyLength : 32,
                KeyPrefix = dto.Parameters.KeyPrefix,
            };
            // 注意：原 JSON 原样回传 BuildPayload 使用，仅需保证字段与服务器一致（Key Naming 可由反序列化承担）
        }
        catch { return null; }
    }

    /// <summary>解 PoW：暴力递增 counter 直到 derivedKey 匹配 keyPrefix。</summary>
    public static AltchaSolution Solve(Challenge ch)
    {
        var nonce = HexToBytes(ch.Nonce);
        var salt = HexToBytes(ch.Salt);
        var prefix = ch.KeyPrefix.Length % 2 == 0 ? HexToBytes(ch.KeyPrefix) : [];
        var cost = ch.Cost;
        var keyLen = ch.KeyLength;

        var password = new byte[nonce.Length + 4];
        Buffer.BlockCopy(nonce, 0, password, 0, nonce.Length);

        uint counter = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            BinaryPrimitives.WriteUInt32BigEndian(password.AsSpan(nonce.Length), counter);

            // data[0] = SHA256(salt || password)[..keyLen]；再 cost-1 次迭代
            var data = new byte[salt.Length + password.Length];
            Buffer.BlockCopy(salt, 0, data, 0, salt.Length);
            Buffer.BlockCopy(password, 0, data, salt.Length, password.Length);
            byte[] derived;
            var hashName = ch.Algorithm switch { "SHA512" => HashAlgorithmName.SHA512, "SHA384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA256 };
            using (var sha = IncrementalHash.CreateHash(hashName))
            {
                sha.AppendData(data);
                derived = sha.GetHashAndReset()[..keyLen];
                for (var i = 1; i < cost; i++)
                {
                    sha.AppendData(derived);
                    derived = sha.GetHashAndReset()[..keyLen];
                }
            }

            if (StartsWith(derived, prefix))
                return new AltchaSolution
                {
                    Counter = counter,
                    DerivedKey = Convert.ToHexString(derived).ToLowerInvariant(),
                    TimeMs = sw.Elapsed.TotalMilliseconds,
                };

            checked { counter++; } // uint32 溢出理论上不会（cost=15000 时平均 1/256 命中）
        }
    }

    public sealed class AltchaSolution
    {
        public uint Counter;
        public string DerivedKey = "";
        public double TimeMs;
    }

    /// <summary>组装 base64 payload：{challenge: <服务器原样响应>, solution:{counter,derivedKey,time}}。</summary>
    public static string BuildPayload(string challengeResponseJson, AltchaSolution solution)
    {
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            // challenge = 服务器完整原样响应（parameters + signature），签名针对这个整体
            ["challenge"] = System.Text.Json.Nodes.JsonNode.Parse(challengeResponseJson)!.DeepClone(),
            ["solution"] = new System.Text.Json.Nodes.JsonObject
            {
                ["counter"] = solution.Counter,
                ["derivedKey"] = solution.DerivedKey,
                ["time"] = Math.Round(solution.TimeMs, 1),
            },
        };
        var json = payload.ToJsonString();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static byte[] HexToBytes(string hex)
    {
        var b = new byte[hex.Length / 2];
        for (var i = 0; i < b.Length; i++)
            b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (prefix.Length == 0 || data.Length < prefix.Length) return prefix.Length == 0;
        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }
}
