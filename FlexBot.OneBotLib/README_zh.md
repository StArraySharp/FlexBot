# OneBotLib

一个完整实现 OneBot 11 协议的 C# 类库，支持通过 WebSocket 与 OneBot 实现进行通信。

[![NuGet](https://img.shields.io/nuget/v/OneBotLib.svg)](https://www.nuget.org/packages/OneBotLib/)
[![GitHub](https://img.shields.io/badge/GitHub-XSY--HYH/OneBotLib-blue)](https://github.com/XSY-HYH/OneBotLib)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## 特性

- 完整实现 OneBot 11 协议
- WebSocket 通信支持
- 事件驱动架构
- 完善的消息段构建器
- 支持 Async/await 模式
- 支持外部连接共享上下文
- 连接状态管理

## 安装

### NuGet

```bash
dotnet add package OneBotLib
```

或者使用包管理器：

```powershell
Install-Package OneBotLib
```

## 快速开始

```csharp
using OneBotLib;
using OneBotLib.Events;
using OneBotLib.Models;
using OneBotLib.MessageSegment;

// 创建客户端实例
var client = new OneBotClient();

// 订阅事件
client.OnMessage += (sender, e) =>
{
    Console.WriteLine($"收到消息: {e.Message.PlainText}");
};

client.OnGroupMessage += (sender, e) =>
{
    var msg = e.Message;
    Console.WriteLine($"群 {msg.GroupId} 的 {msg.Sender.Nickname} 说: {msg.PlainText}");
};

client.OnPrivateMessage += (sender, e) =>
{
    var msg = e.Message;
    Console.WriteLine($"私聊 {msg.UserId} 说: {msg.PlainText}");
};

// 连接到 OneBot 服务
await client.ConnectAsync("ws://127.0.0.1:3001", "your_access_token");

// 或同步连接
bool connected = client.ConnectSync("ws://127.0.0.1:3001", "your_access_token", 5);

// 发送消息（使用 ApiResult 检查结果）
var result = await client.SendPrivateMsgAsync(123456789, "Hello, World!");
if (result.Success)
{
    Console.WriteLine($"消息发送成功，消息ID: {result.Data}");
}
else
{
    Console.WriteLine($"发送失败: {result.ErrorMessage}");
}

// 发送复杂消息
var segments = new List<MessageSegment.MessageSegment>
{
    MessageSegment.MessageSegment.At(123456789),
    MessageSegment.MessageSegment.Text(" 你好！"),
    MessageSegment.MessageSegment.Image("https://example.com/image.png")
};
await client.SendGroupMsgAsync(987654321, segments);

// 关闭连接
await client.CloseAsync();
```

## 连接状态

```csharp
// 订阅连接状态变更事件
client.OnConnectionStateChanged += (sender, e) =>
{
    Console.WriteLine($"连接状态: {e.OldState} -> {e.NewState}");
    if (e.Message != null)
    {
        Console.WriteLine($"消息: {e.Message}");
    }
};

// 状态值：
// ConnectionState.Connecting - 正在连接
// ConnectionState.Connected - 已连接
// ConnectionState.Disconnected - 连接断开
```

## 共享上下文

与外部程序共享已有的 WebSocket 连接：

```csharp
var client = new OneBotClient();

// 定义发送消息的委托
async Task SendMessageAsync(string message)
{
    var bytes = Encoding.UTF8.GetBytes(message);
    await externalWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

// 共享上下文
var sharedContext = client.AttachToExternalConnection(SendMessageAsync);

// 处理接收到的消息
async Task OnWebSocketMessageReceived(string message)
{
    await client.OnExternalMessageReceivedAsync(message);
}

// 断开共享
client.DetachFromExternalConnection();
```

## 事件系统

### 消息事件

```csharp
client.OnMessage += (sender, e) => { };
client.OnPrivateMessage += (sender, e) => { };
client.OnGroupMessage += (sender, e) => { };
```

### 消息发送事件

机器人自己发送的消息（包括从其他设备发送的消息）：

```csharp
client.OnMessageSent += (sender, e) => { };
client.OnPrivateMessageSent += (sender, e) => { };
client.OnGroupMessageSent += (sender, e) => { };
```

### 元事件

```csharp
client.OnLifecycle += (sender, e) => { };
client.OnHeartbeat += (sender, e) => { };
```

### 通知事件

```csharp
client.OnGroupMemberChange += (sender, e) => { };
client.OnGroupAdmin += (sender, e) => { };
client.OnGroupBan += (sender, e) => { };
client.OnGroupUpload += (sender, e) => { };
client.OnGroupRecall += (sender, e) => { };
client.OnFriendAdd += (sender, e) => { };
client.OnFriendRecall += (sender, e) => { };
```

### 请求事件

```csharp
client.OnFriendRequest += async (sender, e) =>
{
    var result = await client.SetFriendAddRequestAsync(e.Flag, true);
};

client.OnGroupRequest += async (sender, e) =>
{
    var result = await client.SetGroupAddRequestAsync(e.Flag, true);
};
```

## 消息段构建器

```csharp
// 文本
MessageSegment.Text("你好")

// @某人
MessageSegment.At(123456789)
MessageSegment.AtAll()

// 图片
MessageSegment.Image("https://example.com/image.png")

// 回复
MessageSegment.Reply(messageId)

// 表情
MessageSegment.Face(123)

// 语音
MessageSegment.Record("https://example.com/audio.mp3")

// 视频
MessageSegment.Video("https://example.com/video.mp4")

// 位置
MessageSegment.Location(39.9042, 116.4074, "北京", "中国首都")

// 音乐
MessageSegment.Music(123456, "qq")

// JSON/XML
MessageSegment.Json("{\"content\": \"...\"}")
MessageSegment.Xml("<xml>...</xml>")

// 小程序/卡片
MessageSegment.MiniApp("data")
MessageSegment.Ark("data")
MessageSegment.Card("data")

// 键盘/按钮
MessageSegment.Keyboard("data")
MessageSegment.KeyboardButton("id", "label")

// 富文本
MessageSegment.RichText("content")
MessageSegment.Link("https://...", "文字")

// 文字转语音
MessageSegment.Tts("要转换的文字")
```

## API 返回结果

所有 API 方法返回 `ApiResult` 或 `ApiResult<T>`：

```csharp
// 有返回数据的 API
var result = await client.GetLoginInfoAsync();
if (result.Success)
{
    Console.WriteLine($"账号: {result.Data.UserId}");
}
else
{
    Console.WriteLine($"错误: {result.ErrorMessage}");
    Console.WriteLine($"堆栈: {result.StackTrace}");
}

// 无返回数据的 API
var result = await client.SetGroupBanAsync(groupId, userId, 600);
if (result.Success)
{
    Console.WriteLine("操作成功");
}
```

## 命名空间

```csharp
using OneBotLib;                    // 主客户端
using OneBotLib.Events;             // 事件参数
using OneBotLib.Models;             // 数据模型
using OneBotLib.MessageSegment;     // 消息段构建器
```

## 许可证

MIT License

## 链接

- [OneBot 11 协议](https://11.onebot.dev/)
- [GitHub 仓库](https://github.com/XSY-HYH/OneBotLib)
