using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using FanController.App.Localization;

namespace FanController.App;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly nint _disconnectedIcon;
    private bool _connected;
    private bool _french;
    private readonly Action _open, _quit, _hide;
    private readonly DispatcherQueue _dispatcher;
    private readonly SubclassProc _procedure;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _available;
    private bool _disposed;
    public bool IsAvailable => _available;

    public TrayIcon(nint window, DispatcherQueue dispatcher, string iconPath, string disconnectedIconPath, Action open, Action hide, Action quit)
    {
        _window = window;
        _dispatcher = dispatcher;
        _open = open;
        _hide = hide;
        _quit = quit;
        _procedure = ProcessMessage;
        var size = GetSystemMetrics(49);
        _icon = LoadImage(0, iconPath, 1, size, size, 0x10);
        if (_icon == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Chargement de l’icône FanController impossible.");
        _disconnectedIcon = LoadImage(0, disconnectedIconPath, 1, size, size, 0x10);
        if (_disconnectedIcon == 0)
        {
            var error = Marshal.GetLastWin32Error();
            DestroyIcon(_icon);
            throw new Win32Exception(error, "Chargement de l’icône de déconnexion impossible.");
        }
        if (!SetWindowSubclass(window, _procedure, 1, 0))
        {
            DestroyIcon(_icon);
            DestroyIcon(_disconnectedIcon);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Installation de l’icône de notification impossible.");
        }
        if (!AddIcon())
        {
            var error = Marshal.GetLastWin32Error();
            RemoveWindowSubclass(window, _procedure, 1);
            Diagnostics.Record(new Win32Exception(error, "Création de l’icône de notification impossible."));
            DestroyIcon(_icon);
            DestroyIcon(_disconnectedIcon);
            return;
        }
        _available = true;
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage,
        Icon = _connected ? _icon : _disconnectedIcon,
        Tip = Localizer.Get(_connected ? "Tray.TooltipConnected" : "Tray.TooltipDisconnected",
            _connected ? "FanController — connected — double-click to open" : "FanController — disconnected — double-click to open"),
        Info = "", InfoTitle = ""
    };
    private bool AddIcon()
    {
        var data = Data();
        return Shell_NotifyIcon(0, ref data);
    }

    public void SetConnected(bool connected)
    {
        if (_disposed || !_available || _connected == connected) return;
        _connected = connected;
        var data = Data();
        if (!Shell_NotifyIcon(1, ref data))
        {
            Diagnostics.Record(new Win32Exception(Marshal.GetLastWin32Error(),
                "Mise à jour de l’état de l’icône de notification impossible."));
        }
    }

    public void SetFrench(bool french)
    {
        if (_disposed || !_available || _french == french) return;
        _french = french;
        var data = Data();
        Shell_NotifyIcon(1, ref data);
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (!_disposed)
            {
                if (message == _taskbarCreated && !AddIcon())
                {
                    Diagnostics.Record(new IOException("Restauration de l’icône après redémarrage Explorer impossible."));
                    _dispatcher.TryEnqueue(() => _open());
                }
                if (message == 5 && wParam == 1) _dispatcher.TryEnqueue(() => _hide());
                if (message == CallbackMessage)
                {
                    if (lParam == 0x203) _dispatcher.TryEnqueue(() => _open());
                    if (lParam == 0x205 || lParam == 0x7B) ShowMenu();
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Record(ex);
            _dispatcher.TryEnqueue(() => _open());
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Menu de notification indisponible.");
        try
        {
            AppendMenu(menu, 0, 1, Localizer.Get("Tray.Open", "Open FanController"));
            AppendMenu(menu, 0, 2, Localizer.Get("Tray.Quit", "Quit (stop acquisition)"));
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var choice = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0);
            if (choice == 1) _dispatcher.TryEnqueue(() => _open());
            if (choice == 2) _dispatcher.TryEnqueue(() => _quit());
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_available) return;
        var data = Data();
        Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(_window, _procedure, 1);
        DestroyIcon(_icon);
        DestroyIcon(_disconnectedIcon);
    }

    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
