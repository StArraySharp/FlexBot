namespace OneBotLib.Events
{
    public class LifecycleEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string MetaEventType { get; set; } = string.Empty;
        public string SubType { get; set; } = string.Empty;
    }

    public class HeartbeatEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string MetaEventType { get; set; } = string.Empty;
        public int Interval { get; set; }
    }
}
