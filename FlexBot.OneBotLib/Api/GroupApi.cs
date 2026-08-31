using System.Text.Json;
using System.Text.Json.Serialization;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public partial class OneBotClient
    {
        public async Task<ApiResult<List<GroupInfo>>> GetGroupListAsync(bool noCache = false)
        {
            var parameters = new Dictionary<string, object> { { "no_cache", noCache } };
            var result = await SendApiAsync<List<GroupInfo>>("get_group_list", parameters);
            if (result.Success && result.Data != null)
            {
                _groupListCache = result.Data;
                foreach (var group in result.Data)
                {
                    _groupNameCache[group.GroupId] = group.GroupName;
                }
            }
            return result;
        }

        public async Task<ApiResult<GroupInfo>> GetGroupInfoAsync(long groupId, bool noCache = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "no_cache", noCache }
            };
            var result = await SendApiAsync<GroupInfo>("get_group_info", parameters);
            if (result.Success && result.Data != null)
            {
                _groupNameCache[groupId] = result.Data.GroupName;
            }
            return result;
        }

        public async Task<ApiResult<List<GroupMemberInfo>>> GetGroupMemberListAsync(long groupId, bool noCache = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "no_cache", noCache }
            };
            return await SendApiAsync<List<GroupMemberInfo>>("get_group_member_list", parameters);
        }

        public async Task<ApiResult<GroupMemberInfo>> GetGroupMemberInfoAsync(long groupId, long userId, bool noCache = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "no_cache", noCache }
            };
            return await SendApiAsync<GroupMemberInfo>("get_group_member_info", parameters);
        }

        public async Task<ApiResult> GroupPokeAsync(long groupId, long userId)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId }
            };
            var result = await SendApiAsync("group_poke", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<GroupSystemMsg>> GetGroupSystemMsgAsync()
        {
            return await SendApiAsync<GroupSystemMsg>("get_group_system_msg");
        }

        public async Task<ApiResult> SetGroupAddRequestAsync(string flag, bool approve = true, string? reason = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "flag", flag },
                { "approve", approve }
            };
            if (!string.IsNullOrEmpty(reason))
            {
                parameters["reason"] = reason;
            }
            var result = await SendApiAsync("set_group_add_request", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupWholeBanAsync(long groupId, bool enable = true)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "enable", enable }
            };
            var result = await SendApiAsync("set_group_whole_ban", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<GroupShutUpMember>>> GetGroupShutListAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<List<GroupShutUpMember>>("get_group_shut_list", parameters);
        }

        public async Task<ApiResult> SetGroupNameAsync(long groupId, string groupName)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "group_name", groupName }
            };
            var result = await SendApiAsync("set_group_name", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> BatchDeleteGroupMemberAsync(long groupId, List<long> userIds)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_ids", userIds }
            };
            var result = await SendApiAsync("batch_delete_group_member", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "reject_add_request", rejectAddRequest }
            };
            var result = await SendApiAsync("set_group_kick", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupBanAsync(long groupId, long userId, long duration = 0)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "duration", duration }
            };
            var result = await SendApiAsync("set_group_ban", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupAdminAsync(long groupId, long userId, bool enable = true)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "enable", enable }
            };
            var result = await SendApiAsync("set_group_admin", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupCardAsync(long groupId, long userId, string? card = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "card", card ?? "" }
            };
            var result = await SendApiAsync("set_group_card", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupLeaveAsync(long groupId, bool isDismiss = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "is_dismiss", isDismiss }
            };
            var result = await SendApiAsync("set_group_leave", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupSpecialTitleAsync(long groupId, long userId, string? specialTitle = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "special_title", specialTitle ?? "" }
            };
            var result = await SendApiAsync("set_group_special_title", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<GroupHonorInfo>> GetGroupHonorInfoAsync(long groupId, string type = "all")
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "type", type }
            };
            return await SendApiAsync<GroupHonorInfo>("get_group_honor_info", parameters);
        }

        public async Task<ApiResult> SetEssenceMsgAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            var result = await SendApiAsync("set_essence_msg", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> DeleteEssenceMsgAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            var result = await SendApiAsync("delete_essence_msg", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<GroupAtAllRemain>> GetGroupAtAllRemainAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<GroupAtAllRemain>("get_group_at_all_remain", parameters);
        }

        public async Task<ApiResult> SendGroupNoticeAsync(long groupId, string content, string? image = null, bool pinned = false, bool confirmRequired = true)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "content", content },
                { "pinned", pinned },
                { "confirm_required", confirmRequired }
            };
            if (!string.IsNullOrEmpty(image))
            {
                parameters["image"] = image;
            }
            var result = await SendApiAsync("_send_group_notice", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<GroupNoticeInfo>>> GetGroupNoticeAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<List<GroupNoticeInfo>>("_get_group_notice", parameters);
        }

        public async Task<ApiResult> DeleteGroupNoticeAsync(long groupId, string noticeId)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "notice_id", noticeId }
            };
            var result = await SendApiAsync("_delete_group_notice", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SendGroupSignAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            var result = await SendApiAsync("send_group_sign", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupMsgMaskAsync(long groupId, int mask)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "mask", mask }
            };
            var result = await SendApiAsync("set_group_msg_mask", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetGroupRemarkAsync(long groupId, string? remark = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "remark", remark ?? "" }
            };
            var result = await SendApiAsync("set_group_remark", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<object>> GetGroupIgnoreAddRequestAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<object>("get_group_ignore_add_request", parameters);
        }
    }

    public class GroupShutUpMember
    {
        [JsonPropertyName("uin")]
        public string Uin { get; set; } = string.Empty;

        [JsonPropertyName("nick")]
        public string Nick { get; set; } = string.Empty;

        [JsonPropertyName("cardName")]
        public string CardName { get; set; } = string.Empty;

        [JsonPropertyName("shutUpTime")]
        public long ShutUpTime { get; set; }

        [JsonPropertyName("role")]
        public int Role { get; set; }
    }
}
