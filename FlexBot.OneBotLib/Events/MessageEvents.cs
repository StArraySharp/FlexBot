using OneBotLib.Models;

namespace OneBotLib.Events
{
    public class MessageEventArgs : EventArgs
    {
        public MessageObject Message { get; set; } = new();
    }

    public class PrivateMessageEventArgs : MessageEventArgs
    {
    }

    public class GroupMessageEventArgs : MessageEventArgs
    {
    }

    public class MessageSentEventArgs : EventArgs
    {
        public long MessageId { get; set; }
        public string MessageType { get; set; } = string.Empty;
        public long? UserId { get; set; }
        public long? GroupId { get; set; }
        public object Message { get; set; } = new();
        public long Time { get; set; }
        public bool IsGroupMessage => MessageType == "group";
        public bool IsPrivateMessage => MessageType == "private";
    }

    public class PrivateMessageSentEventArgs : MessageSentEventArgs
    {
    }

    public class GroupMessageSentEventArgs : MessageSentEventArgs
    {
    }
}
