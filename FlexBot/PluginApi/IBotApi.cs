using System.Text.Json;
using OneBotLib;
using OneBotLib.Api;
using OneBotLib.Models;

namespace FlexBot.PluginApi;

/// <summary>
/// 宿主暴露给插件的 OneBot API 子集（由宿主 BotClient 实现）。
/// 只声明插件实际用到的方法，隔离插件与 OneBotClient 具体实现。
/// </summary>
public interface IBotApi
{
    /// <summary>机器人自身 QQ 号（未登录时为 0）</summary>
    long SelfId { get; }
    Task<ApiResult<long>> SendPrivateMsgAsync(long userId, object message);

    Task<ApiResult<long>> SendGroupMsgAsync(long groupId, object message);

    /// <summary>撤回消息（需要机器人有管理权限或消息是自己发的）</summary>
    Task<ApiResult> DeleteMsgAsync(long messageId);

    /// <summary>给消息加表情回应（emoji_id 见 OneBot 规范，如 "76" 👍）</summary>
    Task<ApiResult> SetMsgEmojiLikeAsync(long messageId, string emojiId, bool set = true);

    /// <summary>禁言（duration 秒；0 = 解除；admin=true 时针对管理员）</summary>
    Task<ApiResult> SetGroupBanAsync(long groupId, long userId, long duration = 0);

    /// <summary>全员禁言（duration 秒；0 = 解除）</summary>
    Task<ApiResult> SetGroupWholeBanAsync(long groupId, long duration = 0);

    /// <summary>踢人（rejectAddRequest=true 时同时拒绝其再次入群申请）</summary>
    Task<ApiResult> SetGroupKickAsync(long groupId, long userId, bool rejectAddRequest = false);

    /// <summary>设置群名片（card 传 null 清除）</summary>
    Task<ApiResult> SetGroupCardAsync(long groupId, long userId, string? card = null);

    /// <summary>设置群头衔</summary>
    Task<ApiResult> SetGroupSpecialTitleAsync(long groupId, long userId, string? title = null);

    /// <summary>发群公告</summary>
    Task<ApiResult> SendGroupNoticeAsync(long groupId, string content, string? image = null);

    /// <summary>退群（isDismiss=true 解散群，仅群主）</summary>
    Task<ApiResult> SetGroupLeaveAsync(long groupId, bool isDismiss = false);

    /// <summary>获取群信息</summary>
    Task<ApiResult<GroupInfo>> GetGroupInfoAsync(long groupId);

    /// <summary>获取好友列表</summary>
    Task<ApiResult<List<FriendInfo>>> GetFriendListAsync();

    /// <summary>上传群文件（file 本地路径）</summary>
    Task<ApiResult> UploadGroupFileAsync(long groupId, string file, string name, string? folder = null);

    /// <summary>处理好友请求（flag 来自 FriendRequestEventArgs.Flag）</summary>
    Task<ApiResult> SetFriendAddRequestAsync(string flag, bool approve = true, string? remark = null);

    /// <summary>处理加群请求/邀请（flag 来自 GroupRequestEventArgs.Flag）</summary>
    Task<ApiResult> SetGroupAddRequestAsync(string flag, string subType, bool approve = true, string? reason = null);

    Task<ApiResult<MsgInfo>> GetMsgAsync(long messageId);

    Task<ApiResult<List<MsgInfo>>> GetGroupMsgHistoryAsync(long groupId, long? messageId = null, int count = 20);

    Task<ApiResult<List<GroupMemberInfo>>> GetGroupMemberListAsync(long groupId);

    Task<ApiResult> GroupPokeAsync(long groupId, long userId);

    Task<ApiResult<AccountInfo>> GetLoginInfoAsync();

    /// <summary>透传任意 OneBot API 动作</summary>
    Task<ApiResult<JsonElement>> CallApiAsync(string action, Dictionary<string, object>? parameters = null);
}
