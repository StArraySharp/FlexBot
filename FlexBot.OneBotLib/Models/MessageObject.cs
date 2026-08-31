using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class MessageObject
    {
        [JsonPropertyName("self_id")]
        public long SelfId { get; set; }

        [JsonPropertyName("user_id")]
        public long? UserId { get; set; }

        [JsonPropertyName("group_id")]
        public long? GroupId { get; set; }

        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }

        [JsonPropertyName("real_id")]
        public long RealId { get; set; }

        [JsonPropertyName("message_seq")]
        public long MessageSeq { get; set; }

        [JsonPropertyName("time")]
        public long Time { get; set; }

        [JsonPropertyName("message_type")]
        public string MessageType { get; set; } = string.Empty;

        [JsonPropertyName("sub_type")]
        public string SubType { get; set; } = string.Empty;

        [JsonPropertyName("raw_message")]
        public string RawMessage { get; set; } = string.Empty;

        [JsonPropertyName("font")]
        public string Font { get; set; } = string.Empty;

        [JsonPropertyName("message_format")]
        public string MessageFormat { get; set; } = string.Empty;

        [JsonPropertyName("sender")]
        public SenderInfo Sender { get; set; } = new SenderInfo();

        [JsonPropertyName("anonymous")]
        public AnonymousInfo Anonymous { get; set; } = new AnonymousInfo();

        [JsonPropertyName("message")]
        public List<MessageSegmentData> MessageSegments { get; set; } = new();

        [JsonIgnore]
        public string PlainText { get; set; } = string.Empty;

        [JsonIgnore]
        public string GroupName { get; set; } = string.Empty;

        [JsonIgnore]
        public string SenderName => Sender?.DisplayName ?? UserId?.ToString() ?? "未知";

        [JsonIgnore]
        public string SenderNickname => Sender?.Nickname ?? "未知";

        [JsonIgnore]
        public string SenderCard => Sender?.Card ?? "";

        [JsonIgnore]
        public string SenderRole => Sender?.Role ?? "member";

        [JsonIgnore]
        public bool IsGroupMessage => MessageType == "group";

        [JsonIgnore]
        public bool IsPrivateMessage => MessageType == "private";

        [JsonIgnore]
        public bool IsAnonymous => Anonymous?.Id > 0;
    }

    public class MessageSegmentData
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public Dictionary<string, object> Data { get; set; } = new();
    }

    public class SendMessageResult
    {
        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }
    }

    public class ForwardMessageNode
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "node";

        [JsonPropertyName("data")]
        public ForwardNodeData Data { get; set; } = new();
    }

    public class ForwardNodeData
    {
        [JsonPropertyName("uin")]
        public long? Uin { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("content")]
        public List<MessageSegmentData>? Content { get; set; }

        [JsonPropertyName("seq")]
        public long? Seq { get; set; }
    }
}
