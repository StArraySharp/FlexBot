namespace FlexBot.PluginApi;

/// <summary>
/// 长期记忆条目存储口（宿主实现，经 <see cref="IBotContext.Notes"/> 给插件）。
///
/// 用途：把 <see cref="MemoryNoteParser"/> 解析出的条目落进库，让"会自己积累的长期记忆"成真。
/// scope 必须来自当前回合（与历史库同一原则）：绝不允许把模型/用户给的字符串直接当 scope。
/// </summary>
public interface IMemoryNotes
{
    /// <summary>写入一条（同 scope 同文本视为同一条：更新重要度与时间，不重复插入）</summary>
    void Add(string scope, string kind, string text, int importance, string tags);

    /// <summary>某 scope 的记忆条目（按重要度与时间倒序）</summary>
    IReadOnlyList<MemoryNote> List(string scope, int limit = 50);
}
