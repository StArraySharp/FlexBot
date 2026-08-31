using System.Text.Json.Serialization;

namespace OneBotLib.MessageSegment
{
    public class MessageSegment
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public Dictionary<string, object> Data { get; set; } = new();

        public static MessageSegment Text(string text)
        {
            return new MessageSegment
            {
                Type = "text",
                Data = new Dictionary<string, object> { { "text", text } }
            };
        }

        public static MessageSegment At(long userId)
        {
            return new MessageSegment
            {
                Type = "at",
                Data = new Dictionary<string, object> { { "qq", userId } }
            };
        }

        public static MessageSegment AtAll()
        {
            return new MessageSegment
            {
                Type = "at",
                Data = new Dictionary<string, object> { { "qq", "all" } }
            };
        }

        public static MessageSegment Face(int id)
        {
            return new MessageSegment
            {
                Type = "face",
                Data = new Dictionary<string, object> { { "id", id } }
            };
        }

        public static MessageSegment Image(string file, bool? cache = null, bool? proxy = null, int? timeout = null)
        {
            var data = new Dictionary<string, object> { { "file", file } };
            if (cache.HasValue) data["cache"] = cache.Value ? 1 : 0;
            if (proxy.HasValue) data["proxy"] = proxy.Value ? 1 : 0;
            if (timeout.HasValue) data["timeout"] = timeout.Value;
            return new MessageSegment { Type = "image", Data = data };
        }

        public static MessageSegment Record(string file, bool? magic = null, bool? cache = null, bool? proxy = null, int? timeout = null)
        {
            var data = new Dictionary<string, object> { { "file", file } };
            if (magic.HasValue) data["magic"] = magic.Value ? 1 : 0;
            if (cache.HasValue) data["cache"] = cache.Value ? 1 : 0;
            if (proxy.HasValue) data["proxy"] = proxy.Value ? 1 : 0;
            if (timeout.HasValue) data["timeout"] = timeout.Value;
            return new MessageSegment { Type = "record", Data = data };
        }

        public static MessageSegment Video(string file, bool? cache = null, bool? proxy = null, int? timeout = null)
        {
            var data = new Dictionary<string, object> { { "file", file } };
            if (cache.HasValue) data["cache"] = cache.Value ? 1 : 0;
            if (proxy.HasValue) data["proxy"] = proxy.Value ? 1 : 0;
            if (timeout.HasValue) data["timeout"] = timeout.Value;
            return new MessageSegment { Type = "video", Data = data };
        }

        public static MessageSegment Reply(long messageId)
        {
            return new MessageSegment
            {
                Type = "reply",
                Data = new Dictionary<string, object> { { "id", messageId } }
            };
        }

        public static MessageSegment Forward(string id)
        {
            return new MessageSegment
            {
                Type = "forward",
                Data = new Dictionary<string, object> { { "id", id } }
            };
        }

        public static MessageSegment Node(long userId, string nickname, List<MessageSegment> content)
        {
            return new MessageSegment
            {
                Type = "node",
                Data = new Dictionary<string, object>
                {
                    { "user_id", userId },
                    { "nickname", nickname },
                    { "content", content }
                }
            };
        }

        public static MessageSegment Xml(string data)
        {
            return new MessageSegment
            {
                Type = "xml",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment Json(string data)
        {
            return new MessageSegment
            {
                Type = "json",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment Location(double lat, double lon, string? title = null, string? content = null)
        {
            var data = new Dictionary<string, object>
            {
                { "lat", lat },
                { "lon", lon }
            };
            if (!string.IsNullOrEmpty(title)) data["title"] = title;
            if (!string.IsNullOrEmpty(content)) data["content"] = content;
            return new MessageSegment { Type = "location", Data = data };
        }

        public static MessageSegment Share(string url, string title, string? content = null, string? image = null)
        {
            var data = new Dictionary<string, object>
            {
                { "url", url },
                { "title", title }
            };
            if (!string.IsNullOrEmpty(content)) data["content"] = content;
            if (!string.IsNullOrEmpty(image)) data["image"] = image;
            return new MessageSegment { Type = "share", Data = data };
        }

        public static MessageSegment Contact(long userId)
        {
            return new MessageSegment
            {
                Type = "contact",
                Data = new Dictionary<string, object> { { "type", "qq" }, { "id", userId } }
            };
        }

        public static MessageSegment ContactGroup(long groupId)
        {
            return new MessageSegment
            {
                Type = "contact",
                Data = new Dictionary<string, object> { { "type", "group" }, { "id", groupId } }
            };
        }

        public static MessageSegment Dice()
        {
            return new MessageSegment
            {
                Type = "dice",
                Data = new Dictionary<string, object>()
            };
        }

        public static MessageSegment Rps()
        {
            return new MessageSegment
            {
                Type = "rps",
                Data = new Dictionary<string, object>()
            };
        }

        public static MessageSegment Shake()
        {
            return new MessageSegment
            {
                Type = "shake",
                Data = new Dictionary<string, object>()
            };
        }

        public static MessageSegment Poke(long type, long id)
        {
            return new MessageSegment
            {
                Type = "poke",
                Data = new Dictionary<string, object> { { "type", type }, { "id", id } }
            };
        }

        public static MessageSegment Anonymous(bool? ignore = null)
        {
            var data = new Dictionary<string, object>();
            if (ignore.HasValue) data["ignore"] = ignore.Value;
            return new MessageSegment { Type = "anonymous", Data = data };
        }

        public static MessageSegment Music(long id, string type = "qq")
        {
            return new MessageSegment
            {
                Type = "music",
                Data = new Dictionary<string, object> { { "type", type }, { "id", id } }
            };
        }

        public static MessageSegment MusicCustom(string url, string audio, string title, string? content = null, string? image = null)
        {
            var data = new Dictionary<string, object>
            {
                { "type", "custom" },
                { "url", url },
                { "audio", audio },
                { "title", title }
            };
            if (!string.IsNullOrEmpty(content)) data["content"] = content;
            if (!string.IsNullOrEmpty(image)) data["image"] = image;
            return new MessageSegment { Type = "music", Data = data };
        }

        public static MessageSegment File(string file, string name)
        {
            return new MessageSegment
            {
                Type = "file",
                Data = new Dictionary<string, object> { { "file", file }, { "name", name } }
            };
        }

        public static MessageSegment Mface(int emojiPackageId, string emojiId, string key, string? summary = null)
        {
            var data = new Dictionary<string, object>
            {
                { "emoji_package_id", emojiPackageId },
                { "emoji_id", emojiId },
                { "key", key }
            };
            if (!string.IsNullOrEmpty(summary)) data["summary"] = summary;
            return new MessageSegment { Type = "mface", Data = data };
        }

        public static MessageSegment Markdown(string content)
        {
            return new MessageSegment
            {
                Type = "markdown",
                Data = new Dictionary<string, object> { { "content", content } }
            };
        }

        public static MessageSegment MiniApp(string data)
        {
            return new MessageSegment
            {
                Type = "mini_app",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment Ark(string data)
        {
            return new MessageSegment
            {
                Type = "ark",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment Keyboard(string data)
        {
            return new MessageSegment
            {
                Type = "keyboard",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment KeyboardButton(string id, string label, string? action = null, string? permission = null)
        {
            var data = new Dictionary<string, object>
            {
                { "id", id },
                { "label", label }
            };
            if (!string.IsNullOrEmpty(action)) data["action"] = action;
            if (!string.IsNullOrEmpty(permission)) data["permission"] = permission;
            return new MessageSegment { Type = "button", Data = data };
        }

        public static MessageSegment RichText(string content)
        {
            return new MessageSegment
            {
                Type = "rich_text",
                Data = new Dictionary<string, object> { { "content", content } }
            };
        }

        public static MessageSegment Gift(long userId, int giftId)
        {
            return new MessageSegment
            {
                Type = "gift",
                Data = new Dictionary<string, object> { { "user_id", userId }, { "gift_id", giftId } }
            };
        }

        public static MessageSegment Card(string data)
        {
            return new MessageSegment
            {
                Type = "card",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment Enotify(string title, string content)
        {
            return new MessageSegment
            {
                Type = "enotify",
                Data = new Dictionary<string, object> { { "title", title }, { "content", content } }
            };
        }

        public static MessageSegment Bubble(string data)
        {
            return new MessageSegment
            {
                Type = "bubble",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment AtWithNick(long userId, string? nickname = null)
        {
            var data = new Dictionary<string, object> { { "qq", userId } };
            if (!string.IsNullOrEmpty(nickname)) data["nickname"] = nickname;
            return new MessageSegment { Type = "at", Data = data };
        }

        public static MessageSegment Tts(string text)
        {
            return new MessageSegment
            {
                Type = "tts",
                Data = new Dictionary<string, object> { { "text", text } }
            };
        }

        public static MessageSegment MultiMsg(string data)
        {
            return new MessageSegment
            {
                Type = "multi_msg",
                Data = new Dictionary<string, object> { { "data", data } }
            };
        }

        public static MessageSegment LongMsg(string id)
        {
            return new MessageSegment
            {
                Type = "long_msg",
                Data = new Dictionary<string, object> { { "id", id } }
            };
        }

        public static MessageSegment InlineKeyboard(List<List<KeyboardButtonData>> rows)
        {
            return new MessageSegment
            {
                Type = "inline_keyboard",
                Data = new Dictionary<string, object> { { "rows", rows } }
            };
        }

        public static MessageSegment Link(string url, string text)
        {
            return new MessageSegment
            {
                Type = "link",
                Data = new Dictionary<string, object> { { "url", url }, { "text", text } }
            };
        }

        public static MessageSegment Mention(string userId)
        {
            return new MessageSegment
            {
                Type = "mention",
                Data = new Dictionary<string, object> { { "user_id", userId } }
            };
        }

        public static MessageSegment Emoji(string emojiId)
        {
            return new MessageSegment
            {
                Type = "emoji",
                Data = new Dictionary<string, object> { { "emoji_id", emojiId } }
            };
        }
    }

    public class KeyboardButtonData
    {
        public string Id { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string? Action { get; set; }
        public string? Permission { get; set; }
        public bool? Enter { get; set; }
    }
}
