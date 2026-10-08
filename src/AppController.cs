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
    private DateTime _searchClosedAt;
    private IntPtr _lastExternalForeground;
    private Task<CoreWebView2Environment>? _webEnv;
    private bool _busy, _disposed;

    public Sounds Sounds { get; }

    public AppController(AppSettings settings)
    {
        Settings = settings;
        Sounds = new Sounds(settings);
        AppCursor.Apply(settings);

        // Un lanceur est proposé de base (une seule fois : tu peux le retirer ensuite).
        if (!Settings.DefaultLauncherAdded)
        {
            Settings.Bubbles.Add(NewLauncher("Lanceur"));
            Settings.DefaultLauncherAdded = true;
            Settings.Save();
        }

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
        _downloadTimer.Tick += async (_, _) => await PollDownloads();
        _downloadTimer.Start();

        // Curseur « seulement Bubulle » : aussi au-dessus des vraies apps rangées dans les bulles.
        _cursorTimer.Tick += (_, _) => TrackCursorOverHostedApps();
        _cursorTimer.Start();
        _badgeTimer.Start();

        // Apps de bureau : Windows prévient quand une fenêtre fait clignoter son bouton (nouveau message).
        var launcherHwnd = new WindowInteropHelper(_launcher).Handle;
        _shellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
        Native.RegisterShellHookWindow(launcherHwnd);
        HwndSource.FromHwnd(launcherHwnd)!.AddHook(ShellHook);
        if (Settings.PreloadWeb) _ = PreloadWebBubbles();
        _ = CheckForUpdate(TimeSpan.FromSeconds(20), quiet: true);
        // Bubulle reste souvent ouvert des jours : on revérifie toutes les 3 heures.
        _updateTimer.Tick += async (_, _) => { if (Updater.Ready == null) await CheckForUpdate(TimeSpan.Zero, quiet: true); };
        _updateTimer.Start();
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
            else if (bubble.IsLauncher) await ShowLauncherBubble(bubble);
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

    // ---------- Lanceurs (répertoires d'apps) ----------

    private static BubbleConfig NewLauncher(string name) => new() { Kind = "Launcher", Name = name, Glyph = BubbleGlyphs.Grid };

    private async Task ShowLauncherBubble(BubbleConfig bubble)
    {
        var frame = await GetReadyFrame(bubble);
        frame.InitLauncher();
        frame.Show();
        Activate(frame);
        frame.FocusLauncherSearch();
        await frame.FadeInAsync();
    }

    /// <summary>Lance l'app normalement (sa propre fenêtre, hors bulle), puis referme le lanceur.</summary>
    public void LaunchFromLauncher(BubbleConfig launcher, LauncherApp app)
    {
        try
        {
            var target = app.Kind == "Web" ? app.Url : app.LaunchPath;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify($"Impossible de lancer {app.Name}.");
            return;
        }
        if (_frames.TryGetValue(launcher, out var frame))
        {
            // L'app qui démarre doit prendre le premier plan, pas le jeu d'avant.
            if (_current == frame) _returnFocus = IntPtr.Zero;
            HideFrame(frame);
        }
        CloseLauncher();
    }

    public void AddLauncher()
    {
        int count = Settings.Bubbles.Count(b => b.IsLauncher);
        var launcher = NewLauncher(count == 0 ? "Lanceur" : $"Lanceur {count + 1}");
        Settings.Bubbles.Add(launcher);
        Settings.Save();
        _launcher.Rebuild(replayOpen: false);
        CustomizeBubble(launcher);
    }

    /// <summary>Ajoute une app (ou un site) dans un lanceur, avec la même recherche que la bulle « + ».</summary>
    public void AddToLauncher(BubbleConfig launcher)
    {
        _search?.SafeClose();
        const double width = 300;
        double left, top;
        if (_frames.TryGetValue(launcher, out var frame) && frame.IsVisible)
        {
            left = frame.Left + (frame.ActualWidth - width) / 2;
            top = frame.Top + 50;
        }
        else
        {
            var (centerY, stripLeft, stripRight) = _launcher.AnchorDip(launcher);
            left = Settings.IsLeft ? stripRight - 6 : stripLeft + 6 - width;
            top = centerY - 20;
        }

        bool AlreadyIn(AppEntry a) => launcher.Apps.Any(x => a.IsWeb
            ? x.Url.Equals(a.Url, StringComparison.OrdinalIgnoreCase)
            : x.LaunchPath.Equals(a.LaunchPath, StringComparison.OrdinalIgnoreCase));

        _search = new SearchWindow(AlreadyIn, left, top, growsUp: false) { ShowTools = false };
        _search.Picked += app => _launcher.Dispatcher.BeginInvoke(async () =>
        {
            AppEntry? chosen = app;
            if (app.Kind == "Browse") chosen = PickProgramFile();
            else if (app.Kind == "AddSite")
            {
                var dialog = new WebSiteDialog();
                dialog.ShowDialog();
                chosen = dialog.Result;
            }
            if (chosen == null || AlreadyIn(chosen)) return;

            var entry = new LauncherApp
            {
                Kind = chosen.IsWeb ? "Web" : "App",
                Name = chosen.Name,
                LaunchPath = chosen.LaunchPath,
                Url = chosen.Url,
                IconPath = chosen.IconPath,
                IconIndex = chosen.IconIndex,
            };
            if (chosen.IsWeb) entry.IconPath = await IconLoader.FetchFaviconAsync(chosen.Url);
            launcher.Apps.Add(entry);
            Settings.Save();
            if (_frames.TryGetValue(launcher, out var f)) f.RefreshLauncher();
        });
        _search.Closed += (_, _) =>
        {
            _search = null;
            _searchClosedAt = DateTime.Now;
        };
        _search.Show();
    }

    /// <summary>Range une app d'un lanceur dans un autre (clic droit → Déplacer vers).</summary>
    public void MoveLauncherApp(LauncherApp app, BubbleConfig from, BubbleConfig to)
    {
        if (!from.Apps.Remove(app)) return;
        bool already = to.Apps.Any(x => app.Kind == "Web"
            ? x.Url.Equals(app.Url, StringComparison.OrdinalIgnoreCase)
            : x.LaunchPath.Equals(app.LaunchPath, StringComparison.OrdinalIgnoreCase));
        if (!already) to.Apps.Add(app);
        Settings.Save();
        foreach (var b in new[] { from, to })
            if (_frames.TryGetValue(b, out var f)) f.RefreshLauncher();
        Notify(already ? $"{app.Name} était déjà dans {to.Name}." : $"{app.Name} est maintenant dans {to.Name}.");
    }

    /// <summary>« Personnaliser… » : nom et logo de n'importe quelle bulle.</summary>
    public void CustomizeBubble(BubbleConfig bubble)
    {
        var dialog = new CustomizeDialog(bubble);
        dialog.ShowDialog();
        if (!dialog.Saved) return;
        bubble.Name = dialog.ResultName;
        bubble.Glyph = dialog.ResultGlyph;
        Settings.Save();
        _launcher.Rebuild(replayOpen: false);
        if (_current != null) _launcher.SetActive(_current.Bubble);
        if (_frames.TryGetValue(bubble, out var frame)) frame.UpdateLook();
    }

    public void RevealFile(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { App.Log(ex); }
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
        var (centerY, stripLeft, stripRight) = b == SettingsBubble ? _launcher.SettingsAnchorDip() : _launcher.AnchorDip(b);
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
            _ = b.IsWindowApp ? frame.ParkAnimatedAsync() : frame.HideAnimatedAsync();
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
        if (frame.Bubble == SettingsBubble) _launcher.HideSettingsBubble();

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
            _ = b.IsWindowApp ? frame.ParkAnimatedAsync() : frame.HideAnimatedAsync();
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
    /// <summary>Sélecteur de fichiers « un programme ou un raccourci » ; null si annulé ou invalide.</summary>
    private AppEntry? PickProgramFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une application",
            Filter = "Programmes et raccourcis (*.exe, *.lnk)|*.exe;*.lnk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            DereferenceLinks = false,
        };
        if (dialog.ShowDialog() != true) return null;

        var app = AppCatalog.FromFile(dialog.FileName);
        if (app == null) Notify("Ce raccourci ne mène pas à un programme.");
        return app;
    }

    private void BrowseForApp()
    {
        var app = PickProgramFile();
        if (app == null) return;
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
        app.Kind != "Launcher" && Settings.Bubbles.Any(b => app.Kind == "Mixer" ? b.IsMixer : app.IsWeb
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
        if (bubble.IsLauncher)
        {
            // Plusieurs lanceurs : « Lanceur », « Lanceur 2 »… (renommables avec « Personnaliser… »).
            int count = Settings.Bubbles.Count(b => b.IsLauncher);
            bubble.Name = count == 0 ? "Lanceur" : $"Lanceur {count + 1}";
            bubble.Glyph = BubbleGlyphs.Grid;
        }
        if (bubble.IsWeb) bubble.IconPath = await IconLoader.FetchFaviconAsync(bubble.Url);

        Settings.Bubbles.Add(bubble);
        Settings.Save();
        _launcher.Rebuild();
        RegisterHotkeys();
    }

    /// <summary>Range une bulle à une autre place dans la colonne (appui long puis glisser).</summary>
    public void MoveBubble(BubbleConfig bubble, int newIndex)
    {
        var list = Settings.Bubbles;
        int oldIndex = list.IndexOf(bubble);
        newIndex = Math.Clamp(newIndex, 0, list.Count - 1);
        if (oldIndex >= 0 && oldIndex != newIndex)
        {
            list.RemoveAt(oldIndex);
            list.Insert(newIndex, bubble);
            Settings.Save();
        }
        _launcher.Rebuild(replayOpen: false);
        if (_current != null)
        {
            _launcher.SetActive(_current.Bubble);
            // La bulle ouverte a peut-être changé de place : sa fenêtre la suit.
            PlaceFrame(_current);
        }
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

    /// <summary>Bulle « Paramètres » : ne fait pas partie de ta liste, elle se déploie au-dessus de la bulle principale.</summary>
    public BubbleConfig SettingsBubble { get; } = new() { Kind = "Settings", Name = "Paramètres", Glyph = "", Width = 560, Height = 760 };

    public async void OpenSettings()
    {
        if (_busy) return;
        _search?.SafeClose();
        if (_current?.Bubble == SettingsBubble)
        {
            _current.Activate();
            return;
        }
        var returnFocus = _current != null ? _returnFocus : _lastExternalForeground;
        HideCurrent(restoreFocus: false);
        _launcher.ShowSettingsBubble();

        var frame = await GetReadyFrame(SettingsBubble);
        frame.InitSettings();
        frame.Show();
        Activate(frame);
        _returnFocus = returnFocus;
        await frame.FadeInAsync();
    }

    /// <summary>Clic sur la bulle engrenage : ouvre ou referme les paramètres.</summary>
    public void ToggleSettings()
    {
        if (_current?.Bubble == SettingsBubble) HideCurrent(restoreFocus: true);
        else OpenSettings();
    }

    public bool CurrentIsSettings => _current?.Bubble == SettingsBubble;

    // Raccourcis coupés pendant qu'on en saisit un dans les paramètres, puis remis.
    public void SuspendHotkeys() => _hotkeys.UnregisterAll();
    public void ResumeHotkeys() => RegisterHotkeys();

    public void ApplyStartupSetting() => ApplyStartup();

    public void MoveToScreen(string deviceName)
    {
        bool settingsOpen = _current?.Bubble == SettingsBubble;
        CloseLauncher();
        Settings.Screen = deviceName;
        Settings.Save();
        _launcher.UpdatePlacement();
        // Changé depuis les paramètres : ils se rouvrent sur le nouvel écran.
        if (settingsOpen) _launcher.Dispatcher.BeginInvoke(OpenSettings, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Lancement au démarrage via une tâche planifiée « à l'ouverture de session » : plus fiable que la clé
    /// Run du registre, que Windows n'a pas exécutée chez toi. L'ancienne entrée Run est supprimée.
    /// </summary>
    private const string StartupTaskName = "Bubulle";

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
            // Ancienne tâche, du temps où l'app s'appelait « Bulles ».
            try { folder.DeleteTask("Bulles", 0); }
            catch { /* Elle n'existait pas. */ }
            if (!Settings.StartWithWindows)
            {
                try { folder.DeleteTask(StartupTaskName, 0); }
                catch { /* La tâche n'existait pas. */ }
                return;
            }

            string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            dynamic task = service.NewTask(0);
            task.RegistrationInfo.Description = "Lance Bubulle à l'ouverture de session";
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
            folder.RegisterTaskDefinition(StartupTaskName, task, 6, null, null, 3);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Notify("Impossible de programmer le lancement au démarrage.");
        }
    }

    // ---------- Mises à jour (GitHub) ----------

    private Forms.ToolStripMenuItem? _updateMenuItem;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(3) };
    private bool _updateBalloonShown;

    /// <summary>Texte d'état pour les paramètres.</summary>
    public string UpdateStatus { get; private set; } = "";

    /// <summary>Cherche une nouvelle version sur GitHub, la télécharge en silence, puis propose de redémarrer.</summary>
    public async Task CheckForUpdate(TimeSpan delay, bool quiet)
    {
        await Task.Delay(delay);
        if (_disposed) return;
        UpdateStatus = "Recherche d'une mise à jour…";
        try
        {
            var version = await Updater.CheckAndDownloadAsync();
            if (version == null)
            {
                UpdateStatus = $"Bubulle est à jour (v{Updater.Current}).";
                if (!quiet) Notify(UpdateStatus);
                return;
            }
            UpdateStatus = $"Bubulle v{version} est prête : redémarre pour l'installer.";
            if (_updateMenuItem == null)
            {
                _updateMenuItem = new Forms.ToolStripMenuItem($"Redémarrer pour mettre à jour (v{version})", null, (_, _) => InstallUpdateNow());
                _tray.ContextMenuStrip!.Items.Insert(0, _updateMenuItem);
            }
            _updateBalloonShown = true;
            _tray.ShowBalloonTip(8000, "Bubulle", $"La version {version} est prête. Clique ici pour redémarrer et l'installer (sinon, au prochain lancement).", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // Pas d'internet, GitHub indisponible… On réessaiera plus tard.
            App.Log(ex);
            Updater.Log("Vérification impossible : " + ex.Message);
            UpdateStatus = "Impossible de vérifier les mises à jour pour l'instant.";
            if (!quiet) Notify(UpdateStatus);
        }
    }

    /// <summary>Quitte Bubulle (en rendant les fenêtres gardées) et installe la mise à jour, qui relance l'app.</summary>
    public void InstallUpdateNow()
    {
        if (Updater.Ready == null) return;
        Dispose();
        if (Updater.StartInstall()) Application.Current.Shutdown();
    }

    // ---------- Curseur au-dessus des apps des bulles ----------

    private readonly System.Windows.Threading.DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT pt);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT pt);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);

    private void TrackCursorOverHostedApps()
    {
        bool over = false;
        if (Settings.UseCustomCursor && !Settings.CursorEverywhere && AppCursor.Current != null && GetCursorPos(out var p))
        {
            var root = GetAncestor(WindowFromPoint(p), 2);
            over = root != IntPtr.Zero && ((_current != null && _current.NativeHwnd == root) || _floating.Any(f => f.NativeHwnd == root));
        }
        AppCursor.TemporarySystemCursor(over);
    }

    // ---------- Jauges de téléchargement ----------

    private readonly DownloadMonitor _downloads = new();
    private readonly System.Windows.Threading.DispatcherTimer _downloadTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, string> _launcherAppProcess = new(StringComparer.OrdinalIgnoreCase);
    private bool _pollingDownloads;

    /// <summary>Lit les téléchargements en arrière-plan, puis remplit l'anneau des bulles concernées.</summary>
    private async Task PollDownloads()
    {
        if (_pollingDownloads || _disposed) return;
        _pollingDownloads = true;
        try
        {
            var byProcess = await Task.Run(_downloads.Poll);
            var perBubble = new Dictionary<BubbleConfig, double>();
            foreach (var b in Settings.Bubbles)
            {
                // Bulle de l'app elle-même (Chrome, Steam…), ou lanceur qui contient l'app.
                var processes = b.IsLauncher ? b.Apps.Select(LauncherAppProcess) : new[] { b.ProcessName };
                // Un pourcentage connu l'emporte sur « en cours, total inconnu ».
                double? best = null;
                foreach (var p in processes)
                {
                    if (string.IsNullOrEmpty(p) || !byProcess.TryGetValue(p, out var v)) continue;
                    best = best == null ? v : Math.Max(best.Value, v);
                }
                if (best != null) perBubble[b] = best.Value;
            }
            _launcher.SetProgress(perBubble);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _pollingDownloads = false;
        }
    }

    /// <summary>Nom du programme d'une app de lanceur (lu une fois depuis son raccourci).</summary>
    private string LauncherAppProcess(LauncherApp app)
    {
        if (app.Kind == "Web" || string.IsNullOrEmpty(app.LaunchPath)) return "";
        if (_launcherAppProcess.TryGetValue(app.LaunchPath, out var cached)) return cached;
        var process = AppCatalog.FromFile(app.LaunchPath)?.ProcessName ?? "";
        _launcherAppProcess[app.LaunchPath] = process;
        return process;
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
        menu.Items.Add("Quitter Bubulle", null, (_, _) => Quit());
        var tray = new Forms.NotifyIcon
        {
            Icon = MakeTrayIcon(),
            Text = $"Bubulle v{typeof(App).Assembly.GetName().Version?.ToString(3)}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => OpenSettings();
        // Clic sur la notification « nouvelle version prête » : on installe.
        tray.BalloonTipClicked += (_, _) => { if (_updateBalloonShown) InstallUpdateNow(); };
        return tray;
    }

    private static System.Drawing.Icon MakeTrayIcon()
    {
        // L'icône de Bubulle, à la taille de la zone de notification.
        var file = Path.Combine(AppContext.BaseDirectory, "bubulle.ico");
        if (File.Exists(file))
        {
            try { return new System.Drawing.Icon(file, Forms.SystemInformation.SmallIconSize); }
            catch (Exception ex) { App.Log(ex); }
        }

        // Secours : une bulle dessinée.
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

    /// <summary>Petite notification Windows de Bubulle (utilisée par les paramètres).</summary>
    public void Announce(string message) => Notify(message);

    private void Notify(string message)
    {
        _updateBalloonShown = false;
        _tray.ShowBalloonTip(4000, "Bubulle", message, Forms.ToolTipIcon.Info);
    }

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
        _downloadTimer.Stop();
        _cursorTimer.Stop();
        AppCursor.TemporarySystemCursor(false);
        Native.DeregisterShellHookWindow(new WindowInteropHelper(_launcher).Handle);
        Native.UnhookWinEvent(_foregroundHook);
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _tray.Visible = false;
        _tray.Dispose();
    }
}
