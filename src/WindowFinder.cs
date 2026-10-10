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

    public static IntPtr Find(string processName, IntPtr preferred, string titleHint = "")
    {
        if (string.IsNullOrEmpty(processName)) return IntPtr.Zero;
        if (preferred != IntPtr.Zero && IsCandidate(preferred, processName, titleHint)) return preferred;

        // Plusieurs fenêtres possibles (écran de chargement de Discord, popups…) : on prend la plus grande.
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        Native.EnumWindows((h, _) =>
        {
            if (IsCandidate(h, processName, titleHint))
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

    /// <summary>Fenêtres de premier niveau visibles (et non masquées par Windows) d'un processus.</summary>
    public static List<IntPtr> VisibleTopLevelWindows(uint pid)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.ProcessId(h) != pid || !Native.IsWindowVisible(h)) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Le processus de la fenêtre correspond-il à l'app ? (Steam : bulles anciennes réglées sur « steam ».)</summary>
    private static bool SameApp(string windowExe, string processName) =>
        windowExe.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
        (processName.Equals("steam", StringComparison.OrdinalIgnoreCase) && windowExe.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase));

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

    private static bool IsCandidate(IntPtr h, string processName, string titleHint = "")
    {
        if (!IsAppWindow(h)) return false;
        var cls = Native.ClassName(h);

        var path = Native.ProcessPath(h);
        if (path == null || !SameApp(Path.GetFileNameWithoutExtension(path), processName)) return false;

        // explorer.exe possède aussi le bureau et la barre des tâches : seules les vraies fenêtres de dossier comptent.
        if (processName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && cls != "CabinetWClass") return false;
        // App web de navigateur : la bonne fenêtre est celle dont le titre contient le nom de l'app.
        if (titleHint.Length > 0 && !Native.WindowTitle(h).Contains(titleHint, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }
}
