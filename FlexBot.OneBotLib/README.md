# OneBotLib

A C# library that fully implements the OneBot 11 protocol, supporting WebSocket communication with OneBot implementations.

[![NuGet](https://img.shields.io/nuget/v/OneBotLib.svg)](https://www.nuget.org/packages/OneBotLib/)
[![GitHub](https://img.shields.io/badge/GitHub-XSY--HYH/OneBotLib-blue)](https://github.com/XSY-HYH/OneBotLib)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

## Features

- Full OneBot 11 protocol implementation
- WebSocket communication support
- Event-driven architecture
- Comprehensive message segment builder
- Async/await pattern support
- Shared context for external connections
- Connection state management

## Installation

### NuGet

```bash
dotnet add package OneBotLib
```

Or via Package Manager:

```powershell
Install-Package OneBotLib
```

## Quick Start

```csharp
using OneBotLib;
using OneBotLib.Events;
using OneBotLib.Models;
using OneBotLib.MessageSegment;

// Create client instance
var client = new OneBotClient();

// Subscribe to events
client.OnMessage += (sender, e) =>
{
    Console.WriteLine($"Received message: {e.Message.PlainText}");
};

client.OnGroupMessage += (sender, e) =>
{
    var msg = e.Message;
    Console.WriteLine($"Group {msg.GroupId} - {msg.Sender.Nickname}: {msg.PlainText}");
};

client.OnPrivateMessage += (sender, e) =>
{
    var msg = e.Message;
    Console.WriteLine($"Private {msg.UserId}: {msg.PlainText}");
};

// Connect to OneBot service
await client.ConnectAsync("ws://127.0.0.1:3001", "your_access_token");

// Or synchronous connection
bool connected = client.ConnectSync("ws://127.0.0.1:3001", "your_access_token", 5);

// Send message (check result with ApiResult)
var result = await client.SendPrivateMsgAsync(123456789, "Hello, World!");
if (result.Success)
{
    Console.WriteLine($"Message sent, ID: {result.Data}");
}
else
{
    Console.WriteLine($"Failed: {result.ErrorMessage}");
}

// Send complex message
var segments = new List<MessageSegment.MessageSegment>
{
    MessageSegment.MessageSegment.At(123456789),
    MessageSegment.MessageSegment.Text(" Hello!"),
    MessageSegment.MessageSegment.Image("https://example.com/image.png")
};
await client.SendGroupMsgAsync(987654321, segments);

// Close connection
await client.CloseAsync();
```

## Connection State

```csharp
// Subscribe to connection state changes
client.OnConnectionStateChanged += (sender, e) =>
{
    Console.WriteLine($"State: {e.OldState} -> {e.NewState}");
    if (e.Message != null)
    {
        Console.WriteLine($"Message: {e.Message}");
    }
};

// States:
// ConnectionState.Connecting - Connecting
// ConnectionState.Connected - Connected
// ConnectionState.Disconnected - Disconnected
```

## Shared Context

Share an existing WebSocket connection with OneBotLib:

```csharp
var client = new OneBotClient();

// Define send message delegate
async Task SendMessageAsync(string message)
{
    var bytes = Encoding.UTF8.GetBytes(message);
    await externalWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
}

// Attach to external connection
var sharedContext = client.AttachToExternalConnection(SendMessageAsync);

// Handle incoming messages
async Task OnWebSocketMessageReceived(string message)
{
    await client.OnExternalMessageReceivedAsync(message);
}

// Detach when done
client.DetachFromExternalConnection();
```

## Events

### Message Events

```csharp
client.OnMessage += (sender, e) => { };
client.OnPrivateMessage += (sender, e) => { };
client.OnGroupMessage += (sender, e) => { };
```

### Message Sent Events

Messages sent by the bot (including from other devices):

```csharp
client.OnMessageSent += (sender, e) => { };
client.OnPrivateMessageSent += (sender, e) => { };
client.OnGroupMessageSent += (sender, e) => { };
```

### Meta Events

```csharp
client.OnLifecycle += (sender, e) => { };
client.OnHeartbeat += (sender, e) => { };
```

### Notice Events

```csharp
client.OnGroupMemberChange += (sender, e) => { };
client.OnGroupAdmin += (sender, e) => { };
client.OnGroupBan += (sender, e) => { };
client.OnGroupUpload += (sender, e) => { };
client.OnGroupRecall += (sender, e) => { };
client.OnFriendAdd += (sender, e) => { };
client.OnFriendRecall += (sender, e) => { };
```

### Request Events

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

## Message Segment Builder

```csharp
// Text
MessageSegment.Text("Hello")

// @Mention
MessageSegment.At(123456789)
MessageSegment.AtAll()

// Image
MessageSegment.Image("https://example.com/image.png")

// Reply
MessageSegment.Reply(messageId)

// Face
MessageSegment.Face(123)

// Voice
MessageSegment.Record("https://example.com/audio.mp3")

// Video
MessageSegment.Video("https://example.com/video.mp4")

// Location
MessageSegment.Location(39.9042, 116.4074, "Beijing", "Capital of China")

// Music
MessageSegment.Music(123456, "qq")

// JSON/XML
MessageSegment.Json("{\"content\": \"...\"}")
MessageSegment.Xml("<xml>...</xml>")

// Mini App/Card
MessageSegment.MiniApp("data")
MessageSegment.Ark("data")
MessageSegment.Card("data")

// Keyboard/Button
MessageSegment.Keyboard("data")
MessageSegment.KeyboardButton("id", "label")

// Rich Text
MessageSegment.RichText("content")
MessageSegment.Link("https://...", "text")

// TTS
MessageSegment.Tts("text to speak")
```

## API Result

All API methods return `ApiResult` or `ApiResult<T>`:

```csharp
// With return data
var result = await client.GetLoginInfoAsync();
if (result.Success)
{
    Console.WriteLine($"Account: {result.Data.UserId}");
}
else
{
    Console.WriteLine($"Error: {result.ErrorMessage}");
    Console.WriteLine($"Stack: {result.StackTrace}");
}

// Without return data
var result = await client.SetGroupBanAsync(groupId, userId, 600);
if (result.Success)
{
    Console.WriteLine("Success");
}
```

## Namespaces

```csharp
using OneBotLib;                    // Main client
using OneBotLib.Events;             // Event args
using OneBotLib.Models;             // Data models
using OneBotLib.MessageSegment;     // Message segment builder
```

## License

MIT License

## Links

- [OneBot 11 Protocol](https://11.onebot.dev/)
- [GitHub Repository](https://github.com/XSY-HYH/OneBotLib)
