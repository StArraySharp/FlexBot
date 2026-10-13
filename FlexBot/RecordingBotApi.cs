using System.Text.Json;
using FlexBot.PluginApi;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Models;

namespace FlexBot;

/// <summary>
/// IBotApi 记录代理：把插件与 AI 的**出站**发送也记进消息流（WebUI「实时消息流」用），
/// 其余方法原样转发给真实通道。只读转发不改变任何行为，唯一副作用是多一次内存环形缓冲写入。
///
/// 为什么要这层：宿主自己发的东西可以在调用点记录，但插件/AI 是通过 ctx.Api 直接发的，
/// 没有代理就看不到 —— 而"机器人在群里到底说了什么"恰恰是运维最想看的。
/// </summary>
sealed class RecordingBotApi(IBotApi inner) : IBotApi
{
    public long SelfId => inner.SelfId;

    public Task<ApiResult<long>> SendPrivateMsgAsync(long userId, object message)
    {
        Stats.Recent.Add("out", "private", userId, BotHelpers.MsgToText(message));
        Stats.IncOut();
        return inner.SendPrivateMsgAsync(userId, message);
    }

    public Task<ApiResult<long>> SendGroupMsgAsync(long groupId, object message)
    {
        Stats.Recent.Add("out", "group", groupId, BotHelpers.MsgToText(message));
        Stats.IncOut();
        return inner.SendGroupMsgAsync(groupId, message);
    }

    public Task<ApiResult<long>> SendGroupForwardMsgAsync(long groupId, List<ForwardNode> messages)
    {
        Stats.Recent.Add("out", "group", groupId, $"[合并转发 {messages.Count} 条]");
        Stats.IncOut();
        return inner.SendGroupForwardMsgAsync(groupId, messages);
    }

    public Task<ApiResult<long>> SendPrivateForwardMsgAsync(long userId, List<ForwardNode> messages)
    {
        Stats.Recent.Add("out", "private", userId, $"[合并转发 {messages.Count} 条]");
        Stats.IncOut();
        return inner.SendPrivateForwardMsgAsync(userId, messages);
    }

    public Task<ApiResult> DeleteMsgAsync(long messageId) => inner.DeleteMsgAsync(messageId);
    public Task<ApiResult> SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set = true) => inner.SetMsgEmojiLikeAsync(messageId, emojiId, set);
    public Task<ApiResult> SetGroupBanAsync(long groupId, long userId, long duration = 0) => inner.SetGroupBanAsync(groupId, userId, duration);
    public Task<ApiResult> SetGroupWholeBanAsync(long groupId, long duration = 0) => inner.SetGroupWholeBanAsync(groupId, duration);
    public Task<ApiResult> SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest = false) => inner.SetGroupKickAsync(groupId, userId, rejectAddRequest);
    public Task<ApiResult> SetGroupCardAsync(long groupId, long userId, string? card = null) => inner.SetGroupCardAsync(groupId, userId, card);
    public Task<ApiResult> SetGroupSpecialTitleAsync(long groupId, long userId, string? title = null) => inner.SetGroupSpecialTitleAsync(groupId, userId, title);
    public Task<ApiResult> SendGroupNoticeAsync(long groupId, string content, string? image = null) => inner.SendGroupNoticeAsync(groupId, content, image);
    public Task<ApiResult> SetGroupLeaveAsync(long groupId, bool isDismiss = false) => inner.SetGroupLeaveAsync(groupId, isDismiss);
    public Task<ApiResult<GroupInfo>> GetGroupInfoAsync(long groupId) => inner.GetGroupInfoAsync(groupId);
    public Task<ApiResult<List<FriendInfo>>> GetFriendListAsync() => inner.GetFriendListAsync();
    public Task<ApiResult> UploadGroupFileAsync(long groupId, string file, string name, string? folder = null) => inner.UploadGroupFileAsync(groupId, file, name, folder);
    public Task<ApiResult> SetFriendAddRequestAsync(string flag, bool approve = true, string? remark = null) => inner.SetFriendAddRequestAsync(flag, approve, remark);
    public Task<ApiResult> SetGroupAddRequestAsync(string flag, string subType, bool approve = true, string? reason = null) => inner.SetGroupAddRequestAsync(flag, subType, approve, reason);
    public Task<ApiResult<MsgInfo>> GetMsgAsync(long messageId) => inner.GetMsgAsync(messageId);
    public Task<ApiResult<List<MsgInfo>>> GetGroupMsgHistoryAsync(long groupId, long? messageId = null, int count = 20) => inner.GetGroupMsgHistoryAsync(groupId, messageId, count);
    public Task<ApiResult<List<GroupMemberInfo>>> GetGroupMemberListAsync(long groupId) => inner.GetGroupMemberListAsync(groupId);
    public Task<ApiResult> GroupPokeAsync(long groupId, long userId) => inner.GroupPokeAsync(groupId, userId);
    public Task<ApiResult<AccountInfo>> GetLoginInfoAsync() => inner.GetLoginInfoAsync();
    public Task<ApiResult<JsonElement>> CallApiAsync(string action, Dictionary<string, object>? parameters = null) => inner.CallApiAsync(action, parameters);
}
