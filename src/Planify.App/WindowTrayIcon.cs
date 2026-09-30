using System.Runtime.InteropServices;

namespace Planify.App;

/// <summary>Small Win32 notification-area icon for the WinUI main window.</summary>
internal sealed class WindowTrayIcon : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmContextMenu = 0x007B;
    private const uint WmApp = 0x8000;
    private const uint TrayCallback = WmApp + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCommand = 0x100;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x800;
    private const int SwHide = 0;
    private const int IdOpen = 1;
    private const int IdExit = 2;

    private readonly IntPtr hwnd;
    private readonly IntPtr icon;
    private readonly SubclassProc subclassProc;
    private readonly Action restore;
    private readonly Action exit;
    private bool disposed;
    private bool allowClose;

    public WindowTrayIcon(IntPtr hwnd, string iconPath, Action restore, Action exit)
    {
        this.hwnd = hwnd;
        this.restore = restore;
        this.exit = exit;
        subclassProc = WindowProc;

        icon = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        if (icon == IntPtr.Zero)
            throw new InvalidOperationException("Could not load the Planify notification-area icon.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));

        if (!SetWindowSubclass(hwnd, subclassProc, IntPtr.Zero, IntPtr.Zero))
        {
            DestroyIcon(icon);
            throw new InvalidOperationException("Could not initialize the Planify notification-area window hook.");
        }

        var data = CreateData();
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            RemoveWindowSubclass(hwnd, subclassProc, IntPtr.Zero);
            DestroyIcon(icon);
            throw new InvalidOperationException("Could not add the Planify icon to the notification area.");
        }

        data.Version = 4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        Window = hwnd,
        Id = 1,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = TrayCallback,
        Icon = icon,
        Tip = "Planify Windows"
    };

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, IntPtr subclassId, IntPtr reference)
    {
        if (message == WmClose && !allowClose)
        {
            ShowWindow(hwnd, SwHide);
            return IntPtr.Zero;
        }

        if (message == TrayCallback)
        {
            uint notification = unchecked((uint)lParam.ToInt64());
            if (notification is WmLButtonUp or WmLButtonDoubleClick)
                restore();
            else if (notification is WmRButtonUp or WmContextMenu)
                ShowContextMenu();
            return IntPtr.Zero;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MfString, (UIntPtr)IdOpen, "打开 Planify");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)IdExit, "退出");
            GetCursorPos(out POINT point);
            SetForegroundWindow(hwnd);
            uint selected = TrackPopupMenu(menu, TpmRightButton | TpmReturnCommand, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (selected == IdOpen) restore();
            else if (selected == IdExit)
            {
                allowClose = true;
                RemoveIcon();
                exit();
            }
        }
        finally { DestroyMenu(menu); }
    }

    private void RemoveIcon()
    {
        var data = CreateData();
        Shell_NotifyIcon(NimDelete, ref data);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        RemoveIcon();
        RemoveWindowSubclass(hwnd, subclassProc, IntPtr.Zero);
        DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, IntPtr subclassId, IntPtr reference);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, IntPtr subclassId, IntPtr reference);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, IntPtr subclassId);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}

