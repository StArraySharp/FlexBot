using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class FriendInfo
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("remark")]
        public string Remark { get; set; } = string.Empty;

        [JsonPropertyName("sex")]
        public string Sex { get; set; } = string.Empty;

        [JsonPropertyName("age")]
        public int Age { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("qid")]
        public string Qid { get; set; } = string.Empty;

        [JsonPropertyName("long_nick")]
        public string LongNick { get; set; } = string.Empty;

        [JsonPropertyName("birthday_year")]
        public int BirthdayYear { get; set; }

        [JsonPropertyName("birthday_month")]
        public int BirthdayMonth { get; set; }

        [JsonPropertyName("birthday_day")]
        public int BirthdayDay { get; set; }
    }

    public class FriendCategory
    {
        [JsonPropertyName("categoryId")]
        public int CategoryId { get; set; }

        [JsonPropertyName("categorySortId")]
        public int CategorySortId { get; set; }

        [JsonPropertyName("categoryName")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("categoryMbCount")]
        public int CategoryMbCount { get; set; }

        [JsonPropertyName("onlineCount")]
        public int OnlineCount { get; set; }

        [JsonPropertyName("buddyList")]
        public List<FriendInfo> BuddyList { get; set; } = new();
    }
}
