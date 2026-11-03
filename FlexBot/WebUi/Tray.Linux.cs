using System.Runtime.InteropServices;

namespace FlexBot.WebUi;

/// <summary>
/// Linux 托盘：GTK 的 <c>delete-event</c> 拦关闭 + <c>libayatana-appindicator3</c> 出图标。
///
/// 为什么不用 NuGet：AppIndicator 在 NuGet 上没有绑定；GtkSharp 绑的是 GTK 本体，
/// 它会调用 Gtk.Application.Init()，与 Photino 自己的 GTK 初始化/线程模型冲突。
/// 真正提供托盘的是系统库，直接 P/Invoke 最轻、也最不容易和 Photino 抢初始化。
///
/// 关闭拦截用 delete-event：用户点关闭按钮就是该信号，处理函数返回 TRUE(=1) 表示
/// "已处理，别走默认销毁"，在里面 gtk_widget_hide 即可。这是 GTK 里唯一干净的挂点。
///
/// 库全部用 NativeLibrary 探测后按名字取符号：缺库/缺符号时返回 false（调用方退回"关窗即退出"），
/// 而不是让 DllNotFoundException 冒到启动链上。
///
/// 实测范围：本机只有 Windows，**Linux 侧未在真机运行验证**；逻辑与 Windows 版一一对应。
/// </summary>
static class TrayLinux
{
    private const int CategoryApplicationStatus = 0;
    private const int StatusActive = 1;
    private const int StatusPassive = 0;

    private static IntPtr _window;
    private static IntPtr _indicator;
    private static Func<Task>? _onOpen;
    private static Action? _onQuit;

    // 委托必须静态持有：交给原生层的是函数指针，被 GC 回收就是野指针
    private static DeleteFn? _delete;
    private static ActivateFn? _activate;
    private static IndNewFn? _indNew;
    private static IndStatusFn? _indStatus;
    private static IndMenuFn? _indMenu;
    private static IndIconFn? _indIcon;
    private static IndTitleFn? _indTitle;

    private delegate int DeleteFn(IntPtr widget, IntPtr evt, IntPtr data);
    private delegate void ActivateFn(IntPtr item, IntPtr data);
    private delegate IntPtr IndNewFn(string id, string icon, int category);
    private delegate void IndStatusFn(IntPtr ind, int status);
    private delegate void IndMenuFn(IntPtr ind, IntPtr menu);
    private delegate void IndIconFn(IntPtr ind, string icon, string desc);
    private delegate void IndTitleFn(IntPtr ind, string title);
    private delegate uint SignalConnectFn(IntPtr instance, string signal, IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    private delegate void W1Fn(IntPtr a);
    private delegate IntPtr MenuNewFn();
    private delegate IntPtr MenuItemFn(string label);
    private delegate void MenuAppendFn(IntPtr menu, IntPtr item);

    private static SignalConnectFn? _connect;
    private static W1Fn? _hide, _showAll, _present;
    private static MenuNewFn? _menuNew;
    private static MenuItemFn? _menuItemNew;
    private static MenuAppendFn? _menuAppend;

    internal static bool TryInstall(IntPtr hwnd, string tooltip, Func<Task> onOpen, Action onQuit, out string? reason)
    {
        reason = null;
        if (!OperatingSystem.IsLinux()) { reason = "非 Linux 平台"; return false; }
        // Photino 在 Linux 不提供窗口句柄（WindowHandle 仅 Windows 可用），下面用 GTK 枚举拿到

        // 1) GTK：Photino 已经把它加载进进程了，这里只取符号，不再 Init
        var gtk = FirstLoad("libgtk-3.so.0", "libgtk-3.so");
        var gobject = FirstLoad("libgobject-2.0.so.0", "libgobject-2.0.so");
        if (gtk == IntPtr.Zero || gobject == IntPtr.Zero) { reason = "未找到 GTK3 运行库"; return false; }

        _hide = Bind<W1Fn>(gtk, "gtk_widget_hide");
        _showAll = Bind<W1Fn>(gtk, "gtk_widget_show_all");
        _present = Bind<W1Fn>(gtk, "gtk_window_present");
        _menuNew = Bind<MenuNewFn>(gtk, "gtk_menu_new");
        _menuItemNew = Bind<MenuItemFn>(gtk, "gtk_menu_item_new_with_label");
        _menuAppend = Bind<MenuAppendFn>(gtk, "gtk_menu_shell_append");
        _connect = Bind<SignalConnectFn>(gobject, "g_signal_connect_data");
        _listToplevels = Bind<ListFn>(gtk, "gtk_window_list_toplevels");

        // 自己找窗口：gtk_window_list_toplevels 返回 GList<GtkWindow*>，本进程只有一个顶层窗口
        if (hwnd == IntPtr.Zero) hwnd = DiscoverWindow();
        if (hwnd == IntPtr.Zero) { reason = "找不到 GtkWindow（gtk_window_list_toplevels 为空）"; return false; }
        if (_hide is null || _showAll is null || _menuNew is null || _menuItemNew is null || _menuAppend is null || _connect is null)
        {
            reason = "GTK 符号缺失（版本不匹配？）";
            return false;
        }

        // 2) AppIndicator：库名各发行版不一，逐个探
        var indLib = FirstLoad("libayatana-appindicator3.so.1", "libayatana-appindicator3.so", "libappindicator3.so.1");
        if (indLib == IntPtr.Zero) { reason = "未安装 libayatana-appindicator3"; return false; }

        _indNew = Bind<IndNewFn>(indLib, "app_indicator_new");
        _indStatus = Bind<IndStatusFn>(indLib, "app_indicator_set_status");
        _indMenu = Bind<IndMenuFn>(indLib, "app_indicator_set_menu");
        _indIcon = Bind<IndIconFn>(indLib, "app_indicator_set_icon_full");
        _indTitle = Bind<IndTitleFn>(indLib, "app_indicator_set_title");
        if (_indNew is null || _indStatus is null || _indMenu is null)
        {
            reason = "AppIndicator 符号缺失";
            return false;
        }

        _window = hwnd;
        _onOpen = onOpen;
        _onQuit = onQuit;

        // 3) 拦关闭：delete-event → 隐藏，返回 1 阻止默认销毁
        _delete = OnDeleteEvent;
        if (_connect(hwnd, "delete-event", Marshal.GetFunctionPointerForDelegate(_delete), IntPtr.Zero, IntPtr.Zero, 0) == 0)
        {
            reason = "连接 delete-event 失败";
            return false;
        }

        // 4) 托盘图标 + 菜单（AppIndicator 必须有菜单，否则点击无反应）
        _indicator = _indNew("flexbot", "application-x-executable", CategoryApplicationStatus);
        if (_indicator == IntPtr.Zero) { reason = "app_indicator_new 返回空"; return false; }

        var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "flexbot.ico");
        _indIcon?.Invoke(_indicator, File.Exists(ico) ? ico : "application-x-executable", tooltip);
        _indTitle?.Invoke(_indicator, tooltip);

        _activate = OnActivate;
        var menu = _menuNew();
        var open = _menuItemNew("打开控制台");
        var quit = _menuItemNew("退出 FlexBot");
        var fp = Marshal.GetFunctionPointerForDelegate(_activate);
        _connect(open, "activate", fp, 1, IntPtr.Zero, 0);
        _connect(quit, "activate", fp, 2, IntPtr.Zero, 0);
        _menuAppend(menu, open);
        _menuAppend(menu, quit);
        _showAll(menu);
        _indMenu(_indicator, menu);
        _indStatus(_indicator, StatusActive);
        return true;
    }

    internal static void ShowAppWindow()
    {
        try
        {
            if (_window == IntPtr.Zero) return;
            _showAll?.Invoke(_window);
            _present?.Invoke(_window);
        }
        catch (Exception ex) { Console.WriteLine($"[tray] 恢复窗口失败: {ex.Message}"); }
    }

    internal static void Remove()
    {
        try
        {
            if (_indicator != IntPtr.Zero && _indStatus is not null)
                _indStatus(_indicator, StatusPassive);
            _indicator = IntPtr.Zero;
        }
        catch { }
    }

    private static int OnDeleteEvent(IntPtr widget, IntPtr evt, IntPtr data)
    {
        try
        {
            if (Tray.Quitting) return 0;          // 放行，真正关闭
            _hide?.Invoke(widget);
            Console.WriteLine("[tray] 窗口已收进托盘（托盘菜单→打开控制台）");
            return 1;                              // 已处理
        }
        catch { return 0; }
    }

    private static void OnActivate(IntPtr item, IntPtr data)
    {
        switch (data.ToInt64())
        {
            case 1: _ = _onOpen?.Invoke(); break;
            case 2 when _onQuit is not null: Tray.RequestQuit(_onQuit); break;
        }
    }

    private static IntPtr FirstLoad(params string[] names)
    {
        foreach (var n in names)
            if (NativeLibrary.TryLoad(n, out var h)) return h;
        return IntPtr.Zero;
    }

    private static T? Bind<T>(IntPtr lib, string symbol) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, symbol, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

    private static ListFn? _listToplevels;
    private delegate IntPtr ListFn();

    /// <summary>
    /// 枚举 GTK 顶层窗口取我们那一个（Photino 不给我们句柄）。
    /// GList 布局：{ gpointer data; GList* next; GList* prev; } —— data 在偏移 0，next 在 IntPtr.Size。
    /// 取最后一个非空项：Photino 的窗口是唯一顶层窗口，遍历到底即可，不依赖顺序细节。
    /// </summary>
    private static IntPtr DiscoverWindow()
    {
        try
        {
            var list = _listToplevels?.Invoke() ?? IntPtr.Zero;
            var found = IntPtr.Zero;
            for (var node = list; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var w = Marshal.ReadIntPtr(node);
                if (w != IntPtr.Zero) found = w;
            }
            return found;
        }
        catch { return IntPtr.Zero; }
    }
}
