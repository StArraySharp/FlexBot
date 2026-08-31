using System.Text.Json;
using System.Text.Json.Serialization;
using OneBotLib.Events;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public partial class OneBotClient
    {
        public async Task<ApiResult<VersionInfo>> GetVersionInfoAsync()
        {
            return await SendApiAsync<VersionInfo>("get_version_info");
        }

        public async Task<ApiResult<BotStatus>> GetStatusAsync()
        {
            return await SendApiAsync<BotStatus>("get_status");
        }

        public async Task<ApiResult<bool>> CanSendImageAsync()
        {
            var result = await SendApiAsync("can_send_image");
            if (!result.Success)
            {
                return ApiResult<bool>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var canSend = result.Data.TryGetProperty("yes", out var yes) && yes.GetBoolean();
            return ApiResult<bool>.Ok(canSend);
        }

        public async Task<ApiResult<bool>> CanSendRecordAsync()
        {
            var result = await SendApiAsync("can_send_record");
            if (!result.Success)
            {
                return ApiResult<bool>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var canSend = result.Data.TryGetProperty("yes", out var yes) && yes.GetBoolean();
            return ApiResult<bool>.Ok(canSend);
        }

        public async Task<ApiResult> SetRestartAsync(int delay = 0)
        {
            var parameters = new Dictionary<string, object> { { "delay", delay } };
            var result = await SendApiAsync("set_restart", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> CleanCacheAsync()
        {
            var result = await SendApiAsync("clean_cache");
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> ReloadEventFilterAsync()
        {
            var result = await SendApiAsync("reload_event_filter");
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> DownloadFileAsync(string url, string? threadCount = null, string? headers = null)
        {
            var parameters = new Dictionary<string, object> { { "url", url } };
            if (!string.IsNullOrEmpty(threadCount))
            {
                parameters["thread_count"] = threadCount;
            }
            if (!string.IsNullOrEmpty(headers))
            {
                parameters["headers"] = headers;
            }
            var result = await SendApiAsync("download_file", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> CheckUrlSafelyAsync(string url)
        {
            var parameters = new Dictionary<string, object> { { "url", url } };
            var result = await SendApiAsync("check_url_safely", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<ClientInfo>>> GetOnlineClientsAsync()
        {
            return await SendApiAsync<List<ClientInfo>>("get_online_clients");
        }

        public async Task<ApiResult<OcrResult>> OcrImageAsync(string image)
        {
            var parameters = new Dictionary<string, object> { { "image", image } };
            return await SendApiAsync<OcrResult>("ocr_image", parameters);
        }

        public async Task<ApiResult<GroupOcrResult>> OcrImageAsync(long groupId, string image)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "image", image }
            };
            return await SendApiAsync<GroupOcrResult>("ocr_image", parameters);
        }

        public async Task<ApiResult<WordSlicesResult>> GetWordSlicesAsync(string content)
        {
            var parameters = new Dictionary<string, object> { { "content", content } };
            return await SendApiAsync<WordSlicesResult>("get_word_slices", parameters);
        }

        public async Task<ApiResult> SetAccountProfileAsync(string? nickname = null, string? personalNote = null, string? sex = null)
        {
            var parameters = new Dictionary<string, object>();
            if (!string.IsNullOrEmpty(nickname))
            {
                parameters["nickname"] = nickname;
            }
            if (!string.IsNullOrEmpty(personalNote))
            {
                parameters["personal_note"] = personalNote;
            }
            if (!string.IsNullOrEmpty(sex))
            {
                parameters["sex"] = sex;
            }
            var result = await SendApiAsync("set_account_profile", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<UnidirectionalFriendInfo>>> GetUnidirectionalFriendListAsync()
        {
            return await SendApiAsync<List<UnidirectionalFriendInfo>>("get_unidirectional_friend_list");
        }

        public async Task<ApiResult> DeleteUnidirectionalFriendAsync(long userId)
        {
            var parameters = new Dictionary<string, object> { { "user_id", userId } };
            var result = await SendApiAsync("delete_unidirectional_friend", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<JsonElement>> GetModelShowAsync(string model)
        {
            var parameters = new Dictionary<string, object> { { "model", model } };
            return await SendApiAsync<JsonElement>("get_model_show", parameters);
        }

        public async Task<ApiResult> SetModelShowAsync(string model)
        {
            var parameters = new Dictionary<string, object> { { "model", model } };
            var result = await SendApiAsync("set_model_show", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<EmojiLikeInfo>>> GetMsgEmojiLikeListAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            return await SendApiAsync<List<EmojiLikeInfo>>("get_msg_emoji_like_list", parameters);
        }

        public async Task<ApiResult> SetSelfProfileCardAsync(string cardName)
        {
            var parameters = new Dictionary<string, object> { { "card_name", cardName } };
            var result = await SendApiAsync("set_self_profile_card", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetSelfLongNickAsync(string longNick)
        {
            var parameters = new Dictionary<string, object> { { "long_nick", longNick } };
            var result = await SendApiAsync("set_self_long_nick", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetSelfSignAsync(string sign)
        {
            var parameters = new Dictionary<string, object> { { "sign", sign } };
            var result = await SendApiAsync("set_self_sign", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetSelfBirthdayAsync(int year, int month, int day)
        {
            var parameters = new Dictionary<string, object>
            {
                { "year", year },
                { "month", month },
                { "day", day }
            };
            var result = await SendApiAsync("set_self_birthday", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetSelfLocationAsync(double lat, double lon)
        {
            var parameters = new Dictionary<string, object>
            {
                { "lat", lat },
                { "lon", lon }
            };
            var result = await SendApiAsync("set_self_location", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }
    }

    public class OcrResult
    {
        [JsonPropertyName("texts")]
        public List<OcrText> Texts { get; set; } = new();

        [JsonPropertyName("language")]
        public string Language { get; set; } = string.Empty;
    }

    public class OcrText
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public int Confidence { get; set; }

        [JsonPropertyName("coordinates")]
        public List<OcrCoordinate> Coordinates { get; set; } = new();
    }

    public class OcrCoordinate
    {
        [JsonPropertyName("x")]
        public int X { get; set; }

        [JsonPropertyName("y")]
        public int Y { get; set; }
    }

    public class GroupOcrResult : OcrResult
    {
    }

    public class WordSlicesResult
    {
        [JsonPropertyName("slices")]
        public List<string> Slices { get; set; } = new();
    }

    public class UnidirectionalFriendInfo
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    public class EmojiLikeInfo
    {
        [JsonPropertyName("emoji_id")]
        public string EmojiId { get; set; } = string.Empty;

        [JsonPropertyName("emoji_index")]
        public int EmojiIndex { get; set; }

        [JsonPropertyName("emoji_type")]
        public int EmojiType { get; set; }

        [JsonPropertyName("emoji_name")]
        public string EmojiName { get; set; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("is_clicked")]
        public bool IsClicked { get; set; }
    }
}
