using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Bulles;

/// <summary>Écran choisi pour les bulles et conversions pixels physiques ↔ unités WPF.</summary>
public static class Screens
{
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>L'écran mémorisé dans les paramètres, ou le principal s'il n'est plus branché.</summary>
    public static Forms.Screen Current(AppSettings s) =>
        Forms.Screen.AllScreens.FirstOrDefault(sc => sc.DeviceName == s.Screen) ?? Forms.Screen.PrimaryScreen!;

    public static double Scale(Forms.Screen screen)
    {
        var center = new Native.POINT
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2,
        };
        var monitor = MonitorFromPoint(center, 2);
        return GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>Zone de travail (sans la barre des tâches) de l'écran choisi, en unités WPF.</summary>
    public static Rect WorkAreaDip(AppSettings s)
    {
        var screen = Current(s);
        double k = Scale(screen);
        var wa = screen.WorkingArea;
        return new Rect(wa.Left / k, wa.Top / k, wa.Width / k, wa.Height / k);
    }

    public static string Label(Forms.Screen screen, int index) =>
        $"Écran {index + 1}{(screen.Primary ? " (principal)" : "")} · {screen.Bounds.Width}×{screen.Bounds.Height}";
}
