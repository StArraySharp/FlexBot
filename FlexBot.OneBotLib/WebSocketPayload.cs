using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneBotLib
{
    public class WebSocketPayload
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("params")]
        public Dictionary<string, object>? Params { get; set; }

        [JsonPropertyName("echo")]
        public string Echo { get; set; } = string.Empty;
    }

    public class ApiResponse
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("retcode")]
        public int Retcode { get; set; }

        [JsonPropertyName("data")]
        public JsonElement Data { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("wording")]
        public string Wording { get; set; } = string.Empty;

        [JsonPropertyName("echo")]
        public string? Echo { get; set; }
    }
}
