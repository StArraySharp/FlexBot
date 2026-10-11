using System.Text.Json;
using FlexBot.PluginApi;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Models;
using OneBotClient = OneBotLib.Api.OneBotClient;

namespace FlexBot;

// 继承 OneBotLib 暴露通用 API 调用，同时实现插件可见的 IBotApi 与宿主侧传输接口 IBotTransport
// （基类方法带 autoEscape 可选参数，不能隐式满足接口签名，故显式实现）
class BotClient : OneBotClient, IBotApi, IBotTransport
{
    public long SelfId => CurrentAccountInfo?.UserId ?? 0;

    // ---- IBotTransport：OneBot 通道 ----
    public string TransportName => "onebot";
    public Task DisconnectAsync() => CloseAsync();

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

    // 消息摘要（日志与回复共用）：字符串原样；消息段序列渲染成 CQ 风格短码
    // （text 段直出，其余 [type:关键值]，如 [at:123][reply:4]）
    internal static string MsgBrief(object message)
    {
        var s = message is IEnumerable<OneBotLib.MessageSegment.MessageSegment> segs
            ? RenderSegments(segs)
            : message?.ToString() ?? "";
        s = s.Replace("\n", " ⏎ ");
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    private static string RenderSegments(IEnumerable<OneBotLib.MessageSegment.MessageSegment> segs)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segs)
        {
            if (seg.Type == "text")
            {
                if (seg.Data.TryGetValue("text", out var t)) sb.Append(t?.ToString());
                continue;
            }
            // 非文本段：只有一个字段就带上值（[at:123]），多字段只留类型（[json]）
            var one = seg.Data.Count == 1 ? seg.Data.Values.First()?.ToString() : null;
            sb.Append(one is null ? $"[{seg.Type}]" : $"[{seg.Type}:{one}]");
        }
        return sb.ToString();
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
