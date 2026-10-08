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
    /// <summary>Bulles en mini-lecteur ou traversables : elles restent affichées, indépendamment de la cascade.</summary>
    private readonly HashSet<BubbleFrame> _floating = new();
    private readonly Dictionary<BubbleConfig, int> _badges = new();
    private readonly System.Windows.Threading.DispatcherTimer _badgeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _clickThroughTipShown;
    private IntPtr _returnFocus;
    private SearchWindow? _search;
    private SettingsWindow? _settingsWindow;
    private DateTime _searchClosedAt;
    private IntPtr _lastExternalForeground;
    private Task<CoreWebView2Environment>? _webEnv;
    private bool _busy, _disposed;

    public Sounds Sounds { get; }

    public AppController(AppSettings settings)
    {
        Settings = settings;
        Sounds = new Sounds(settings);

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

        _badgeTimer.Tick += (_, _) => PollAppBadges();
        _badgeTimer.Start();

        // Apps de bureau : Windows prévient quand une fenêtre fait clignoter son bouton (nouveau message).
        var launcherHwnd = new WindowInteropHelper(_launcher).Handle;
        _shellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
        Native.RegisterShellHookWindow(launcherHwnd);
        HwndSource.FromHwnd(launcherHwnd)!.AddHook(ShellHook);
        if (Settings.PreloadWeb) _ = PreloadWebBubbles();
    }

    // ---------- Pastilles de notification ----------

    private static readonly System.Text.RegularExpressions.Regex BadgePattern = new(@"^\s*\((\d+)\+?\)");

    /// <summary>« (3) Discord », « (12) WhatsApp » : le nombre de non-lus que les sites mettent dans leur titre.</summary>
    public void OnContentTitle(BubbleConfig bubble, string title)
    {
        var m = BadgePattern.Match(title ?? "");
        int count = m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
        if (_badges.TryGetValue(bubble, out var old) && old == count) return;
        _badges[bubble] = count;
        RefreshBadge(bubble);
    }

    /// <summary>Affiche le nombre de non-lus, sinon un point si l'app a signalé de l'activité ; le total va sur la bulle principale.</summary>
    private void RefreshBadge(BubbleConfig bubble)
    {
        int count = _badges.GetValueOrDefault(bubble);
        int shown = count > 0 ? count : _activity.Contains(bubble) ? LauncherWindow.DotBadge : 0;
        int total = Settings.Bubbles.Sum(b => Math.Max(0, _badges.GetValueOrDefault(b)));
        if (total == 0 && Settings.Bubbles.Any(_activity.Contains)) total = LauncherWindow.DotBadge;
        _launcher.SetBadge(bubble, shown, total);
    }

    private readonly HashSet<BubbleConfig> _activity = new();
    private readonly uint _shellHookMessage;

    private IntPtr ShellHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _shellHookMessage && wParam.ToInt32() == Native.HSHELL_FLASH) OnAppFlash(lParam);
        return IntPtr.Zero;
    }

    /// <summary>Une app de bureau fait clignoter son bouton (Discord à un nouveau message) : point rouge sur sa bulle.</summary>
    private void OnAppFlash(IntPtr window)
    {
        var bubble = AppBubbleOf(window);
        if (bubble == null || _current?.Bubble == bubble) return;
        if (_activity.Add(bubble)) RefreshBadge(bubble);
    }

    private void ClearActivity(BubbleConfig bubble)
    {
        if (_activity.Remove(bubble)) RefreshBadge(bubble);
    }

    private BubbleConfig? AppBubbleOf(IntPtr window)
    {
        if (window == IntPtr.Zero || Native.ProcessId(window) == _ourPid) return null;
        var path = Native.ProcessPath(window);
        if (path == null) return null;
        var exe = Path.GetFileNameWithoutExtension(path);
        return Settings.Bubbles.FirstOrDefault(b => b.Kind == "App" && b.ProcessName.Equals(exe, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Apps Windows : même principe, avec le titre de leur fenêtre.</summary>
    private void PollAppBadges()
    {
        foreach (var b in Settings.Bubbles.Where(b => b.Kind == "App"))
        {
            var hwnd = _frames.TryGetValue(b, out var f) && f.NativeHwnd != IntPtr.Zero ? f.NativeHwnd
                : _lastHwnd.TryGetValue(b, out var h) ? h : IntPtr.Zero;
            if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd)) OnContentTitle(b, Native.WindowTitle(hwnd));
        }
    }

    /// <summary>Charge les sites des bulles en arrière-plan pour que leurs pastilles marchent dès le démarrage.</summary>
    private async Task PreloadWebBubbles()
    {
        await Task.Delay(3000);
        foreach (var b in Settings.Bubbles.Where(b => b.IsWeb).ToList())
        {
            if (_disposed || _frames.ContainsKey(b)) continue;
            try { await GetFrame(b).PreloadWebAsync(await WebEnvironment()); }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    // ---------- Mini-lecteur et mode traversable ----------

    public void ToggleMini(BubbleFrame frame)
    {
        if (!frame.IsMini)
        {
            MakeFloating(frame);
            frame.EnterMini(MiniRect());
            return;
        }
        RememberMiniRect(frame);
        frame.ExitMini();
        if (!frame.ClickThrough) Reattach(frame);
    }

    public void ToggleClickThrough(BubbleFrame frame)
    {
        if (!frame.ClickThrough)
        {
            MakeFloating(frame);
            frame.SetClickThrough(true);
            if (!_clickThroughTipShown)
            {
                _clickThroughTipShown = true;
                Notify($"{frame.Bubble.Name} est traversable : clique sur sa bulle pour la reprendre en main.");
            }
            return;
        }
        frame.SetClickThrough(false);
        if (!frame.IsMini) Reattach(frame);
    }

    /// <summary>La bulle quitte la cascade et reste affichée par-dessus le jeu ; le focus revient au jeu.</summary>
    private void MakeFloating(BubbleFrame frame)
    {
        _floating.Add(frame);
        if (_current != frame) return;
        _current = null;
        _launcher.SetActive(null);
        if (_returnFocus != IntPtr.Zero && Native.IsWindow(_returnFocus)) Native.SetForegroundWindow(_returnFocus);
    }

    /// <summary>Remet une bulle flottante à côté de sa bulle, comme une bulle ouverte normalement.</summary>
    private void Reattach(BubbleFrame frame)
    {
        _floating.Remove(frame);
        if (_current != null && _current != frame) HideCurrent(restoreFocus: false);
        if (!_launcher.IsOpen) _launcher.Open();
        PlaceFrame(frame);
        _returnFocus = _lastExternalForeground;
        Activate(frame);
        frame.FocusContent();
    }

    /// <summary>Clic sur la bulle d'une fenêtre flottante : on la reprend en main.</summary>
    private void ReclaimFloating(BubbleFrame frame)
    {
        if (frame.ClickThrough) frame.SetClickThrough(false);
        if (frame.IsMini)
        {
            RememberMiniRect(frame);
            frame.ExitMini();
        }
        Reattach(frame);
    }

    private Rect MiniRect()
    {
        var wa = Screens.WorkAreaDip(Settings);
        double w = Settings.MiniWidth > 0 ? Settings.MiniWidth : 420, h = Settings.MiniHeight > 0 ? Settings.MiniHeight : 270;
        var saved = new Rect(Settings.MiniLeft, Settings.MiniTop, w, h);
        if (Settings.MiniWidth > 0 && wa.IntersectsWith(saved)) return saved;
        // Par défaut : en bas, du côté opposé aux bulles.
        double left = Settings.IsLeft ? wa.Right - w - 16 : wa.Left + 16;
        return new Rect(left, wa.Bottom - h - 16, w, h);
    }

    private void RememberMiniRect(BubbleFrame frame)
    {
        Settings.MiniLeft = frame.Left;
        Settings.MiniTop = frame.Top;
        Settings.MiniWidth = frame.ActualWidth;
        Settings.MiniHeight = frame.ActualHeight;
        Settings.Save();
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

        if (_frames.TryGetValue(bubble, out var floating) && _floating.Contains(floating))
        {
            if (fromHotkey && !_launcher.IsOpen) _launcher.Open();
            ReclaimFloating(floating);
            return;
        }

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
            ClearActivity(bubble);
            if (bubble.IsWeb) await ShowWeb(bubble);
            else if (bubble.IsMixer) await ShowMixer(bubble);
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

    private async Task ShowMixer(BubbleConfig bubble)
    {
        var frame = await GetReadyFrame(bubble);
        frame.InitMixer();
        frame.Show();
        Activate(frame);
        await frame.FadeInAsync();
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
        if (_floating.Remove(frame))
        {
            // Fermeture d'un mini-lecteur ou d'une bulle traversable.
            if (frame.IsMini) RememberMiniRect(frame);
            frame.SetClickThrough(false);
            frame.ExitMini();
            var b = frame.Bubble;
            _ = b.IsWeb || b.IsMixer ? frame.HideAnimatedAsync() : frame.ParkAnimatedAsync();
            return;
        }
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
            _ = b.IsWeb || b.IsMixer ? frame.HideAnimatedAsync() : frame.ParkAnimatedAsync();
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
        // Tu regardes l'app : son point d'activité disparaît.
        if (AppBubbleOf(hwnd) is { } viewed) ClearActivity(viewed);
        if (_current != null && hwnd == _current.NativeHwnd) return;
        if (_floating.Any(f => f.NativeHwnd == hwnd)) return;
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
        _search.Picked += app =>
        {
            if (app.Kind == "Browse") _launcher.Dispatcher.BeginInvoke(BrowseForApp);
            else if (app.Kind == "AddSite") _launcher.Dispatcher.BeginInvoke(AskForWebSite);
            else _ = AddBubble(app);
        };
        _search.Closed += (_, _) =>
        {
            _search = null;
            _searchClosedAt = DateTime.Now;
        };
        _search.Show();
    }

    /// <summary>Ajoute n'importe quel programme (.exe) ou raccourci du PC, même absent du menu Démarrer.</summary>
    private void BrowseForApp()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une application pour une nouvelle bulle",
            Filter = "Programmes et raccourcis (*.exe, *.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog() != true) return;

        var app = AppCatalog.FromFile(dialog.FileName);
        if (app == null)
        {
            Notify("Ce raccourci ne mène pas à un programme.");
            return;
        }
        if (IsAlreadyBubble(app))
        {
            Notify($"{app.Name} a déjà sa bulle.");
            return;
        }
        _ = AddBubble(app);
    }

    /// <summary>Ajoute ta propre page web (nom + adresse) comme bulle.</summary>
    private void AskForWebSite()
    {
        var dialog = new WebSiteDialog();
        dialog.ShowDialog();
        var site = dialog.Result;
        if (site == null) return;
        if (IsAlreadyBubble(site))
        {
            Notify($"{site.Name} a déjà sa bulle.");
            return;
        }
        _ = AddBubble(site);
    }

    private bool IsAlreadyBubble(AppEntry app) =>
        Settings.Bubbles.Any(b => app.Kind == "Mixer" ? b.IsMixer : app.IsWeb
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
            Sounds.Reload();
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
        _badgeTimer.Stop();
        Native.DeregisterShellHookWindow(new WindowInteropHelper(_launcher).Handle);
        Native.UnhookWinEvent(_foregroundHook);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _tray.Visible = false;
        _tray.Dispose();
    }
}
