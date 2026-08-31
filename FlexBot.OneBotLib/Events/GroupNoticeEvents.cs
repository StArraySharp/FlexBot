namespace OneBotLib.Events
{
    public class GroupMemberChangeEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public long OperatorId { get; set; }
        public string SubType { get; set; } = string.Empty;
    }

    public class GroupAdminEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string SubType { get; set; } = string.Empty;
    }

    public class GroupBanEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public long OperatorId { get; set; }
        public long Duration { get; set; }
        public string SubType { get; set; } = string.Empty;
    }

    public class GroupUploadEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public UploadFileInfo File { get; set; } = new();
    }

    public class UploadFileInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
        public long Busid { get; set; }
    }

    public class GroupRecallEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public long OperatorId { get; set; }
        public long MessageId { get; set; }
    }

    public class GroupPokeEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public long TargetId { get; set; }
    }

    public class GroupLuckyKingEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public long TargetId { get; set; }
    }

    public class GroupHonorEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string HonorType { get; set; } = string.Empty;
    }

    public class GroupCardChangeEventArgs : EventArgs
    {
        public long Time { get; set; }
        public long SelfId { get; set; }
        public string PostType { get; set; } = string.Empty;
        public string NoticeType { get; set; } = string.Empty;
        public long GroupId { get; set; }
        public long UserId { get; set; }
        public string CardNew { get; set; } = string.Empty;
        public string CardOld { get; set; } = string.Empty;
    }
}
