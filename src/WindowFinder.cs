using System;
using System.Collections.Generic;
using System.IO;

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

    private static bool IsCandidate(IntPtr h, string processName)
    {
        if (!Native.IsWindow(h) || !Native.IsWindowVisible(h)) return false;
        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        if ((Native.GetExStyle(h) & Native.WS_EX_TOOLWINDOW) != 0) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        var cls = Native.ClassName(h);
        if (IgnoredClasses.Contains(cls)) return false;

        var path = Native.ProcessPath(h);
        if (path == null || !Path.GetFileNameWithoutExtension(path).Equals(processName, StringComparison.OrdinalIgnoreCase)) return false;

        // explorer.exe possède aussi le bureau et la barre des tâches : seules les vraies fenêtres de dossier comptent.
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return false;
        return true;
    }
}
