using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Bulles;

internal static class Native
{
    public const int GWL_EXSTYLE = -20, GWL_STYLE = -16, GWLP_HWNDPARENT = -8;
    public const long WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_MAXIMIZE = 0x1000000, WS_MINIMIZE = 0x20000000;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
    public const long WS_EX_LAYERED = 0x80000;
    public const uint LWA_ALPHA = 0x2;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;

    public const int SW_SHOWNORMAL = 1, SW_SHOWMAXIMIZED = 3, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;
    public const int GW_OWNER = 4;

    public const int WM_HOTKEY = 0x312, WM_SYSCOMMAND = 0x112, SC_KEYMENU = 0xF100;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DEFAULT = 0, DWMWCP_ROUND = 2;

    public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    public static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

    public static long GetExStyle(IntPtr h) => GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
    public static void SetExStyle(IntPtr h, long style) => SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(style));
    public static long GetStyle(IntPtr h) => GetWindowLongPtr(h, GWL_STYLE).ToInt64();
    public static void SetStyle(IntPtr h, long style) => SetWindowLongPtr(h, GWL_STYLE, new IntPtr(style));

    /// <summary>Coupe (ou remet) les animations Windows d'ouverture/réduction d'une fenêtre.</summary>
    public static void SetTransitionsDisabled(IntPtr h, bool disabled)
    {
        int value = disabled ? 1 : 0;
        DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
    }

    /// <summary>Applique une opacité globale (0-100 %) à une fenêtre, ou la retire à 100 %.</summary>
    public static void SetOpacity(IntPtr h, int percent, long baseExStyle)
    {
        if (percent >= 100)
        {
            if ((baseExStyle & WS_EX_LAYERED) == 0) SetExStyle(h, GetExStyle(h) & ~WS_EX_LAYERED);
            else SetLayeredWindowAttributes(h, 0, 255, LWA_ALPHA);
            return;
        }
        SetExStyle(h, GetExStyle(h) | WS_EX_LAYERED);
        SetLayeredWindowAttributes(h, 0, (byte)(Math.Clamp(percent, 10, 100) * 255 / 100), LWA_ALPHA);
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static uint ProcessId(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    public static string? ProcessPath(IntPtr hwnd) => ProcessPathFromPid(ProcessId(hwnd));

    public static string? ProcessPathFromPid(uint pid)
    {
        var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(proc, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(proc);
        }
    }
}
