using System.Text.Json.Serialization;

namespace OneBotLib.Models
{
    public class GroupFileInfo
    {
        [JsonPropertyName("file_id")]
        public string FileId { get; set; } = string.Empty;

        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("busid")]
        public int BusId { get; set; }

        [JsonPropertyName("file_size")]
        public long FileSize { get; set; }

        [JsonPropertyName("upload_time")]
        public long UploadTime { get; set; }

        [JsonPropertyName("dead_time")]
        public long DeadTime { get; set; }

        [JsonPropertyName("modify_time")]
        public long ModifyTime { get; set; }

        [JsonPropertyName("download_times")]
        public int DownloadTimes { get; set; }

        [JsonPropertyName("uploader")]
        public long Uploader { get; set; }

        [JsonPropertyName("uploader_name")]
        public string UploaderName { get; set; } = string.Empty;
    }

    public class GroupFolderInfo
    {
        [JsonPropertyName("folder_id")]
        public string FolderId { get; set; } = string.Empty;

        [JsonPropertyName("folder_name")]
        public string FolderName { get; set; } = string.Empty;

        [JsonPropertyName("create_time")]
        public long CreateTime { get; set; }

        [JsonPropertyName("creator")]
        public long Creator { get; set; }

        [JsonPropertyName("creator_name")]
        public string CreatorName { get; set; } = string.Empty;

        [JsonPropertyName("total_file_count")]
        public int TotalFileCount { get; set; }
    }

    public class GroupFileSystemInfo
    {
        [JsonPropertyName("file_count")]
        public int FileCount { get; set; }

        [JsonPropertyName("limit_count")]
        public int LimitCount { get; set; }

        [JsonPropertyName("used_space")]
        public long UsedSpace { get; set; }

        [JsonPropertyName("total_space")]
        public long TotalSpace { get; set; }
    }

    public class GroupFilesResult
    {
        [JsonPropertyName("files")]
        public List<GroupFileInfo> Files { get; set; } = new();

        [JsonPropertyName("folders")]
        public List<GroupFolderInfo> Folders { get; set; } = new();
    }

    public class FileUrlResult
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }

    public class ImageInfo
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("file_size")]
        public string FileSize { get; set; } = string.Empty;

        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;
    }

    public class RecordInfo
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("file_size")]
        public string FileSize { get; set; } = string.Empty;

        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;
    }
}
