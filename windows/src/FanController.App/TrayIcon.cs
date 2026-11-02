using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace FanController.App;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly nint _window;
    private readonly Action _open, _quit, _hide;
    private readonly DispatcherQueue _dispatcher;
    private readonly SubclassProc _procedure;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private bool _disposed;

    public TrayIcon(nint window, DispatcherQueue dispatcher, Action open, Action hide, Action quit)
    {
        _window = window;
        _dispatcher = dispatcher;
        _open = open;
        _hide = hide;
        _quit = quit;
        _procedure = ProcessMessage;
        if (!SetWindowSubclass(window, _procedure, 1, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Installation de l’icône de notification impossible.");
        if (!AddIcon())
        {
            RemoveWindowSubclass(window, _procedure, 1);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Création de l’icône de notification impossible.");
        }
    }

    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = 1 | 2 | 4, Callback = CallbackMessage,
        Icon = LoadIcon(0, 32516), Tip = "FanController — double-clic pour ouvrir",
        Info = "", InfoTitle = ""
    };
    private bool AddIcon()
    {
        var data = Data();
        return Shell_NotifyIcon(0, ref data);
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
            AppendMenu(menu, 0, 1, "Ouvrir FanController");
            AppendMenu(menu, 0, 2, "Quitter (arrêter l’acquisition)");
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
        var data = Data();
        Shell_NotifyIcon(2, ref data);
        RemoveWindowSubclass(_window, _procedure, 1);
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
