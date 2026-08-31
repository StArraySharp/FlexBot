using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class AccountInfo
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("sign")]
        public string Sign { get; set; } = string.Empty;

        [JsonPropertyName("sex")]
        public string Sex { get; set; } = string.Empty;

        [JsonPropertyName("age")]
        public int Age { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("login_days")]
        public int LoginDays { get; set; }

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
}
