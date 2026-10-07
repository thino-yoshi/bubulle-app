using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Bulles;

/// <summary>Chef d'orchestre : bulles, raccourcis, recherche et bulles-fenêtres.</summary>
public sealed class AppController : IDisposable
{
    private const double FrameGap = 4;

    public AppSettings Settings { get; }

    private readonly LauncherWindow _launcher;
    private readonly HotkeyManager _hotkeys;
    private readonly Forms.NotifyIcon _tray;
    private readonly Native.WinEventProc _foregroundProc;
    private readonly IntPtr _foregroundHook;
    private readonly uint _ourPid = (uint)Environment.ProcessId;
    private readonly Dictionary<BubbleConfig, IntPtr> _lastHwnd = new();
    private readonly Dictionary<BubbleConfig, BubbleFrame> _frames = new();

    private BubbleFrame? _current;
    private IntPtr _returnFocus;
    private SearchWindow? _search;
    private SettingsWindow? _settingsWindow;
    private DateTime _searchClosedAt;
    private IntPtr _lastExternalForeground;
    private Task<CoreWebView2Environment>? _webEnv;
    private bool _busy, _disposed;

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
        SearchWindow.WarmWebIcons();

        // Remet à jour la tâche de démarrage (chemin de l'app, migration depuis l'ancienne clé Run).
        if (Settings.StartWithWindows) ApplyStartup();
    }

    /// <summary>Un seul moteur web partagé : tes connexions (Discord, etc.) sont gardées entre les sessions.</summary>
    private Task<CoreWebView2Environment> WebEnvironment() =>
        _webEnv ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.Dir, "WebData"));

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

    // ---------- Bulles-fenêtres ----------

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

        var returnFocus = _current != null ? _returnFocus : _lastExternalForeground;
        HideCurrent(restoreFocus: false);

        _busy = true;
        _launcher.SetLoading(bubble, true);
        try
        {
            if (bubble.IsWeb) await ShowWeb(bubble);
            else await ShowApp(bubble);
            if (_current != null) _returnFocus = returnFocus;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify($"Impossible d'ouvrir {bubble.Name}.");
        }
        finally
        {
            _busy = false;
            _launcher.SetLoading(bubble, false);
        }
    }

    private BubbleFrame GetFrame(BubbleConfig bubble)
    {
        if (!_frames.TryGetValue(bubble, out var frame))
        {
            frame = new BubbleFrame(this, bubble, bubble.Opacity > 0 ? bubble.Opacity : Settings.DefaultOpacity);
            _frames[bubble] = frame;
        }
        PlaceFrame(frame);
        return frame;
    }

    private async Task ShowWeb(BubbleConfig bubble)
    {
        var frame = await GetReadyFrame(bubble);
        frame.Show();
        Activate(frame);
        var fade = frame.FadeInAsync();
        // La page reste chargée quand la bulle est cachée (Discord reste connecté, le vocal continue).
        await frame.InitWebAsync(await WebEnvironment());
        await fade;
        frame.FocusContent();
    }

    /// <summary>Cadre prêt à apparaître en fondu (attend la fin d'un éventuel fondu de fermeture).</summary>
    private async Task<BubbleFrame> GetReadyFrame(BubbleConfig bubble)
    {
        if (_frames.TryGetValue(bubble, out var existing)) await existing.HideTask;
        var frame = GetFrame(bubble);
        frame.PrepareShow();
        return frame;
    }

    private async Task ShowApp(BubbleConfig bubble)
    {
        // L'app est déjà garée dans sa bulle : elle réapparaît tout de suite, sans relancement ni rechargement.
        if (_frames.TryGetValue(bubble, out var parked))
        {
            await parked.HideTask;
            if (parked.HasParkedNative)
            {
                parked.PrepareShow();
                PlaceFrame(parked);
                parked.Show();
                Activate(parked);
                bool repaint = parked.Unpark();
                parked.FocusContent();
                await Task.Delay(repaint ? 320 : 110);
                await parked.FadeInAsync();
                return;
            }
        }

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
        if (!_launcher.IsOpen) return;
        _lastHwnd[bubble] = hwnd;

        var frame = await GetReadyFrame(bubble);
        frame.Show();
        Activate(frame);
        bool mustRepaint = frame.AttachNative(hwnd);
        frame.FocusContent();
        // Laisse l'app se redessiner à sa nouvelle taille, encore invisible, avant le fondu.
        await Task.Delay(mustRepaint ? 320 : 90);
        await frame.FadeInAsync();
    }

    private void Activate(BubbleFrame frame)
    {
        _current = frame;
        _launcher.SetActive(frame.Bubble);
        frame.Activate();
    }

    /// <summary>Place la bulle-fenêtre à côté de sa bulle, centrée verticalement sur elle.</summary>
    private void PlaceFrame(BubbleFrame frame)
    {
        var b = frame.Bubble;
        var wa = Screens.WorkAreaDip(Settings);
        var (centerY, stripLeft, stripRight) = _launcher.AnchorDip(b);
        double maxWidth = wa.Width - LauncherWindow.StripWidth - 2 * FrameGap;
        double width = Math.Min(b.Width > 0 ? b.Width : wa.Width * Settings.DefaultWidthPct / 100, maxWidth);
        double height = Math.Min(b.Height > 0 ? b.Height : wa.Height * Settings.DefaultHeightPct / 100, wa.Height - 2 * FrameGap);
        frame.Width = width;
        frame.Height = height;
        frame.Left = Settings.IsLeft ? stripRight - 8 : stripLeft + 8 - width;
        frame.Top = Math.Clamp(centerY - height / 2, wa.Top + FrameGap, wa.Bottom - height - FrameGap);
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

    public void HideFrame(BubbleFrame frame)
    {
        if (_current == frame) HideCurrent(restoreFocus: true);
        else frame.Hide();
    }

    private void HideCurrent(bool restoreFocus, bool animate = true)
    {
        var frame = _current;
        if (frame == null) return;
        _current = null;
        _launcher.SetActive(null);

        // Mémorise la taille et l'opacité réglées pour cette bulle.
        var b = frame.Bubble;
        if (frame.ActualWidth > 0)
        {
            b.Width = (int)Math.Round(frame.ActualWidth);
            b.Height = (int)Math.Round(frame.ActualHeight);
        }
        b.Opacity = frame.OpacityPercent == Settings.DefaultOpacity ? 0 : frame.OpacityPercent;
        Settings.Save();

        if (animate)
        {
            // App Windows : garée dans sa bulle (réouverture instantanée). Page web : simplement cachée.
            _ = b.IsWeb ? frame.HideAnimatedAsync() : frame.ParkAnimatedAsync();
        }
        else
        {
            frame.DetachNative(minimize: true);
            frame.Hide();
        }

        var focus = _returnFocus;
        if (restoreFocus && focus != IntPtr.Zero && Native.IsWindow(focus) && !Native.IsIconic(focus))
            Native.SetForegroundWindow(focus);
    }

    private void OnForegroundChanged(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || Native.ProcessId(hwnd) == _ourPid) return;
        if (_current != null && hwnd == _current.NativeHwnd) return;
        // Les fenêtres du moteur web (msedgewebview2) appartiennent à Bulles.
        if (Native.ClassName(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) &&
            Native.ProcessPath(hwnd)?.EndsWith("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) == true) return;
        _lastExternalForeground = hwnd;

        if (_current != null && Settings.AutoHide && !_current.Pinned && !_busy)
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
        double left = Settings.IsLeft ? stripRight - 6 : stripLeft + 6 - width;

        _search = new SearchWindow(IsAlreadyBubble, left, down ? top - 8 : bottom + 8, growsUp: !down);
        _search.Picked += app => _ = AddBubble(app);
        _search.Closed += (_, _) =>
        {
            _search = null;
            _searchClosedAt = DateTime.Now;
        };
        _search.Show();
    }

    private bool IsAlreadyBubble(AppEntry app) =>
        Settings.Bubbles.Any(b => app.IsWeb
            ? b.IsWeb && b.Url.Equals(app.Url, StringComparison.OrdinalIgnoreCase)
            : !b.IsWeb && (b.LaunchPath.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase) ||
                           b.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase)));

    private async Task AddBubble(AppEntry app)
    {
        int n = Settings.Bubbles.Count;
        var bubble = new BubbleConfig
        {
            Kind = app.Kind,
            Url = app.Url,
            Name = app.Name,
            LaunchPath = app.LaunchPath,
            ProcessName = app.ProcessName,
            IconPath = app.IconPath,
            IconIndex = app.IconIndex,
            Hotkey = n < 9 ? $"Alt+{n + 1}" : "",
        };
        if (bubble.IsWeb) bubble.IconPath = await IconLoader.FetchFaviconAsync(bubble.Url);

        Settings.Bubbles.Add(bubble);
        Settings.Save();
        _launcher.Rebuild();
        RegisterHotkeys();
    }

    public void RemoveBubble(BubbleConfig bubble)
    {
        if (_current?.Bubble == bubble) HideCurrent(restoreFocus: true, animate: false);
        if (_frames.Remove(bubble, out var frame))
        {
            frame.DisposeContent();
            frame.Close();
        }
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
            CloseLauncher();
            _launcher.UpdatePlacement();
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

    public void MoveToScreen(string deviceName)
    {
        CloseLauncher();
        Settings.Screen = deviceName;
        Settings.Save();
        _launcher.UpdatePlacement();
    }

    /// <summary>
    /// Lancement au démarrage via une tâche planifiée « à l'ouverture de session » : plus fiable que la clé
    /// Run du registre, que Windows n'a pas exécutée chez toi. L'ancienne entrée Run est supprimée.
    /// </summary>
    private void ApplyStartup()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            run?.DeleteValue("Bulles", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }

        try
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            if (!Settings.StartWithWindows)
            {
                try { folder.DeleteTask("Bulles", 0); }
                catch { /* La tâche n'existait pas. */ }
                return;
            }

            string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "Lance Bulles à l'ouverture de session";
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.ExecutionTimeLimit = "PT0S";
            task.Settings.MultipleInstances = 2; // Ignorer si déjà lancé.
            dynamic trigger = task.Triggers.Create(9); // À l'ouverture de session.
            trigger.UserId = user;
            trigger.Delay = "PT5S";
            dynamic action = task.Actions.Create(0);
            action.Path = Environment.ProcessPath;
            action.Arguments = App.StartupArgument;
            action.WorkingDirectory = AppContext.BaseDirectory;
            task.Principal.UserId = user;
            task.Principal.LogonType = 3; // Session interactive, sans mot de passe.
            task.Principal.RunLevel = 0; // Droits normaux.
            folder.RegisterTaskDefinition("Bulles", task, 6, null, null, 3);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify("Impossible de programmer le lancement au démarrage.");
        }
    }

    private Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var screens = new Forms.ToolStripMenuItem("Écran des bulles");
        menu.Items.Add(screens);
        // Liste reconstruite à chaque ouverture : un écran peut avoir été branché ou débranché.
        menu.Opening += (_, _) =>
        {
            screens.DropDownItems.Clear();
            var all = Forms.Screen.AllScreens;
            var current = Screens.Current(Settings).DeviceName;
            for (int i = 0; i < all.Length; i++)
            {
                var device = all[i].DeviceName;
                var item = new Forms.ToolStripMenuItem(Screens.Label(all[i], i)) { Checked = device == current };
                item.Click += (_, _) => MoveToScreen(device);
                screens.DropDownItems.Add(item);
            }
        };
        menu.Items.Add("Paramètres", null, (_, _) => OpenSettings());
        menu.Items.Add("Quitter Bulles", null, (_, _) => Quit());
        var tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = "Bulles v0.0.2",
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
        if (_disposed) return;
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Rend toujours les fenêtres empruntées dans leur état d'origine.
        HideCurrent(restoreFocus: false, animate: false);
        foreach (var frame in _frames.Values) frame.DisposeContent();
        _hotkeys.Dispose();
        Native.UnhookWinEvent(_foregroundHook);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _tray.Visible = false;
        _tray.Dispose();
    }
}
