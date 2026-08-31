using System.Text.Json;
using System.Text.Json.Serialization;
using OneBotLib.Models;

namespace OneBotLib.Api
{
    public partial class OneBotClient
    {
        public async Task<ApiResult<GroupFilesResult>> GetGroupFilesAsync(long groupId, int? startIndex = null, int? fileCount = null, int? folderCount = null, string? folderId = null)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            if (startIndex.HasValue) parameters["start_index"] = startIndex.Value;
            if (fileCount.HasValue) parameters["file_count"] = fileCount.Value;
            if (folderCount.HasValue) parameters["folder_count"] = folderCount.Value;
            if (!string.IsNullOrEmpty(folderId)) parameters["folder_id"] = folderId;

            return await SendApiAsync<GroupFilesResult>("get_group_files", parameters);
        }

        public async Task<ApiResult<GroupFileUrl>> GetGroupFileUrlAsync(long groupId, string fileId, int busid)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "file_id", fileId },
                { "busid", busid }
            };
            return await SendApiAsync<GroupFileUrl>("get_group_file_url", parameters);
        }

        public async Task<ApiResult> UploadGroupFileAsync(long groupId, string file, string name, string? folder = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "file", file },
                { "name", name }
            };
            if (!string.IsNullOrEmpty(folder))
            {
                parameters["folder"] = folder;
            }
            var result = await SendApiAsync("upload_group_file", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> DeleteGroupFileAsync(long groupId, string fileId, int busid)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "file_id", fileId },
                { "busid", busid }
            };
            var result = await SendApiAsync("delete_group_file", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> CreateGroupFileFolderAsync(long groupId, string name, string? parentId = null)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "name", name }
            };
            if (!string.IsNullOrEmpty(parentId))
            {
                parameters["parent_id"] = parentId;
            }
            var result = await SendApiAsync("create_group_file_folder", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult> DeleteGroupFolderAsync(long groupId, string folderId)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "folder_id", folderId }
            };
            var result = await SendApiAsync("delete_group_folder", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<GroupSpaceInfo>> GetGroupSpaceInfoAsync(long groupId)
        {
            var parameters = new Dictionary<string, object> { { "group_id", groupId } };
            return await SendApiAsync<GroupSpaceInfo>("get_group_space_info", parameters);
        }

        public async Task<ApiResult<GroupRootFiles>> GetGroupRootFilesAsync(long groupId, int startIndex = 0, int fileCount = 50, int folderCount = 50)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "start_index", startIndex },
                { "file_count", fileCount },
                { "folder_count", folderCount }
            };
            return await SendApiAsync<GroupRootFiles>("get_group_root_files", parameters);
        }

        public async Task<ApiResult<GroupRootFiles>> GetGroupFilesByFolderAsync(long groupId, string folderId, int startIndex = 0, int fileCount = 50, int folderCount = 50)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "folder_id", folderId },
                { "start_index", startIndex },
                { "file_count", fileCount },
                { "folder_count", folderCount }
            };
            return await SendApiAsync<GroupRootFiles>("get_group_files_by_folder", parameters);
        }

        public async Task<ApiResult<GroupFileUrl>> GetGroupFileUrlAsync(long groupId, string fileId)
        {
            var parameters = new Dictionary<string, object>
            {
                { "group_id", groupId },
                { "file_id", fileId }
            };
            return await SendApiAsync<GroupFileUrl>("get_group_file_url", parameters);
        }

        public async Task<ApiResult> UploadPrivateFileAsync(long userId, string file, string name)
        {
            var parameters = new Dictionary<string, object>
            {
                { "user_id", userId },
                { "file", file },
                { "name", name }
            };
            var result = await SendApiAsync("upload_private_file", parameters);
            return result.Success ? ApiResult.Ok() : ApiResult.Fail(result.ErrorMessage!, result.StackTrace);
        }

        public async Task<ApiResult<GroupFileUrl>> GetFileAsync(string fileId)
        {
            var parameters = new Dictionary<string, object> { { "file_id", fileId } };
            return await SendApiAsync<GroupFileUrl>("get_file", parameters);
        }

        public async Task<ApiResult<string>> UploadFileAsync(string file, string? name = null)
        {
            var parameters = new Dictionary<string, object> { { "file", file } };
            if (!string.IsNullOrEmpty(name))
            {
                parameters["name"] = name;
            }
            var result = await SendApiAsync("upload_file", parameters);
            if (!result.Success)
            {
                return ApiResult<string>.Fail(result.ErrorMessage!, result.StackTrace);
            }
            var fileId = result.Data.TryGetProperty("file_id", out var id) ? id.GetString() ?? "" : "";
            return ApiResult<string>.Ok(fileId);
        }
    }

    public class GroupFilesResult
    {
        [JsonPropertyName("files")]
        public List<GroupFileInfo> Files { get; set; } = new();

        [JsonPropertyName("folders")]
        public List<GroupFolderInfo> Folders { get; set; } = new();
    }

    public class GroupRootFiles
    {
        [JsonPropertyName("files")]
        public List<GroupFileInfo> Files { get; set; } = new();

        [JsonPropertyName("folders")]
        public List<GroupFolderInfo> Folders { get; set; } = new();
    }

    public class GroupFileInfo
    {
        [JsonPropertyName("file_id")]
        public string FileId { get; set; } = string.Empty;

        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("busid")]
        public int Busid { get; set; }

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

    public class GroupFileUrl
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("file_id")]
        public string FileId { get; set; } = string.Empty;
    }

    public class GroupSpaceInfo
    {
        [JsonPropertyName("total_size")]
        public long TotalSize { get; set; }

        [JsonPropertyName("used_size")]
        public long UsedSize { get; set; }
    }
}
