using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Bulles;

/// <summary>Chef d'orchestre : bulles, raccourcis, recherche et fenêtres attachées.</summary>
public sealed class AppController : IDisposable
{
    private const int WindowGap = 6;

    public AppSettings Settings { get; }

    private readonly LauncherWindow _launcher;
    private readonly HotkeyManager _hotkeys;
    private readonly Forms.NotifyIcon _tray;
    private readonly Native.WinEventProc _foregroundProc;
    private readonly IntPtr _foregroundHook;
    private readonly uint _ourPid = (uint)Environment.ProcessId;
    private readonly Dictionary<BubbleConfig, IntPtr> _lastHwnd = new();

    private AttachedWindow? _current;
    private SearchWindow? _search;
    private SettingsWindow? _settingsWindow;
    private DateTime _searchClosedAt;
    private IntPtr _lastExternalForeground;
    private bool _busy, _disposed;

    /// <summary>État d'origine d'une fenêtre qu'on a prise en main, pour la rendre intacte.</summary>
    private sealed class AttachedWindow
    {
        public IntPtr Hwnd;
        public BubbleConfig Bubble = null!;
        public Native.WINDOWPLACEMENT Placement;
        public long ExStyle;
        public byte OriginalAlpha = 255;
        public uint OriginalAlphaFlags;
        public int ShownWidth, ShownHeight;
        public IntPtr ReturnFocus;
    }

    public AppController(AppSettings settings)
    {
        Settings = settings;

        _launcher = new LauncherWindow(this);
        _launcher.Show();
        _launcher.UpdatePlacement();
        _launcher.Rebuild();

        _hotkeys = new HotkeyManager(new WindowInteropHelper(_launcher).Handle);
        RegisterHotkeys();

        _tray = CreateTray();

        _foregroundProc = OnForegroundChanged;
        _foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // Prépare la liste des apps en arrière-plan pour que la recherche soit instantanée.
        _ = AppCatalog.GetAsync();
    }

    // ---------- Bulle principale ----------

    public void ToggleLauncher()
    {
        if (_launcher.IsOpen) CloseLauncher();
        else _launcher.Open();
    }

    public void CloseLauncher()
    {
        _search?.SafeClose();
        HideCurrent(restoreFocus: true);
        if (_launcher.IsOpen) _launcher.Close();
    }

    // ---------- Fenêtres attachées ----------

    public async void ToggleApp(BubbleConfig bubble) => await ToggleAppAsync(bubble, fromHotkey: false);

    private async Task ToggleAppAsync(BubbleConfig bubble, bool fromHotkey)
    {
        if (_busy || !Settings.Bubbles.Contains(bubble)) return;
        _search?.SafeClose();

        if (_current?.Bubble == bubble)
        {
            HideCurrent(restoreFocus: true);
            return;
        }

        if (fromHotkey && !_launcher.IsOpen) _launcher.Open();

        var returnFocus = _current?.ReturnFocus ?? _lastExternalForeground;
        HideCurrent(restoreFocus: false);

        _busy = true;
        _launcher.SetLoading(bubble, true);
        try
        {
            _lastHwnd.TryGetValue(bubble, out var preferred);
            var hwnd = WindowFinder.Find(bubble.ProcessName, preferred);
            if (hwnd == IntPtr.Zero)
            {
                if (!Launch(bubble)) return;
                hwnd = await WaitForWindow(bubble);
            }
            if (hwnd == IntPtr.Zero)
            {
                Notify($"Je n'ai pas trouvé la fenêtre de {bubble.Name}.");
                return;
            }
            _lastHwnd[bubble] = hwnd;
            if (_launcher.IsOpen) Attach(bubble, hwnd, returnFocus);
        }
        finally
        {
            _busy = false;
            _launcher.SetLoading(bubble, false);
        }
    }

    private bool Launch(BubbleConfig bubble)
    {
        try
        {
            Process.Start(new ProcessStartInfo(bubble.LaunchPath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify($"Impossible de lancer {bubble.Name}.");
            return false;
        }
    }

    private static async Task<IntPtr> WaitForWindow(BubbleConfig bubble)
    {
        var deadline = DateTime.Now.AddSeconds(25);
        while (DateTime.Now < deadline)
        {
            await Task.Delay(300);
            if (WindowFinder.Find(bubble.ProcessName, IntPtr.Zero) != IntPtr.Zero)
            {
                // Laisse le temps à un éventuel écran de chargement de céder la place à la vraie fenêtre.
                await Task.Delay(1500);
                return WindowFinder.Find(bubble.ProcessName, IntPtr.Zero);
            }
        }
        return IntPtr.Zero;
    }

    private void Attach(BubbleConfig bubble, IntPtr hwnd, IntPtr returnFocus)
    {
        var w = new AttachedWindow
        {
            Hwnd = hwnd,
            Bubble = bubble,
            ReturnFocus = returnFocus,
            ExStyle = Native.GetExStyle(hwnd),
            Placement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() },
        };
        Native.GetWindowPlacement(hwnd, ref w.Placement);
        if ((w.ExStyle & Native.WS_EX_LAYERED) != 0)
            Native.GetLayeredWindowAttributes(hwnd, out _, out w.OriginalAlpha, out w.OriginalAlphaFlags);

        if (Native.IsIconic(hwnd) || w.Placement.showCmd == Native.SW_SHOWMAXIMIZED)
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        var wa = Forms.Screen.PrimaryScreen!.WorkingArea;
        var (centerY, stripLeft, stripRight) = _launcher.AnchorPx(bubble);
        int maxWidth = (int)(wa.Width - (stripRight - stripLeft) - 2 * WindowGap);
        int width = Math.Min(bubble.Width > 0 ? bubble.Width : wa.Width * Settings.DefaultWidthPct / 100, maxWidth);
        int height = Math.Min(bubble.Height > 0 ? bubble.Height : wa.Height * Settings.DefaultHeightPct / 100, wa.Height - 2 * WindowGap);
        int x = Settings.IsLeft ? (int)stripRight - WindowGap : (int)stripLeft + WindowGap - width;
        int y = Math.Clamp((int)(centerY - height / 2.0), wa.Top + WindowGap, wa.Bottom - height - WindowGap);

        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_SHOWWINDOW);
        w.ShownWidth = width;
        w.ShownHeight = height;

        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        int opacity = bubble.Opacity > 0 ? bubble.Opacity : Settings.DefaultOpacity;
        if (opacity < 100)
        {
            Native.SetExStyle(hwnd, w.ExStyle | Native.WS_EX_LAYERED);
            Native.SetLayeredWindowAttributes(hwnd, 0, (byte)(opacity * 255 / 100), Native.LWA_ALPHA);
        }

        _current = w;
        Native.SetForegroundWindow(hwnd);
        _launcher.SetActive(bubble);
    }

    private void HideCurrent(bool restoreFocus)
    {
        var w = _current;
        if (w == null) return;
        _current = null;
        _launcher.SetActive(null);

        if (Native.IsWindow(w.Hwnd))
        {
            // Mémorise la taille si tu as redimensionné la fenêtre.
            if (!Native.IsIconic(w.Hwnd) && Native.GetWindowRect(w.Hwnd, out var r) &&
                (Math.Abs(r.Width - w.ShownWidth) > 2 || Math.Abs(r.Height - w.ShownHeight) > 2))
            {
                w.Bubble.Width = r.Width;
                w.Bubble.Height = r.Height;
                Settings.Save();
            }

            Native.SetWindowPos(w.Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            if ((w.ExStyle & Native.WS_EX_LAYERED) != 0)
                Native.SetLayeredWindowAttributes(w.Hwnd, 0, w.OriginalAlpha, w.OriginalAlphaFlags);
            Native.SetExStyle(w.Hwnd, w.ExStyle);
            int corner = Native.DWMWCP_DEFAULT;
            Native.DwmSetWindowAttribute(w.Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // Remet la position d'origine et réduit la fenêtre pour qu'elle ne gêne pas le jeu.
            var p = w.Placement;
            p.showCmd = Native.SW_SHOWMINNOACTIVE;
            Native.SetWindowPlacement(w.Hwnd, ref p);
        }

        if (restoreFocus && w.ReturnFocus != IntPtr.Zero && w.ReturnFocus != w.Hwnd &&
            Native.IsWindow(w.ReturnFocus) && !Native.IsIconic(w.ReturnFocus))
            Native.SetForegroundWindow(w.ReturnFocus);
    }

    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || Native.ProcessId(hwnd) == _ourPid) return;
        if (_current != null && hwnd == _current.Hwnd) return;
        _lastExternalForeground = hwnd;

        if (_current != null && Settings.AutoHide && !_busy)
            HideCurrent(restoreFocus: false);
    }

    // ---------- Recherche et gestion des bulles ----------

    public void ToggleSearch()
    {
        if (_search != null)
        {
            _search.SafeClose();
            return;
        }
        // Le clic sur « + » ferme d'abord la recherche (perte de focus) : on ne la rouvre pas aussitôt.
        if ((DateTime.Now - _searchClosedAt).TotalMilliseconds < 300) return;

        HideCurrent(restoreFocus: false);
        var (top, bottom, stripLeft, stripRight, down) = _launcher.PlusAnchor();
        const double width = 300;
        double left = Settings.IsLeft ? stripRight - WindowGap : stripLeft + WindowGap - width;

        _search = new SearchWindow(IsAlreadyBubble, left, down ? top - 8 : bottom + 8, growsUp: !down);
        _search.Picked += AddBubble;
        _search.Closed += (_, _) =>
        {
            _search = null;
            _searchClosedAt = DateTime.Now;
        };
        _search.Show();
    }

    private bool IsAlreadyBubble(AppEntry app) =>
        Settings.Bubbles.Any(b => b.LaunchPath.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase) ||
                                  b.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));

    private void AddBubble(AppEntry app)
    {
        int n = Settings.Bubbles.Count;
        Settings.Bubbles.Add(new BubbleConfig
        {
            Name = app.Name,
            LaunchPath = app.LaunchPath,
            ProcessName = app.ProcessName,
            IconPath = app.IconPath,
            IconIndex = app.IconIndex,
            Hotkey = n < 9 ? $"Alt+{n + 1}" : "",
        });
        Settings.Save();
        _launcher.Rebuild();
        RegisterHotkeys();
    }

    public void RemoveBubble(BubbleConfig bubble)
    {
        if (_current?.Bubble == bubble) HideCurrent(restoreFocus: true);
        Settings.Bubbles.Remove(bubble);
        _lastHwnd.Remove(bubble);
        Settings.Save();
        _launcher.Rebuild();
        if (_current != null) _launcher.SetActive(_current.Bubble);
        RegisterHotkeys();
    }

    // ---------- Raccourcis, paramètres, icône de notification ----------

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        var failed = new List<string>();

        var main = Hotkey.Parse(Settings.MainHotkey);
        if (!_hotkeys.Register(main, ToggleLauncher)) failed.Add(main.ToString());

        if (Settings.PerBubbleHotkeys)
        {
            foreach (var b in Settings.Bubbles)
            {
                var hk = Hotkey.Parse(b.Hotkey);
                var bubble = b;
                if (!_hotkeys.Register(hk, () => _ = ToggleAppAsync(bubble, fromHotkey: true))) failed.Add($"{hk} ({b.Name})");
            }
        }

        if (failed.Count > 0)
            Notify("Raccourci déjà utilisé par une autre app : " + string.Join(", ", failed) + ". Change-le dans les paramètres.");
    }

    public void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Coupe les raccourcis globaux pour pouvoir les saisir dans les champs.
        _hotkeys.UnregisterAll();
        bool startupBefore = Settings.StartWithWindows;
        _settingsWindow = new SettingsWindow(Settings);
        _settingsWindow.Saved += () =>
        {
            if (Settings.StartWithWindows != startupBefore) ApplyStartup();
            _launcher.Rebuild();
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            RegisterHotkeys();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplyStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (Settings.StartWithWindows) key.SetValue("Bulles", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("Bulles", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Paramètres", null, (_, _) => OpenSettings());
        menu.Items.Add("Quitter Bulles", null, (_, _) => Quit());
        var tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Bulles v0.0.1",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        return tray;
    }

    private static System.Drawing.Icon MakeTrayIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var c = LauncherWindow.Accent;
            using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(c.R, c.G, c.B));
            using var ring = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f);
            g.FillEllipse(fill, 1, 1, 30, 30);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            g.FillEllipse(System.Drawing.Brushes.White, 11, 11, 10, 10);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void Notify(string message) => _tray.ShowBalloonTip(4000, "Bulles", message, Forms.ToolTipIcon.Info);

    private void OnDisplayChanged(object? sender, EventArgs e) =>
        _launcher.Dispatcher.BeginInvoke(() =>
        {
            HideCurrent(restoreFocus: false);
            _launcher.UpdatePlacement();
        });

    public void Quit()
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Rend toujours la fenêtre empruntée dans son état d'origine (plus au premier plan, plus transparente).
        HideCurrent(restoreFocus: false);
        _hotkeys.Dispose();
        Native.UnhookWinEvent(_foregroundHook);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _tray.Visible = false;
        _tray.Dispose();
    }
}
