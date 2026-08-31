using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class GroupNoticeInfo
    {
        [JsonPropertyName("notice_id")]
        public string NoticeId { get; set; } = string.Empty;

        [JsonPropertyName("sender_id")]
        public long SenderId { get; set; }

        [JsonPropertyName("publish_time")]
        public long PublishTime { get; set; }

        [JsonPropertyName("message")]
        public GroupNoticeMessage Message { get; set; } = new();
    }

    public class GroupNoticeMessage
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("images")]
        public List<GroupNoticeImage> Images { get; set; } = new();
    }

    public class GroupNoticeImage
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("width")]
        public string Width { get; set; } = string.Empty;

        [JsonPropertyName("height")]
        public string Height { get; set; } = string.Empty;
    }

    public class GroupHonorInfo
    {
        [JsonPropertyName("group_id")]
        public string GroupId { get; set; } = string.Empty;

        [JsonPropertyName("current_talkative")]
        public GroupHonorUser? CurrentTalkative { get; set; }

        [JsonPropertyName("talkative_list")]
        public List<GroupHonorUser> TalkativeList { get; set; } = new();

        [JsonPropertyName("performer_list")]
        public List<GroupHonorUser> PerformerList { get; set; } = new();

        [JsonPropertyName("legend_list")]
        public List<GroupHonorUser> LegendList { get; set; } = new();

        [JsonPropertyName("emotion_list")]
        public List<GroupHonorUser> EmotionList { get; set; } = new();

        [JsonPropertyName("strong_newbie_list")]
        public List<GroupHonorUser> StrongNewbieList { get; set; } = new();
    }

    public class GroupHonorUser
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("avatar")]
        public string Avatar { get; set; } = string.Empty;

        [JsonPropertyName("day_count")]
        public int DayCount { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class GroupAtAllRemain
    {
        [JsonPropertyName("can_at_all")]
        public bool CanAtAll { get; set; }

        [JsonPropertyName("remain_at_all_count_for_group")]
        public int RemainAtAllCountForGroup { get; set; }

        [JsonPropertyName("remain_at_all_count_for_uin")]
        public int RemainAtAllCountForUin { get; set; }
    }

    public class GroupSystemMsg
    {
        [JsonPropertyName("invited_requests")]
        public List<GroupInvitedRequest> InvitedRequests { get; set; } = new();

        [JsonPropertyName("join_requests")]
        public List<GroupJoinRequest> JoinRequests { get; set; } = new();
    }

    public class GroupInvitedRequest
    {
        [JsonPropertyName("request_id")]
        public long RequestId { get; set; }

        [JsonPropertyName("invitor_uin")]
        public long InvitorUin { get; set; }

        [JsonPropertyName("invitor_nick")]
        public string InvitorNick { get; set; } = string.Empty;

        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("checked")]
        public bool Checked { get; set; }

        [JsonPropertyName("actor")]
        public long Actor { get; set; }
    }

    public class GroupJoinRequest
    {
        [JsonPropertyName("request_id")]
        public long RequestId { get; set; }

        [JsonPropertyName("requester_uin")]
        public long RequesterUin { get; set; }

        [JsonPropertyName("requester_nick")]
        public string RequesterNick { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("checked")]
        public bool Checked { get; set; }

        [JsonPropertyName("actor")]
        public long Actor { get; set; }
    }
}
