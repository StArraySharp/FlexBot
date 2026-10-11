namespace FlexBot.PluginApi;

/// <summary>插件设置项定义（WebUI 自动渲染表单）。type: text | number | bool | select</summary>
public record PluginSettingDef(
    string Key,
    string Label,
    string Type = "text",
    string? Default = null,
    string? Description = null,
    IReadOnlyList<string>? Options = null);

/// <summary>
/// 插件接口：一切功能皆为插件（独立 DLL），由宿主负责加载、卸载与热重载。
/// 卸载契约：OnUnloadAsync 内必须停止所有后台任务/定时器、断开外部连接（如 MCP），
/// 并 Dispose 所有 IBotContext 持有的订阅（宿主也会兜底清理）。
/// </summary>
public interface IBotPlugin
{
    /// <summary>插件名（唯一标识，建议与 DLL/目录名一致）</summary>
    string Name { get; }

    string Version { get; }

    string Description { get; }

    /// <summary>加载：创建内部服务、订阅消息/事件、启动后台任务。</summary>
    Task OnLoadAsync(IBotContext context);

    /// <summary>卸载：停止一切后台活动、释放外部资源。返回后宿主将卸载程序集。</summary>
    Task OnUnloadAsync();

    /// <summary>插件设置项定义（WebUI 自动渲染表单）；无设置项保持默认（空）</summary>
    IReadOnlyList<PluginSettingDef> SettingDefs => [];

    /// <summary>WebUI 保存设置后回调（热应用，无需重载插件）</summary>
    Task OnSettingsChangedAsync() => Task.CompletedTask;
}
