using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class VersionInfo
    {
        [JsonPropertyName("app_name")]
        public string AppName { get; set; } = string.Empty;

        [JsonPropertyName("app_version")]
        public string AppVersion { get; set; } = string.Empty;

        [JsonPropertyName("protocol_version")]
        public string ProtocolVersion { get; set; } = string.Empty;
    }

    public class BotStatus
    {
        [JsonPropertyName("online")]
        public bool Online { get; set; }

        [JsonPropertyName("good")]
        public bool Good { get; set; }

        [JsonPropertyName("stat")]
        public BotStat Stat { get; set; } = new();
    }

    public class BotStat
    {
        [JsonPropertyName("message_received")]
        public int MessageReceived { get; set; }

        [JsonPropertyName("message_sent")]
        public int MessageSent { get; set; }

        [JsonPropertyName("last_message_time")]
        public long LastMessageTime { get; set; }

        [JsonPropertyName("startup_time")]
        public long StartupTime { get; set; }
    }

    public class CookiesInfo
    {
        [JsonPropertyName("cookies")]
        public string Cookies { get; set; } = string.Empty;

        [JsonPropertyName("bkn")]
        public string Bkn { get; set; } = string.Empty;
    }

    public class ProfileLikeUser
    {
        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        [JsonPropertyName("uin")]
        public long Uin { get; set; }

        [JsonPropertyName("nick")]
        public string Nick { get; set; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("latestTime")]
        public long LatestTime { get; set; }

        [JsonPropertyName("isFriend")]
        public bool IsFriend { get; set; }

        [JsonPropertyName("isvip")]
        public bool IsVip { get; set; }

        [JsonPropertyName("isSvip")]
        public bool IsSvip { get; set; }

        [JsonPropertyName("gender")]
        public int Gender { get; set; }

        [JsonPropertyName("age")]
        public int Age { get; set; }
    }

    public class ProfileLikeResult
    {
        [JsonPropertyName("users")]
        public List<ProfileLikeUser> Users { get; set; } = new();

        [JsonPropertyName("nextStart")]
        public int NextStart { get; set; }
    }

    public class RobotUinRange
    {
        [JsonPropertyName("minUin")]
        public string MinUin { get; set; } = string.Empty;

        [JsonPropertyName("maxUin")]
        public string MaxUin { get; set; } = string.Empty;
    }

    public class AvatarInfo
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    public class AICharacter
    {
        [JsonPropertyName("character_id")]
        public string CharacterId { get; set; } = string.Empty;

        [JsonPropertyName("character_name")]
        public string CharacterName { get; set; } = string.Empty;

        [JsonPropertyName("preview_url")]
        public string PreviewUrl { get; set; } = string.Empty;
    }

    public class AICharacterType
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("characters")]
        public List<AICharacter> Characters { get; set; } = new();
    }

    public class VoiceToTextResult
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class FlashFileInfo
    {
        [JsonPropertyName("file_set_id")]
        public string FileSetId { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("share_link")]
        public string ShareLink { get; set; } = string.Empty;

        [JsonPropertyName("total_file_size")]
        public string TotalFileSize { get; set; } = string.Empty;

        [JsonPropertyName("expire_time")]
        public long ExpireTime { get; set; }

        [JsonPropertyName("files")]
        public List<FlashFileItem> Files { get; set; } = new();
    }

    public class FlashFileItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
