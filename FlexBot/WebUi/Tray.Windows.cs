using System.Runtime.InteropServices;

namespace FlexBot.WebUi;

/// <summary>
/// Windows 托盘实现：<c>Shell_NotifyIcon</c> + 子类化窗口过程。
///
/// 为什么用"自己的消息窗口"而不是拿 Photino 的窗口收托盘消息：Shell_NotifyIcon 的回调消息
/// 会打到注册时给的那个 HWND，而 Photino 的窗口过程是它自己实现的，我们往里塞自己的消息语义
/// 既脆弱又容易和它抢。另起一个隐藏窗口（带自己的消息循环）收托盘事件最干净；
/// 需要改动的只有 Photino 窗口的 WM_CLOSE 一处，用 SetWindowLongPtr 换过程、其余原样转发。
/// </summary>
static class TrayWindows
{
    private const int WM_CLOSE = 0x0010;
    private const int WM_COMMAND = 0x0111;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NULL = 0x0000;
    private const int WM_APP = 0x8000;
    private const int TrayCallback = WM_APP + 1;
    private const int CmdOpen = 1;
    private const int CmdQuit = 2;
    private const int GWLP_WNDPROC = -4;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int NIM_ADD = 0x0;
    private const int NIM_DELETE = 0x2;
    private const int NIF_MESSAGE = 0x1;
    private const int NIF_ICON = 0x2;
    private const int NIF_TIP = 0x4;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x0000;
    private const int IDI_APPLICATION = 32512;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint LR_DEFAULTSIZE = 0x0040;

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    // v3 布局：字段少一个都会让 Shell_NotifyIcon 判 cbSize 不合法而失败，所以照足声明。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private static IntPtr _trayHwnd;
    private static IntPtr _appHwnd;
    private static IntPtr _oldAppProc;
    private static TrayWndProcHolder? _holder;   // 持有委托，防止 GC 回收后回调地址悬空
    private static NOTIFYICONDATA _data;
    private static bool _added;

    private sealed class TrayWndProcHolder
    {
        public WndProc? TrayProc;
        public WndProc? AppProc;
    }

    internal static bool TryInstall(IntPtr hwnd, string tooltip, Func<Task> onOpen, Action onQuit, out string? reason)
    {
        reason = null;
        _appHwnd = hwnd;
        var holder = new TrayWndProcHolder();
        _holder = holder;
        holder.TrayProc = TrayProc;
        holder.AppProc = AppProc;

        using var ready = new ManualResetEventSlim(false);
        string? failReason = null;

        // 托盘消息窗口必须有自己的消息循环，才能在 Photino 阻塞在 WaitForClose 时也能收到点击。
        var thread = new Thread(() =>
        {
            try
            {
                var hInstance = GetModuleHandleW(null);
                var cls = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(holder.TrayProc!),
                    hInstance = hInstance,
                    lpszClassName = "FlexBotTrayWindow",
                };
                RegisterClassExW(ref cls);
                _trayHwnd = CreateWindowExW(0, "FlexBotTrayWindow", "FlexBotTray", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
                if (_trayHwnd == IntPtr.Zero)
                {
                    failReason = $"创建托盘消息窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）";
                    ready.Set();
                    return;
                }

                _data = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _trayHwnd,
                    uID = 1,
                    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                    uCallbackMessage = TrayCallback,
                    hIcon = LoadTrayIcon(),
                    szTip = tooltip,
                    szInfo = "",
                    szInfoTitle = "",
                    guidItem = Guid.Empty,
                };
                _added = Shell_NotifyIconW(NIM_ADD, ref _data);
                if (!_added)
                {
                    failReason = "Shell_NotifyIcon(ADD) 失败（资源管理器未就绪？）";
                    ready.Set();
                    return;
                }

                // 子类化 Photino 的窗口：只改 WM_CLOSE，其余原样转发
                _oldAppProc = SetWndProc(_appHwnd, Marshal.GetFunctionPointerForDelegate(holder.AppProc!));

                ready.Set();

                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                ready.Set();
            }
        })
        { IsBackground = true, Name = "FlexBotTray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
        {
            reason = "托盘线程启动超时";
            return false;
        }
        if (failReason is not null || !_added)
        {
            reason = failReason ?? "未知原因";
            return false;
        }
        return true;

        // ---- 窗口过程 ----
        IntPtr TrayProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == TrayCallback)
            {
                var evt = (int)(lParam.ToInt64() & 0xFFFF);
                if (evt == WM_LBUTTONUP)
                {
                    _ = onOpen();
                    return IntPtr.Zero;
                }
                if (evt == WM_RBUTTONUP)
                {
                    ShowMenu();
                    return IntPtr.Zero;
                }
            }
            if (msg == WM_COMMAND)
            {
                var cmd = (int)(wParam.ToInt64() & 0xFFFF);
                if (cmd == CmdOpen) _ = onOpen();
                else if (cmd == CmdQuit) Tray.RequestQuit(onQuit);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        // ---- Photino 窗口的替换过程：把"关闭"翻译成"隐藏" ----
        IntPtr AppProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_CLOSE && !Tray.Quitting)
            {
                ShowWindow(hWnd, SW_HIDE);
                Console.WriteLine("[tray] 窗口已收进托盘（单击托盘图标恢复；右键→退出）");
                return IntPtr.Zero;
            }
            return CallWindowProcW(_oldAppProc, hWnd, msg, wParam, lParam);
        }

        void ShowMenu()
        {
            try
            {
                var menu = CreatePopupMenu();
                AppendMenuW(menu, MF_STRING, CmdOpen, "打开控制台");
                AppendMenuW(menu, MF_STRING, CmdQuit, "退出 FlexBot");
                GetCursorPos(out var pt);
                // TrackPopupMenu 的前置要求：菜单宿主窗口必须先到前台，否则点外面菜单不消失
                SetForegroundWindow(_trayHwnd);
                var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _trayHwnd, IntPtr.Zero);
                DestroyMenu(menu);
                PostMessageW(_trayHwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
                if ((int)cmd == CmdOpen) _ = onOpen();
                else if ((int)cmd == CmdQuit) Tray.RequestQuit(onQuit);
            }
            catch (Exception ex) { Console.WriteLine($"[tray] 托盘菜单失败: {ex.Message}"); }
        }
    }

    /// <summary>把已隐藏/最小化的窗口叫回前台。由 PhotinoGui 在 UI 线程上调用。</summary>
    internal static void ShowAppWindow()
    {
        try
        {
            if (_appHwnd == IntPtr.Zero) return;
            ShowWindow(_appHwnd, SW_SHOW);
            ShowWindow(_appHwnd, SW_RESTORE);
            SetForegroundWindow(_appHwnd);
        }
        catch (Exception ex) { Console.WriteLine($"[tray] 恢复窗口失败: {ex.Message}"); }
    }

    /// <summary>卸载托盘图标（进程收尾时调用；不调用的话图标会残留到鼠标划过为止）。</summary>
    internal static void Remove()
    {
        try
        {
            if (_added) { _data.cbSize = Marshal.SizeOf<NOTIFYICONDATA>(); Shell_NotifyIconW(NIM_DELETE, ref _data); _added = false; }
            if (_oldAppProc != IntPtr.Zero && _appHwnd != IntPtr.Zero) { SetWndProc(_appHwnd, _oldAppProc); _oldAppProc = IntPtr.Zero; }
            if (_trayHwnd != IntPtr.Zero) { PostMessageW(_trayHwnd, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero); _trayHwnd = IntPtr.Zero; }
        }
        catch { }
    }

    /// <summary>优先用 exe 自带图标；失败退回系统默认图标（托盘的 16px 缩放由系统做）。</summary>
    private static IntPtr LoadTrayIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "Assets", "flexbot.ico");
            if (File.Exists(ico))
            {
                var h = LoadImageW(IntPtr.Zero, ico, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }
        return LoadIconW(IntPtr.Zero, (IntPtr)IDI_APPLICATION);
    }

    private static IntPtr SetWndProc(IntPtr hWnd, IntPtr newProc) =>
        IntPtr.Size == 8
            ? SetWindowLongPtrW(hWnd, GWLP_WNDPROC, newProc)
            : (IntPtr)SetWindowLongW(hWnd, GWLP_WNDPROC, newProc.ToInt32());

    // ---- Win32 ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(int exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLongW(IntPtr hWnd, int index, int value);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIconW(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, int id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
