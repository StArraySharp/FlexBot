using System.Text.Json;
using System.Text.Json.Serialization;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public partial class OneBotClient
    {
        public async Task<ApiResult<long>> SendPrivateMsgAsync(long userId, object message, bool autoEscape = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "message", message },
                { "auto_escape", autoEscape }
            };
            var result = await SendApiAsync("send_private_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<long>> SendGroupMsgAsync(long groupId, object message, bool autoEscape = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "message", message },
                { "auto_escape", autoEscape }
            };
            var result = await SendApiAsync("send_group_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<long>> SendMsgAsync(string messageType, long targetId, object message, bool autoEscape = false)
        {
            var parameters = new Dictionary<string, object>
            {
                { "message_type", messageType },
                { "message", message },
                { "auto_escape", autoEscape }
            };
            if (messageType == "private")
            {
                parameters["user_id"] = targetId;
            }
            else
            {
                parameters["group_id"] = targetId;
            }
            var result = await SendApiAsync("send_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult> DeleteMsgAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            var result = await SendApiAsync("delete_msg", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<MsgInfo>> GetMsgAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            return await SendApiAsync<MsgInfo>("get_msg", parameters);
        }

        public async Task<ApiResult<long>> GetForwardMsgAsync(string id)
        {
            var parameters = new Dictionary<string, object> { { "id", id } };
            var result = await SendApiAsync("get_forward_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var msg) ? msg.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult> MarkMsgAsReadAsync(long messageId)
        {
            var parameters = new Dictionary<string, object> { { "message_id", messageId } };
            var result = await SendApiAsync("mark_msg_as_read", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set = true)
        {
            var parameters = new Dictionary<string, object>
            {
                { "message_id", messageId },
                { "emoji_id", emojiId },
                { "set", set }
            };
            var result = await SendApiAsync("set_msg_emoji_like", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<long>> SendPrivateForwardMsgAsync(long userId, List<ForwardNode> messages)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "messages", messages }
            };
            var result = await SendApiAsync("send_private_forward_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<long>> SendGroupForwardMsgAsync(long groupId, List<ForwardNode> messages)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "messages", messages }
            };
            var result = await SendApiAsync("send_group_forward_msg", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<List<ForwardMessageNode>>> GetForwardMsgNodesAsync(string id)
        {
            var parameters = new Dictionary<string, object> { { "id", id } };
            return await SendApiAsync<List<ForwardMessageNode>>("get_forward_msg_nodes", parameters);
        }

        public async Task<ApiResult<long>> SendPrivateFileAsync(long userId, string file, string name, int? timeout = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "file", file },
                { "name", name }
            };
            if (timeout.HasValue)
            {
                parameters["timeout"] = timeout.Value;
            }
            var result = await SendApiAsync("send_private_file", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<long>> SendGroupFileAsync(long groupId, string file, string name, int? timeout = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "file", file },
                { "name", name }
            };
            if (timeout.HasValue)
            {
                parameters["timeout"] = timeout.Value;
            }
            var result = await SendApiAsync("send_group_file", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult> SetGroupInputStatusAsync(long groupId, int status)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "status", status }
            };
            var result = await SendApiAsync("set_group_input_status", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> SetPrivateInputStatusAsync(long userId, int status)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "status", status }
            };
            var result = await SendApiAsync("set_private_input_status", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<List<MsgInfo>>> GetGroupMsgHistoryAsync(long groupId, long? messageId = null, int count = 20)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "count", count }
            };
            if (messageId.HasValue)
            {
                parameters["message_id"] = messageId.Value;
            }
            var result = await SendApiAsync("get_group_msg_history", parameters);
            return ToMsgListResult(result);
        }

        public async Task<ApiResult<List<MsgInfo>>> GetPrivateMsgHistoryAsync(long userId, long? messageId = null, int count = 20)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "count", count }
            };
            if (messageId.HasValue)
            {
                parameters["message_id"] = messageId.Value;
            }
            var result = await SendApiAsync("get_private_msg_history", parameters);
            return ToMsgListResult(result);
        }

        // get_*_msg_history 的 data 兼容两种形态：数组（直接列表）或对象 {messages:[...]}（NapCat/LLOneBot 实现）
        private static ApiResult<List<MsgInfo>> ToMsgListResult(ApiResult<System.Text.Json.JsonElement> result)
        {
            if (!result.Success)
                return ApiResult<List<MsgInfo>>.Fail(result.ErrorMessage!, result.StackTrace);
            try
            {
                if (result.Data.ValueKind == System.Text.Json.JsonValueKind.Null
                    || result.Data.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                    return ApiResult<List<MsgInfo>>.Ok([]);
                var el = result.Data.ValueKind == System.Text.Json.JsonValueKind.Object
                         && result.Data.TryGetProperty("messages", out var arr)
                    ? arr
                    : result.Data;
                var list = JsonSerializer.Deserialize<List<MsgInfo>>(el.GetRawText()) ?? [];
                return ApiResult<List<MsgInfo>>.Ok(list);
            }
            catch (Exception ex)
            {
                return ApiResult<List<MsgInfo>>.Fail(ex);
            }
        }

        public async Task<ApiResult<List<MsgInfo>>> GetRecentMsgAsync(int count = 20)
        {
            var parameters = new Dictionary<string, object> { { "count", count } };
            return await SendApiAsync<List<MsgInfo>>("get_recent_msg", parameters);
        }

        public async Task<ApiResult<List<MsgInfo>>> GetRoamingMsgAsync(long peerUin, long lastMsgId, long lastMsgTime, int count = 20)
        {
            var parameters = new Dictionary<string, object>
            {
                { "peer_uin", peerUin },
                { "last_msg_id", lastMsgId },
                { "last_msg_time", lastMsgTime },
                { "count", count }
            };
            return await SendApiAsync<List<MsgInfo>>("get_roaming_msg", parameters);
        }

        public async Task<ApiResult<List<MsgInfo>>> GetMsgListAsync(int count = 20)
        {
            var parameters = new Dictionary<string, object> { { "count", count } };
            return await SendApiAsync<List<MsgInfo>>("get_msg_list", parameters);
        }

        public async Task<ApiResult<long>> SendGroupAiRecordAsync(long groupId, string text, string character)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "text", text },
                { "character", character }
            };
            var result = await SendApiAsync("send_group_ai_record", parameters);
            if (!result.Success)
            {
                return ApiResult<long>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var msgId = result.Data.TryGetProperty("message_id", out var id) ? id.GetInt64() : 0;
            return ApiResult<long>.Ok(msgId);
        }

        public async Task<ApiResult<List<AiCharacter>>> GetAiCharactersAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<List<AiCharacter>>("get_ai_characters", parameters);
        }

        public async Task<ApiResult> SendGroupAiEmojiAsync(long groupId, long userId, int code)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "user_id", userId },
                { "code", code }
            };
            var result = await SendApiAsync("send_group_ai_emoji", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<string>> GetGroupAiRecordAsync(long groupId, string text, string character)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "text", text },
                { "character", character }
            };
            var result = await SendApiAsync("get_group_ai_record", parameters);
            if (!result.Success)
            {
                return ApiResult<string>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var file = result.Data.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            return ApiResult<string>.Ok(file);
        }
    }

    public class MsgInfo
    {
        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }

        [JsonPropertyName("real_id")]
        public long RealId { get; set; }

        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("time")]
        public long Time { get; set; }

        [JsonPropertyName("message")]
        public object Message { get; set; } = new();

        [JsonPropertyName("message_type")]
        public string MessageType { get; set; } = string.Empty;

        [JsonPropertyName("sender")]
        public SenderInfo Sender { get; set; } = new();

        [JsonPropertyName("group_id")]
        public long? GroupId { get; set; }

        [JsonPropertyName("peer_id")]
        public long? PeerId { get; set; }

        [JsonPropertyName("target_id")]
        public long? TargetId { get; set; }
    }

    public class ForwardNode
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public object Content { get; set; } = new();
    }

    public class ForwardMessageNode
    {
        [JsonPropertyName("user_id")]
        public long UserId { get; set; }

        [JsonPropertyName("nickname")]
        public string Nickname { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public object Content { get; set; } = new();

        [JsonPropertyName("time")]
        public long Time { get; set; }
    }

    public class AiCharacter
    {
        [JsonPropertyName("character_id")]
        public string CharacterId { get; set; } = string.Empty;

        [JsonPropertyName("character_name")]
        public string CharacterName { get; set; } = string.Empty;

        [JsonPropertyName("preview_url")]
        public string PreviewUrl { get; set; } = string.Empty;
    }
}
