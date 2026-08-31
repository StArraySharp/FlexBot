using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class GroupInfo
    {
        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("group_memo")]
        public string GroupMemo { get; set; } = string.Empty;

        [JsonPropertyName("member_count")]
        public int MemberCount { get; set; }

        [JsonPropertyName("max_member_count")]
        public int MaxMemberCount { get; set; }

        [JsonPropertyName("group_create_time")]
        public long GroupCreateTime { get; set; }

        [JsonPropertyName("group_level")]
        public int GroupLevel { get; set; }

        [JsonPropertyName("is_frozen")]
        public bool IsFrozen { get; set; }

        [JsonPropertyName("remark_name")]
        public string RemarkName { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("owner_id")]
        public long OwnerId { get; set; }

        [JsonPropertyName("is_top")]
        public bool IsTop { get; set; }

        [JsonPropertyName("shut_up_all_timestamp")]
        public long ShutUpAllTimestamp { get; set; }

        [JsonPropertyName("shut_up_me_timestamp")]
        public long ShutUpMeTimestamp { get; set; }
    }

    public class GroupMemberInfo
    {
        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("card")]
        public string Card { get; set; } = string.Empty;

        [JsonPropertyName("card_or_nickname")]
        public string CardOrNickname { get; set; } = string.Empty;

        [JsonPropertyName("level")]
        public string GroupLevel { get; set; } = string.Empty;

        [JsonPropertyName("qq_level")]
        public int QqLevel { get; set; }

        [JsonPropertyName("last_sent_time")]
        public long LastSentTime { get; set; }

        [JsonPropertyName("join_time")]
        public long JoinTime { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("sex")]
        public string Sex { get; set; } = string.Empty;

        [JsonPropertyName("age")]
        public int Age { get; set; }

        [JsonPropertyName("area")]
        public string Area { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("title_expire_time")]
        public long TitleExpireTime { get; set; }

        [JsonPropertyName("unfriendly")]
        public bool Unfriendly { get; set; }

        [JsonPropertyName("card_changeable")]
        public bool CardChangeable { get; set; }

        [JsonPropertyName("is_robot")]
        public bool IsRobot { get; set; }

        [JsonPropertyName("shut_up_timestamp")]
        public long ShutUpTimestamp { get; set; }

        [JsonIgnore]
        public string UserRoleDesc =>
            Role switch
            {
                "owner" => "群主",
                "admin" => "管理员",
                _ => "成员"
            };

        [JsonIgnore]
        public string DisplayName => !string.IsNullOrEmpty(Card) ? Card : Nickname;
    }
}
