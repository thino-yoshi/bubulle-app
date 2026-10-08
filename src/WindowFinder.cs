using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Bulles;

/// <summary>Retrouve la fenêtre principale d'une application à partir du nom de son processus.</summary>
public static class WindowFinder
{
    private static readonly HashSet<string> IgnoredClasses = new()
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    public static IntPtr Find(string processName, IntPtr preferred)
    {
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;
        if (preferred != IntPtr.Zero && IsCandidate(preferred, processName)) return preferred;

        // Plusieurs fenêtres possibles (écran de chargement de Discord, popups…) : on prend la plus grande.
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        Native.EnumWindows((h, _) =>
        {
            if (IsCandidate(h, processName))
            {
                var p = new Native.WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
                Native.GetWindowPlacement(h, ref p);
                long area = (long)p.rcNormalPosition.Width * p.rcNormalPosition.Height;
                if (area > bestArea)
                {
                    best = h;
                    bestArea = area;
                }
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Apps qui ont une fenêtre ouverte en ce moment (une entrée par programme), pour en faire des bulles.</summary>
    public static List<string> OpenAppPaths()
    {
        var ourPid = (uint)Environment.ProcessId;
        var paths = new List<string>();
        Native.EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h) || Native.ProcessId(h) == ourPid) return true;
            var path = Native.ProcessPath(h);
            if (path == null || paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
            var exe = Path.GetFileNameWithoutExtension(path);
            // Le bureau et la barre des tâches appartiennent aussi à explorer ; les apps du Store passent
            // par ApplicationFrameHost et ne peuvent pas être mises dans une bulle.
            if (exe.Equals("explorer", StringComparison.OrdinalIgnoreCase) && Native.ClassName(h) != "CabinetWClass") return true;
            if (exe.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return true;
            paths.Add(path);
            return true;
        }, IntPtr.Zero);
        return paths;
    }

    /// <summary>Fenêtre principale « normale » d'une app : visible, avec un titre, pas un outil ni un popup.</summary>
    private static bool IsAppWindow(IntPtr h)
    {
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return false;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        if ((Native.GetExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !IgnoredClasses.Contains(Native.ClassName(h));
    }

    private static bool IsCandidate(IntPtr h, string processName)
    {
        if (!IsAppWindow(h)) return false;
        var cls = Native.ClassName(h);

        var path = Native.ProcessPath(h);
        if (path == null || !Path.GetFileNameWithoutExtension(path).Equals(processName, StringComparison.OrdinalIgnoreCase)) return false;

        // explorer.exe possède aussi le bureau et la barre des tâches : seules les vraies fenêtres de dossier comptent.
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return false;
        return true;
    }
}
