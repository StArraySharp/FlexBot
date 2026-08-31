using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OneBotLib.Events;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public enum ConnectionState
    {
        Connecting,
        Connected,
        Disconnected
    }

    public class ConnectionStateChangedEventArgs : EventArgs
    {
        public ConnectionState OldState { get; set; }
        public ConnectionState NewState { get; set; }
        public string? Message { get; set; }
    }

    public delegate Task ExternalSendMessageDelegate(string message);

    public class SharedContext
    {
        public ExternalSendMessageDelegate? SendMessageAsync { get; set; }
        public Action<string>? OnMessageReceived { get; set; }
        public bool IsExternalConnection { get; set; }
    }

    public partial class OneBotClient : IAsyncDisposable
    {
        private ClientWebSocket? _webSocket;
        private ConnectionState _connectionState = ConnectionState.Disconnected;
        private string _wsUrl = string.Empty;
        private string _token = string.Empty;
        private int _seq = 0;
        private readonly Dictionary<string, TaskCompletionSource<ApiResponse>> _echoResponses = new();
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveTask;
        private bool _running = false;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _echoLock = new(1, 1);
        private bool _isExternalConnection = false;
        private ExternalSendMessageDelegate? _externalSendMessage;

        private Dictionary<long, string> _groupNameCache = new();
        private AccountInfo? _currentAccountInfo;
        private List<GroupInfo>? _groupListCache;
        private List<FriendInfo>? _friendListCache;

        public event EventHandler<MessageEventArgs>? OnMessage;
        public event EventHandler<PrivateMessageEventArgs>? OnPrivateMessage;
        public event EventHandler<GroupMessageEventArgs>? OnGroupMessage;
        public event EventHandler<LifecycleEventArgs>? OnLifecycle;
        public event EventHandler<HeartbeatEventArgs>? OnHeartbeat;
        public event EventHandler<GroupMemberChangeEventArgs>? OnGroupMemberChange;
        public event EventHandler<GroupAdminEventArgs>? OnGroupAdmin;
        public event EventHandler<GroupBanEventArgs>? OnGroupBan;
        public event EventHandler<GroupUploadEventArgs>? OnGroupUpload;
        public event EventHandler<GroupRecallEventArgs>? OnGroupRecall;
        public event EventHandler<GroupPokeEventArgs>? OnGroupPoke;
        public event EventHandler<GroupLuckyKingEventArgs>? OnGroupLuckyKing;
        public event EventHandler<GroupHonorEventArgs>? OnGroupHonor;
        public event EventHandler<GroupCardChangeEventArgs>? OnGroupCardChange;
        public event EventHandler<FriendAddEventArgs>? OnFriendAdd;
        public event EventHandler<FriendRecallEventArgs>? OnFriendRecall;
        public event EventHandler<FriendPokeEventArgs>? OnFriendPoke;
        public event EventHandler<ClientStatusEventArgs>? OnClientStatus;
        public event EventHandler<FriendRequestEventArgs>? OnFriendRequest;
        public event EventHandler<GroupRequestEventArgs>? OnGroupRequest;
        public event EventHandler<ConnectionStateChangedEventArgs>? OnConnectionStateChanged;
        public event EventHandler<MessageSentEventArgs>? OnMessageSent;
        public event EventHandler<PrivateMessageSentEventArgs>? OnPrivateMessageSent;
        public event EventHandler<GroupMessageSentEventArgs>? OnGroupMessageSent;

        public ConnectionState ConnectionState => _connectionState;
        public bool IsConnected => _connectionState == ConnectionState.Connected;
        public bool IsExternalConnection => _isExternalConnection;
        public AccountInfo? CurrentAccountInfo => _currentAccountInfo;
        public List<GroupInfo>? GroupList => _groupListCache;
        public List<FriendInfo>? FriendList => _friendListCache;

        private void SetConnectionState(ConnectionState newState, string? message = null)
        {
            if (_connectionState != newState)
            {
                var oldState = _connectionState;
                _connectionState = newState;
                OnConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs
                {
                    OldState = oldState,
                    NewState = newState,
                    Message = message
                });
            }
        }

        public bool ConnectSync(string wsUrl, string token, int timeoutSeconds = 5)
        {
            _wsUrl = wsUrl;
            _token = token;

            var connectTask = ConnectAsync(wsUrl, token);
            connectTask.Wait(timeoutSeconds * 1000);

            for (int i = 0; i < timeoutSeconds; i++)
            {
                if (_connectionState == ConnectionState.Connected)
                {
                    return true;
                }
                Thread.Sleep(1000);
            }

            return false;
        }

        public async Task ConnectAsync(string wsUrl, string token)
        {
            _wsUrl = wsUrl;
            _token = token;
            await ConnectInternalAsync();
        }

        public SharedContext AttachToExternalConnection(ExternalSendMessageDelegate sendMessage)
        {
            _isExternalConnection = true;
            _externalSendMessage = sendMessage;
            _running = true;
            SetConnectionState(ConnectionState.Connected, "Attached to external connection");

            return new SharedContext
            {
                SendMessageAsync = sendMessage,
                OnMessageReceived = OnExternalMessageReceived,
                IsExternalConnection = true
            };
        }

        public void OnExternalMessageReceived(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("echo", out var echoElement))
                {
                    string echo = echoElement.GetString() ?? string.Empty;
                    var response = JsonSerializer.Deserialize<ApiResponse>(message);
                    if (response != null)
                    {
                        _echoLock.Wait();
                        try
                        {
                            if (_echoResponses.TryGetValue(echo, out var tcs))
                            {
                                tcs.SetResult(response);
                                _echoResponses.Remove(echo);
                            }
                        }
                        finally
                        {
                            _echoLock.Release();
                        }
                    }
                }
                else
                {
                    try
                    {
                        ProcessIncomingEvent(root).Wait();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public async Task OnExternalMessageReceivedAsync(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                if (root.TryGetProperty("echo", out var echoElement))
                {
                    string echo = echoElement.GetString() ?? string.Empty;
                    var response = JsonSerializer.Deserialize<ApiResponse>(message);
                    if (response != null)
                    {
                        await _echoLock.WaitAsync();
                        try
                        {
                            if (_echoResponses.TryGetValue(echo, out var tcs))
                            {
                                tcs.SetResult(response);
                                _echoResponses.Remove(echo);
                            }
                        }
                        finally
                        {
                            _echoLock.Release();
                        }
                    }
                }
                else
                {
                    try
                    {
                        await ProcessIncomingEvent(root);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void DetachFromExternalConnection()
        {
            _isExternalConnection = false;
            _externalSendMessage = null;
            _running = false;
            SetConnectionState(ConnectionState.Disconnected, "Detached from external connection");
        }

        private async Task ConnectInternalAsync()
        {
            SetConnectionState(ConnectionState.Connecting, $"Connecting to {_wsUrl}");
            
            string wsUrlWithToken = $"{_wsUrl}?access_token={_token}";
            _webSocket = new ClientWebSocket();
            _receiveCts = new CancellationTokenSource();

            try
            {
                await _webSocket.ConnectAsync(new Uri(wsUrlWithToken), CancellationToken.None);
                _running = true;
                SetConnectionState(ConnectionState.Connected, "Connected successfully");

                _receiveTask = Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                SetConnectionState(ConnectionState.Disconnected, $"Connection failed: {ex.Message}");
                throw;
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[65536];
            var messageBuilder = new StringBuilder();

            while (_running && _webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                try
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _receiveCts?.Token ?? CancellationToken.None);
                        string chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);
                    }
                    while (!result.EndOfMessage);

                    string message = messageBuilder.ToString();

                    using var doc = JsonDocument.Parse(message);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("echo", out var echoElement))
                    {
                        string echo = echoElement.GetString() ?? string.Empty;
                        var response = JsonSerializer.Deserialize<ApiResponse>(message);
                        if (response != null)
                        {
                            await _echoLock.WaitAsync();
                            try
                            {
                                if (_echoResponses.TryGetValue(echo, out var tcs))
                                {
                                    tcs.SetResult(response);
                                    _echoResponses.Remove(echo);
                                }
                            }
                            finally
                            {
                                _echoLock.Release();
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            await ProcessIncomingEvent(root);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                catch (WebSocketException)
                {
                    SetConnectionState(ConnectionState.Disconnected, "WebSocket error");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (JsonException)
                {
                }
                catch (Exception)
                {
                }
            }

            SetConnectionState(ConnectionState.Disconnected, "Receive loop ended");
        }

        private async Task ProcessIncomingEvent(JsonElement root)
        {
            if (root.TryGetProperty("post_type", out var postType))
            {
                string postTypeStr = postType.GetString() ?? string.Empty;

                switch (postTypeStr)
                {
                    case "meta_event":
                        await ProcessMetaEvent(root);
                        break;
                    case "message":
                        await ProcessMessage(root);
                        break;
                    case "message_sent":
                        await ProcessMessageSent(root);
                        break;
                    case "notice":
                        await ProcessNoticeEvent(root);
                        break;
                    case "request":
                        await ProcessRequestEvent(root);
                        break;
                }
            }
        }

        private async Task ProcessMetaEvent(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("meta_event_type", out var metaEventType))
                {
                    string metaEventTypeStr = metaEventType.GetString() ?? string.Empty;

                    switch (metaEventTypeStr)
                    {
                        case "lifecycle":
                            {
                                var args = new LifecycleEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "meta_event",
                                    MetaEventType = metaEventTypeStr,
                                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : ""
                                };
                                OnLifecycle?.Invoke(this, args);
                                break;
                            }
                        case "heartbeat":
                            {
                                var args = new HeartbeatEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "meta_event",
                                    MetaEventType = "heartbeat",
                                    Interval = root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 0
                                };
                                OnHeartbeat?.Invoke(this, args);
                                break;
                            }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessMessage(JsonElement root)
        {
            try
            {
                var msg = new MessageObject
                {
                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : null,
                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : null,
                    MessageId = root.TryGetProperty("message_id", out var messageId) ? messageId.GetInt64() : 0,
                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                    MessageType = root.TryGetProperty("message_type", out var messageType) ? messageType.GetString() ?? "" : "",
                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : "",
                    RawMessage = root.TryGetProperty("raw_message", out var rawMessage) ? rawMessage.GetString() ?? "" : "",
                    Sender = new SenderInfo()
                };

                if (root.TryGetProperty("font", out var fontElement))
                {
                    msg.Font = fontElement.ValueKind switch
                    {
                        JsonValueKind.String => fontElement.GetString() ?? "",
                        JsonValueKind.Number => fontElement.GetInt32().ToString(),
                        _ => ""
                    };
                }

                if (root.TryGetProperty("sender", out var senderElement))
                {
                    msg.Sender.UserId = senderElement.TryGetProperty("user_id", out var senderUserId) ? senderUserId.GetInt64() : 0;
                    msg.Sender.Nickname = senderElement.TryGetProperty("nickname", out var nickname) ? nickname.GetString() ?? "" : "";
                    msg.Sender.Card = senderElement.TryGetProperty("card", out var card) ? card.GetString() ?? "" : "";
                    msg.Sender.Sex = senderElement.TryGetProperty("sex", out var sex) ? sex.GetString() ?? "" : "";
                    msg.Sender.Age = senderElement.TryGetProperty("age", out var age) ? age.GetInt32() : 0;
                    msg.Sender.Area = senderElement.TryGetProperty("area", out var area) ? area.GetString() ?? "" : "";
                    msg.Sender.Level = senderElement.TryGetProperty("level", out var level) ? level.GetString() ?? "" : "";
                    msg.Sender.Role = senderElement.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "";
                    msg.Sender.Title = senderElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";

                    if (msg.UserId == null || msg.UserId == 0)
                    {
                        msg.UserId = msg.Sender.UserId;
                    }
                }

                if (root.TryGetProperty("anonymous", out var anonymousElement))
                {
                    msg.Anonymous.Id = anonymousElement.TryGetProperty("id", out var anonymousId) ? anonymousId.GetInt64() : 0;
                    msg.Anonymous.Name = anonymousElement.TryGetProperty("name", out var anonymousName) ? anonymousName.GetString() ?? "" : "";
                    msg.Anonymous.Flag = anonymousElement.TryGetProperty("flag", out var flag) ? flag.GetString() ?? "" : "";
                }

                msg.PlainText = msg.RawMessage;

                if (root.TryGetProperty("message", out var messageElement))
                {
                    if (messageElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var segment in messageElement.EnumerateArray())
                        {
                            var seg = new MessageSegmentData();
                            seg.Type = segment.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";

                            if (segment.TryGetProperty("data", out var dataElement))
                            {
                                var data = new Dictionary<string, object>();
                                foreach (var prop in dataElement.EnumerateObject())
                                {
                                    data[prop.Name] = prop.Value.GetRawText();
                                }
                                seg.Data = data;
                            }
                            msg.MessageSegments.Add(seg);
                        }
                    }
                }

                if (msg.GroupId.HasValue && msg.GroupId.Value > 0)
                {
                    if (_groupNameCache.TryGetValue(msg.GroupId.Value, out var groupName))
                    {
                        msg.GroupName = groupName;
                    }
                }

                var eventArgs = new MessageEventArgs { Message = msg };
                OnMessage?.Invoke(this, eventArgs);

                if (msg.IsPrivateMessage)
                {
                    OnPrivateMessage?.Invoke(this, new PrivateMessageEventArgs { Message = msg });
                }
                else if (msg.IsGroupMessage)
                {
                    OnGroupMessage?.Invoke(this, new GroupMessageEventArgs { Message = msg });
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessMessageSent(JsonElement root)
        {
            try
            {
                var msg = new MessageObject
                {
                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : null,
                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : null,
                    MessageId = root.TryGetProperty("message_id", out var messageId) ? messageId.GetInt64() : 0,
                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                    MessageType = root.TryGetProperty("message_type", out var messageType) ? messageType.GetString() ?? "" : "",
                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : "",
                    RawMessage = root.TryGetProperty("raw_message", out var rawMessage) ? rawMessage.GetString() ?? "" : "",
                    Sender = new SenderInfo()
                };

                if (root.TryGetProperty("font", out var fontElement))
                {
                    msg.Font = fontElement.ValueKind switch
                    {
                        JsonValueKind.String => fontElement.GetString() ?? "",
                        JsonValueKind.Number => fontElement.GetInt32().ToString(),
                        _ => ""
                    };
                }

                if (root.TryGetProperty("sender", out var senderElement))
                {
                    msg.Sender.UserId = senderElement.TryGetProperty("user_id", out var senderUserId) ? senderUserId.GetInt64() : 0;
                    msg.Sender.Nickname = senderElement.TryGetProperty("nickname", out var nickname) ? nickname.GetString() ?? "" : "";
                    msg.Sender.Card = senderElement.TryGetProperty("card", out var card) ? card.GetString() ?? "" : "";
                    msg.Sender.Sex = senderElement.TryGetProperty("sex", out var sex) ? sex.GetString() ?? "" : "";
                    msg.Sender.Age = senderElement.TryGetProperty("age", out var age) ? age.GetInt32() : 0;
                    msg.Sender.Area = senderElement.TryGetProperty("area", out var area) ? area.GetString() ?? "" : "";
                    msg.Sender.Level = senderElement.TryGetProperty("level", out var level) ? level.GetString() ?? "" : "";
                    msg.Sender.Role = senderElement.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "";
                    msg.Sender.Title = senderElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";

                    if (msg.UserId == null || msg.UserId == 0)
                    {
                        msg.UserId = msg.Sender.UserId;
                    }
                }

                msg.PlainText = msg.RawMessage;

                if (root.TryGetProperty("message", out var messageElement))
                {
                    if (messageElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var segment in messageElement.EnumerateArray())
                        {
                            var seg = new MessageSegmentData();
                            seg.Type = segment.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "" : "";

                            if (segment.TryGetProperty("data", out var dataElement))
                            {
                                var data = new Dictionary<string, object>();
                                foreach (var prop in dataElement.EnumerateObject())
                                {
                                    data[prop.Name] = prop.Value.GetRawText();
                                }
                                seg.Data = data;
                            }
                            msg.MessageSegments.Add(seg);
                        }
                    }
                }

                if (msg.GroupId.HasValue && msg.GroupId.Value > 0)
                {
                    if (_groupNameCache.TryGetValue(msg.GroupId.Value, out var groupName))
                    {
                        msg.GroupName = groupName;
                    }
                }

                var eventArgs = new MessageSentEventArgs
                {
                    MessageId = msg.MessageId,
                    MessageType = msg.MessageType,
                    Message = msg,
                    UserId = msg.UserId,
                    GroupId = msg.GroupId,
                    Time = msg.Time
                };
                OnMessageSent?.Invoke(this, eventArgs);

                if (msg.IsPrivateMessage)
                {
                    OnPrivateMessageSent?.Invoke(this, new PrivateMessageSentEventArgs
                    {
                        MessageId = msg.MessageId,
                        MessageType = msg.MessageType,
                        Message = msg,
                        UserId = msg.UserId,
                        Time = msg.Time
                    });
                }
                else if (msg.IsGroupMessage)
                {
                    OnGroupMessageSent?.Invoke(this, new GroupMessageSentEventArgs
                    {
                        MessageId = msg.MessageId,
                        MessageType = msg.MessageType,
                        Message = msg,
                        GroupId = msg.GroupId,
                        Time = msg.Time
                    });
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessNoticeEvent(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("notice_type", out var noticeType))
                {
                    string noticeTypeStr = noticeType.GetString() ?? string.Empty;

                    switch (noticeTypeStr)
                    {
                        case "group_increase":
                        case "group_decrease":
                            {
                                var args = new GroupMemberChangeEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    OperatorId = root.TryGetProperty("operator_id", out var operatorId) ? operatorId.GetInt64() : 0,
                                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : ""
                                };
                                OnGroupMemberChange?.Invoke(this, args);
                                break;
                            }
                        case "group_admin":
                            {
                                var args = new GroupAdminEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : ""
                                };
                                OnGroupAdmin?.Invoke(this, args);
                                break;
                            }
                        case "group_ban":
                            {
                                var args = new GroupBanEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    OperatorId = root.TryGetProperty("operator_id", out var operatorId) ? operatorId.GetInt64() : 0,
                                    Duration = root.TryGetProperty("duration", out var duration) ? duration.GetInt64() : 0,
                                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : ""
                                };
                                OnGroupBan?.Invoke(this, args);
                                break;
                            }
                        case "group_upload":
                            {
                                var args = new GroupUploadEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0
                                };
                                if (root.TryGetProperty("file", out var fileElement))
                                {
                                    args.File = new UploadFileInfo
                                    {
                                        Id = fileElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                                        Name = fileElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                                        Size = fileElement.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                                        Busid = fileElement.TryGetProperty("busid", out var busid) ? busid.GetInt64() : 0
                                    };
                                }
                                OnGroupUpload?.Invoke(this, args);
                                break;
                            }
                        case "group_recall":
                            {
                                var args = new GroupRecallEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    OperatorId = root.TryGetProperty("operator_id", out var operatorId) ? operatorId.GetInt64() : 0,
                                    MessageId = root.TryGetProperty("message_id", out var messageId) ? messageId.GetInt64() : 0
                                };
                                OnGroupRecall?.Invoke(this, args);
                                break;
                            }
                        case "notify":
                            {
                                var subType = root.TryGetProperty("sub_type", out var subTypeEl) ? subTypeEl.GetString() ?? "" : "";
                                switch (subType)
                                {
                                    case "poke":
                                        {
                                            var args = new GroupPokeEventArgs
                                            {
                                                Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                                SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                                PostType = "notice",
                                                NoticeType = noticeTypeStr,
                                                GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                                UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                                TargetId = root.TryGetProperty("target_id", out var targetId) ? targetId.GetInt64() : 0
                                            };
                                            OnGroupPoke?.Invoke(this, args);
                                            break;
                                        }
                                    case "lucky_king":
                                        {
                                            var args = new GroupLuckyKingEventArgs
                                            {
                                                Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                                SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                                PostType = "notice",
                                                NoticeType = noticeTypeStr,
                                                GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                                UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                                TargetId = root.TryGetProperty("target_id", out var targetId) ? targetId.GetInt64() : 0
                                            };
                                            OnGroupLuckyKing?.Invoke(this, args);
                                            break;
                                        }
                                    case "honor":
                                        {
                                            var args = new GroupHonorEventArgs
                                            {
                                                Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                                SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                                PostType = "notice",
                                                NoticeType = noticeTypeStr,
                                                GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                                UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                                HonorType = root.TryGetProperty("honor_type", out var honorType) ? honorType.GetString() ?? "" : ""
                                            };
                                            OnGroupHonor?.Invoke(this, args);
                                            break;
                                        }
                                }
                                break;
                            }
                        case "group_card":
                            {
                                var args = new GroupCardChangeEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    CardNew = root.TryGetProperty("card_new", out var cardNew) ? cardNew.GetString() ?? "" : "",
                                    CardOld = root.TryGetProperty("card_old", out var cardOld) ? cardOld.GetString() ?? "" : ""
                                };
                                OnGroupCardChange?.Invoke(this, args);
                                break;
                            }
                        case "friend_add":
                            {
                                var args = new FriendAddEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0
                                };
                                OnFriendAdd?.Invoke(this, args);
                                break;
                            }
                        case "friend_recall":
                            {
                                var args = new FriendRecallEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    MessageId = root.TryGetProperty("message_id", out var messageId) ? messageId.GetInt64() : 0
                                };
                                OnFriendRecall?.Invoke(this, args);
                                break;
                            }
                        case "client_status":
                            {
                                var args = new ClientStatusEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "notice",
                                    NoticeType = noticeTypeStr
                                };
                                if (root.TryGetProperty("client", out var clientElement))
                                {
                                    args.Client = new ClientInfo
                                    {
                                        AppId = clientElement.TryGetProperty("app_id", out var appId) ? appId.GetString() ?? "" : "",
                                        DeviceName = clientElement.TryGetProperty("device_name", out var deviceName) ? deviceName.GetString() ?? "" : "",
                                        DeviceKind = clientElement.TryGetProperty("device_kind", out var deviceKind) ? deviceKind.GetString() ?? "" : "",
                                        Online = clientElement.TryGetProperty("online", out var online) && online.GetBoolean()
                                    };
                                }
                                OnClientStatus?.Invoke(this, args);
                                break;
                            }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private async Task ProcessRequestEvent(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("request_type", out var requestType))
                {
                    string requestTypeStr = requestType.GetString() ?? string.Empty;

                    switch (requestTypeStr)
                    {
                        case "friend":
                            {
                                var args = new FriendRequestEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "request",
                                    RequestType = requestTypeStr,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    Comment = root.TryGetProperty("comment", out var comment) ? comment.GetString() ?? "" : "",
                                    Flag = root.TryGetProperty("flag", out var flag) ? flag.GetString() ?? "" : ""
                                };
                                OnFriendRequest?.Invoke(this, args);
                                break;
                            }
                        case "group":
                            {
                                var args = new GroupRequestEventArgs
                                {
                                    Time = root.TryGetProperty("time", out var time) ? time.GetInt64() : 0,
                                    SelfId = root.TryGetProperty("self_id", out var selfId) ? selfId.GetInt64() : 0,
                                    PostType = "request",
                                    RequestType = requestTypeStr,
                                    SubType = root.TryGetProperty("sub_type", out var subType) ? subType.GetString() ?? "" : "",
                                    GroupId = root.TryGetProperty("group_id", out var groupId) ? groupId.GetInt64() : 0,
                                    UserId = root.TryGetProperty("user_id", out var userId) ? userId.GetInt64() : 0,
                                    Comment = root.TryGetProperty("comment", out var comment) ? comment.GetString() ?? "" : "",
                                    Flag = root.TryGetProperty("flag", out var flag) ? flag.GetString() ?? "" : ""
                                };
                                OnGroupRequest?.Invoke(this, args);
                                break;
                            }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private int NextSeq() => Interlocked.Increment(ref _seq);

        protected async Task<ApiResult<JsonElement>> SendApiAsync(string action, Dictionary<string, object>? parameters = null, string? echo = null)
        {
            try
            {
                if (_connectionState != ConnectionState.Connected)
                {
                    return ApiResult<JsonElement>.Fail("Not connected or connection closed");
                }

                if (!_isExternalConnection && (_webSocket == null || _webSocket.State != WebSocketState.Open))
                {
                    return ApiResult<JsonElement>.Fail("Not connected or connection closed");
                }

                echo ??= $"echo_{NextSeq()}";
                var payload = new WebSocketPayload
                {
                    Action = action,
                    Params = parameters,
                    Echo = echo
                };

                var tcs = new TaskCompletionSource<ApiResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

                await _echoLock.WaitAsync();
                try
                {
                    _echoResponses[echo] = tcs;
                }
                finally
                {
                    _echoLock.Release();
                }

                try
                {
                    string json = JsonSerializer.Serialize(payload);

                    if (_isExternalConnection && _externalSendMessage != null)
                    {
                        await _sendLock.WaitAsync();
                        try
                        {
                            await _externalSendMessage(json);
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }
                    else
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(json);

                        await _sendLock.WaitAsync();
                        try
                        {
                            await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }

                    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(30000));
                    if (completedTask != tcs.Task)
                    {
                        return ApiResult<JsonElement>.Fail($"API {action} timeout");
                    }

                    var response = await tcs.Task;

                    if (response.Status != "ok")
                    {
                        return ApiResult<JsonElement>.Fail($"API error: {response.Message} ({response.Wording})");
                    }

                    return ApiResult<JsonElement>.Ok(response.Data);
                }
                catch (Exception ex)
                {
                    await _echoLock.WaitAsync();
                    try
                    {
                        _echoResponses.Remove(echo);
                    }
                    finally
                    {
                        _echoLock.Release();
                    }
                    return ApiResult<JsonElement>.Fail(ex);
                }
            }
            catch (Exception ex)
            {
                return ApiResult<JsonElement>.Fail(ex);
            }
        }

        protected async Task<ApiResult<T>> SendApiAsync<T>(string action, Dictionary<string, object>? parameters = null, string? echo = null)
        {
            var result = await SendApiAsync(action, parameters, echo);
            if (!result.Success)
            {
                return ApiResult<T>.Fail(result.ErrorMessage!, result.StackTrace);
            }

            try
            {
                if (result.Data.ValueKind == JsonValueKind.Null || result.Data.ValueKind == JsonValueKind.Undefined)
                {
                    return ApiResult<T>.Ok(default!);
                }
                var data = JsonSerializer.Deserialize<T>(result.Data.GetRawText());
                return ApiResult<T>.Ok(data!);
            }
            catch (Exception ex)
            {
                return ApiResult<T>.Fail(ex);
            }
        }

        public async Task CloseAsync()
        {
            _running = false;

            if (_isExternalConnection)
            {
                _isExternalConnection = false;
                _externalSendMessage = null;
                SetConnectionState(ConnectionState.Disconnected, "Closed external connection");
                return;
            }

            SetConnectionState(ConnectionState.Disconnected, "Connection closed");

            _receiveCts?.Cancel();

            if (_receiveTask != null)
            {
                try
                {
                    await _receiveTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
                catch
                {
                }
                _webSocket.Dispose();
                _webSocket = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
            _receiveCts?.Dispose();
            _sendLock.Dispose();
            _echoLock.Dispose();
        }
    }
}
