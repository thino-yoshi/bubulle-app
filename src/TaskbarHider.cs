using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Bulles;

/// <summary>
/// Option « Cacher la barre des tâches » : tant que Bubulle tourne, la barre Windows (et celles des autres
/// écrans) est masquée et ne réapparaît plus en bas de l'écran. Elle passe en « masquage automatique »
/// le temps que Bubulle tourne, pour que les apps profitent de toute la hauteur, puis tout est remis
/// comme avant à la fermeture (ou au lancement suivant si Bubulle s'est arrêté brutalement).
/// </summary>
public sealed class TaskbarHider : IDisposable
{
    private const int SW_HIDE = 0, SW_SHOWNA = 8;
    private const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    private const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Native.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? className, string? title);
    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _guard = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public bool Active { get; private set; }

    public TaskbarHider(AppSettings settings)
    {
        _settings = settings;
        // La barre revient d'elle-même (redémarrage de l'explorateur, écran branché…) : on la recache.
        _guard.Tick += (_, _) => HideBars();
    }

    /// <summary>Au démarrage : Bubulle s'était arrêté sans rendre la barre ? On la rend avant tout.</summary>
    public void RecoverIfNeeded()
    {
        if (_settings.TaskbarHiddenByBubulle) Restore();
    }

    public void Apply()
    {
        if (_settings.HideTaskbar) Hide();
        else if (Active || _settings.TaskbarHiddenByBubulle) Restore();
    }

    private void Hide()
    {
        if (!Active)
        {
            if (!_settings.TaskbarHiddenByBubulle)
            {
                // On retient le réglage d'origine (masquage auto ou non) pour le remettre ensuite.
                _settings.TaskbarWasAutoHide = (GetState() & ABS_AUTOHIDE) != 0;
                _settings.TaskbarHiddenByBubulle = true;
                _settings.Save();
            }
            SetState(ABS_AUTOHIDE);
            Active = true;
        }
        HideBars();
        _guard.Start();
    }

    /// <summary>Remet la barre comme avant (visible, et son réglage de masquage automatique d'origine).</summary>
    public void Restore()
    {
        _guard.Stop();
        foreach (var bar in Bars()) ShowWindow(bar, SW_SHOWNA);
        if (_settings.TaskbarHiddenByBubulle)
        {
            SetState(_settings.TaskbarWasAutoHide ? ABS_AUTOHIDE : ABS_ALWAYSONTOP);
            _settings.TaskbarHiddenByBubulle = false;
            _settings.Save();
        }
        Active = false;
    }

    private static void HideBars()
    {
        foreach (var bar in Bars())
            if (IsWindowVisible(bar)) ShowWindow(bar, SW_HIDE);
    }

    /// <summary>La barre principale et celles des autres écrans.</summary>
    private static List<IntPtr> Bars()
    {
        var list = new List<IntPtr>();
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) list.Add(main);
        var name = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            name.Clear();
            if (GetClassName(h, name, name.Capacity) > 0 && name.ToString() == "Shell_SecondaryTrayWnd") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static int GetState()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    private static void SetState(int state)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)state };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    public void Dispose()
    {
        if (Active || _settings.TaskbarHiddenByBubulle) Restore();
    }
}
