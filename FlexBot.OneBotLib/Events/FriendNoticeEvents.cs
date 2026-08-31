namespace OneBotLib.Events
{
    public class FriendAddEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long UserId { get; set; }
    }

    public class FriendRecallEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long UserId { get; set; }
        public long MessageId { get; set; }
    }

    public class FriendPokeEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long UserId { get; set; }
        public long TargetId { get; set; }
    }

    public class ClientStatusEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public ClientInfo Client { get; set; } = new();
    }

    public class ClientInfo
    {
        public string AppId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string DeviceKind { get; set; } = string.Empty;
        public bool Online { get; set; }
    }
}
