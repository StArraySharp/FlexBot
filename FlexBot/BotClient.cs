using System.Text.Json;
using FlexBot.PluginApi;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Models;
using OneBotClient = OneBotLib.Api.OneBotClient;

namespace FlexBot;

// 继承 OneBotLib 暴露通用 API 调用，同时实现插件可见的 IBotApi 接口
// （基类方法带 autoEscape 可选参数，不能隐式满足接口签名，故显式实现）
class BotClient : OneBotClient, IBotApi
{
    public long SelfId => CurrentAccountInfo?.UserId ?? 0;

    Task<ApiResult<long>> IBotApi.SendPrivateMsgAsync(long userId, object message)
    {
        Console.WriteLine($"[private→] {userId}: {MsgBrief(message)}");
        return SendPrivateMsgAsync(userId, message);
    }

    Task<ApiResult<long>> IBotApi.SendGroupMsgAsync(long groupId, object message)
    {
        Console.WriteLine($"[group→] {groupId}: {MsgBrief(message)}");
        return SendGroupMsgAsync(groupId, message);
    }

    // 消息摘要（CQ 码压缩 + 截断；日志与回复共用）
    internal static string MsgBrief(object message)
    {
        var s = message?.ToString() ?? "";
        // MessageSegment 列表的 ToString 未必可读——反射取常见形态前先简单处理
        s = s.Replace("\n", " ⏎ ");
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    Task<ApiResult> IBotApi.DeleteMsgAsync(long messageId) => DeleteMsgAsync(messageId);
    Task<ApiResult> IBotApi.SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set) =>
        SetMsgEmojiLikeAsync(messageId, emojiId, set);

    Task<ApiResult> IBotApi.SetGroupBanAsync(long groupId, long userId, long duration) =>
        SetGroupBanAsync(groupId, userId, duration);
    Task<ApiResult> IBotApi.SetGroupWholeBanAsync(long groupId, long duration) =>
        SetGroupWholeBanAsync(groupId, duration > 0);
    Task<ApiResult> IBotApi.SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest) =>
        SetGroupKickAsync(groupId, userId, rejectAddRequest);
    Task<ApiResult> IBotApi.SetGroupCardAsync(long groupId, long userId, string? card) =>
        SetGroupCardAsync(groupId, userId, card);
    Task<ApiResult> IBotApi.SetGroupSpecialTitleAsync(long groupId, long userId, string? title) =>
        SetGroupSpecialTitleAsync(groupId, userId, title);
    Task<ApiResult> IBotApi.SendGroupNoticeAsync(long groupId, string content, string? image) =>
        SendGroupNoticeAsync(groupId, content, image);
    Task<ApiResult> IBotApi.SetGroupLeaveAsync(long groupId, bool isDismiss) =>
        SetGroupLeaveAsync(groupId, isDismiss);
    Task<ApiResult<GroupInfo>> IBotApi.GetGroupInfoAsync(long groupId) =>
        GetGroupInfoAsync(groupId);
    Task<ApiResult<List<FriendInfo>>> IBotApi.GetFriendListAsync() =>
        GetFriendListAsync();
    Task<ApiResult> IBotApi.UploadGroupFileAsync(long groupId, string file, string name, string? folder) =>
        UploadGroupFileAsync(groupId, file, name, folder);
    Task<ApiResult> IBotApi.SetFriendAddRequestAsync(string flag, bool approve, string? remark) =>
        SetFriendAddRequestAsync(flag, approve, remark);
    Task<ApiResult> IBotApi.SetGroupAddRequestAsync(string flag, string subType, bool approve, string? reason) =>
        SetGroupAddRequestAsync(flag, approve, reason);

    Task<ApiResult<MsgInfo>> IBotApi.GetMsgAsync(long messageId) =>
        GetMsgAsync(messageId);

    Task<ApiResult<List<MsgInfo>>> IBotApi.GetGroupMsgHistoryAsync(long groupId, long? messageId, int count) =>
        GetGroupMsgHistoryAsync(groupId, messageId, count);

    Task<ApiResult<List<GroupMemberInfo>>> IBotApi.GetGroupMemberListAsync(long groupId) =>
        GetGroupMemberListAsync(groupId);

    Task<ApiResult> IBotApi.GroupPokeAsync(long groupId, long userId) =>
        GroupPokeAsync(groupId, userId);

    Task<ApiResult<AccountInfo>> IBotApi.GetLoginInfoAsync() =>
        GetLoginInfoAsync();

    public Task<ApiResult<JsonElement>> CallApiAsync(string action, Dictionary<string, object>? parameters = null)
        => SendApiAsync(action, parameters);
}
