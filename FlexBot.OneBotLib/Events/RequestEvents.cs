namespace OneBotLib.Events
{
    public class FriendRequestEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public long UserId { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string Flag { get; set; } = string.Empty;
    }

    public class GroupRequestEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public string SubType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string Flag { get; set; } = string.Empty;
    }
}
