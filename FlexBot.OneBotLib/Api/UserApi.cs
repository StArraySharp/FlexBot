using System.Text.Json;
using System.Text.Json.Serialization;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public partial class OneBotClient
    {
        public async Task<ApiResult<AccountInfo>> GetLoginInfoAsync()
        {
            var result = await SendApiAsync<AccountInfo>("get_login_info");
            if (result.Success && result.Data != null)
            {
                _currentAccountInfo = result.Data;
            }
            return result;
        }

        public async Task<ApiResult> SendLikeAsync(long userId, int times = 1)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "times", times }
            };
            var result = await SendApiAsync("send_like", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<FriendInfo>>> GetFriendListAsync(bool noCache = false)
        {
            var parameters = new Dictionary<string, object> { { "no_cache", noCache } };
            var result = await SendApiAsync<List<FriendInfo>>("get_friend_list", parameters);
            if (result.Success && result.Data != null)
            {
                _friendListCache = result.Data;
            }
            return result;
        }

        public async Task<ApiResult<List<FriendCategory>>> GetFriendsWithCategoryAsync()
        {
            return await SendApiAsync<List<FriendCategory>>("get_friends_with_category");
        }

        public async Task<ApiResult> DeleteFriendAsync(long userId)
        {
            var parameters = new Dictionary<string, object> { { "user_id", userId } };
            var result = await SendApiAsync("delete_friend", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetFriendAddRequestAsync(string flag, bool approve, string? remark = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "flag", flag },
                { "approve", approve }
            };
            if (!string.IsNullOrEmpty(remark))
            {
                parameters["remark"] = remark;
            }
            var result = await SendApiAsync("set_friend_add_request", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetFriendRemarkAsync(long userId, string remark)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "remark", remark }
            };
            var result = await SendApiAsync("set_friend_remark", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<StrangerInfo>> GetStrangerInfoAsync(long userId)
        {
            var parameters = new Dictionary<string, object> { { "user_id", userId } };
            return await SendApiAsync<StrangerInfo>("get_stranger_info", parameters);
        }

        public async Task<ApiResult> SetQQAvatarAsync(string file)
        {
            var parameters = new Dictionary<string, object> { { "file", file } };
            var result = await SendApiAsync("set_qq_avatar", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> FriendPokeAsync(long userId, long? targetId = null)
        {
            var parameters = new Dictionary<string, object> { { "user_id", userId } };
            if (targetId.HasValue)
            {
                parameters["target_id"] = targetId.Value;
            }
            var result = await SendApiAsync("friend_poke", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<ProfileLikeResult>> GetProfileLikeAsync(int start = 0, int count = 20)
        {
            var parameters = new Dictionary<string, object>
            {
                { "start", start },
                { "count", count }
            };
            return await SendApiAsync<ProfileLikeResult>("get_profile_like", parameters);
        }

        public async Task<ApiResult<ProfileLikeResult>> GetProfileLikeMeAsync(int start = 0, int count = 20)
        {
            var parameters = new Dictionary<string, object>
            {
                { "start", start },
                { "count", count }
            };
            return await SendApiAsync<ProfileLikeResult>("get_profile_like_me", parameters);
        }

        public async Task<ApiResult<List<RobotUinRange>>> GetRobotUinRangeAsync()
        {
            return await SendApiAsync<List<RobotUinRange>>("get_robot_uin_range");
        }

        public async Task<ApiResult> SetFriendCategoryAsync(long userId, int categoryId)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "category_id", categoryId }
            };
            var result = await SendApiAsync("set_friend_category", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<AvatarInfo>> GetQQAvatarAsync(long? userId = null, long? groupId = null)
        {
            var parameters = new Dictionary<string, object>();
            if (userId.HasValue)
            {
                parameters["user_id"] = userId.Value;
            }
            if (groupId.HasValue)
            {
                parameters["group_id"] = groupId.Value;
            }
            return await SendApiAsync<AvatarInfo>("get_qq_avatar", parameters);
        }

        public async Task<ApiResult<List<DoubtFriendRequest>>> GetDoubtFriendsAddRequestAsync(int count = 50)
        {
            var parameters = new Dictionary<string, object> { { "count", count } };
            return await SendApiAsync<List<DoubtFriendRequest>>("get_doubt_friends_add_request", parameters);
        }

        public async Task<ApiResult> SetDoubtFriendsAddRequestAsync(string flag)
        {
            var parameters = new Dictionary<string, object> { { "flag", flag } };
            var result = await SendApiAsync("set_doubt_friends_add_request", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetQQProfileAsync(string? nickname = null, string? personalNote = null)
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
            var result = await SendApiAsync("set_qq_profile", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }
    }

    public class DoubtFriendRequest
    {
        [JsonPropertyName("flag")]
        public string Flag { get; set; } = string.Empty;

        [JsonPropertyName("uin")]
        public string Uin { get; set; } = string.Empty;

        [JsonPropertyName("nick")]
        public string Nick { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;

        [JsonPropertyName("msg")]
        public string Msg { get; set; } = string.Empty;

        [JsonPropertyName("group_code")]
        public string GroupCode { get; set; } = string.Empty;

        [JsonPropertyName("time")]
        public string Time { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }
}
