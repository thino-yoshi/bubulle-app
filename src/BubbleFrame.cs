using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Bulles;

/// <summary>
/// La « bulle-fenêtre » : un cadre Bulles (coins arrondis, bord lumineux, barre avec opacité et épingle)
/// qui contient soit une page web intégrée, soit la vraie fenêtre d'une app Windows sans sa barre de titre.
/// </summary>
public sealed class BubbleFrame : Window
{
    private const double HeaderHeight = 38, Inset = 5, FrameRadius = 16, ContentRadius = 11;
    private const double DefaultMinWidth = 300, DefaultMinHeight = 220;

    private static readonly Brush FrameBg = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)));
    private static readonly Brush HeaderFg = Frozen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ButtonHover = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    private readonly AppController _c;
    private readonly Border _host;
    private readonly Slider _opacity;
    private readonly ToggleButton _pin;
    private readonly DispatcherTimer _sync;
    private WebView2CompositionControl? _web;
    private TextBox? _address;
    private int _opacityPercent = 100;

    // État d'origine de la fenêtre Windows qu'on a mise dans le cadre.
    private IntPtr _native;
    private Native.WINDOWPLACEMENT _nativePlacement;
    private long _nativeStyle, _nativeExStyle;
    private IntPtr _nativeOwner;

    public BubbleConfig Bubble { get; }
    public bool Pinned => _pin.IsChecked == true;
    public IntPtr NativeHwnd => _native;
    public IntPtr Hwnd => new WindowInteropHelper(this).Handle;

    public BubbleFrame(AppController controller, BubbleConfig bubble, int opacity)
    {
        _c = controller;
        Bubble = bubble;
        Title = bubble.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        // Fenêtre transparente : la bulle (coins arrondis, bord lumineux) est dessinée par WPF,
        // et le fondu passe par Opacity, que WPF gère proprement.
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = 0;
        // Tout aligné sur les pixels de l'écran : sans ça, la page web est dessinée « entre deux pixels » et paraît floue.
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = DefaultMinWidth;
        MinHeight = DefaultMinHeight;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _host = new Border { Margin = new Thickness(Inset, 0, Inset, Inset), Background = FrameBg };
        // Arrondit aussi le contenu (page web) dans le bas de la bulle.
        _host.SizeChanged += (_, _) => _host.Clip = new RectangleGeometry(
            new Rect(0, 0, _host.ActualWidth, _host.ActualHeight), ContentRadius, ContentRadius);
        _opacity = new Slider
        {
            Minimum = 30, Maximum = 100, Value = opacity, Width = 80,
            VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1,
            ToolTip = "Opacité", Margin = new Thickness(6, 0, 6, 0),
        };
        _pin = new ToggleButton { Content = Glyph(""), ToolTip = "Épingler (reste affichée quand tu cliques ailleurs)" };
        StyleButton(_pin);

        var root = new Grid();
        _headerRow = new RowDefinition { Height = new GridLength(HeaderHeight) };
        root.RowDefinitions.Add(_headerRow);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _header = BuildHeader();
        _miniHeader = BuildMiniHeader();
        _miniHeader.Visibility = Visibility.Collapsed;
        Grid.SetRow(_header, 0);
        Grid.SetRow(_miniHeader, 0);
        Grid.SetRow(_host, 1);
        root.Children.Add(_header);
        root.Children.Add(_miniHeader);
        root.Children.Add(_host);
        Content = new Border
        {
            CornerRadius = new CornerRadius(FrameRadius),
            Background = FrameBg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B)),
            BorderThickness = new Thickness(2),
            Child = root,
        };

        _opacity.ValueChanged += (_, _) => ApplyOpacity((int)_opacity.Value);
        _sync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sync.Tick += (_, _) => SyncNative();

        SourceInitialized += (_, _) =>
        {
            var h = Hwnd;
            Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_TOOLWINDOW);
            _opacityPercent = (int)_opacity.Value;
        };
        LocationChanged += (_, _) => SyncNative();
        SizeChanged += (_, _) => SyncNative();
        Activated += (_, _) => RaiseNative();
    }

    public int OpacityPercent => _opacityPercent;

    /// <summary>Fondu de fermeture en cours (à attendre avant de rouvrir la même bulle).</summary>
    public Task HideTask { get; private set; } = Task.CompletedTask;

    private byte _alpha;
    private byte TargetAlpha => (byte)(_opacityPercent * 255 / 100);

    // ---------- Apparition / disparition ----------

    /// <summary>À appeler avant Show() : le cadre (et la fenêtre qu'il contiendra) partent invisibles.</summary>
    public void PrepareShow()
    {
        _alpha = 0;
        Opacity = 0;
    }

    public async Task FadeInAsync()
    {
        await Fade(TargetAlpha, 170);
        ApplyOpacity(_opacityPercent);
    }

    /// <summary>Fondu de sortie, puis rend la fenêtre de l'app (réduite) et cache le cadre.</summary>
    public Task HideAnimatedAsync()
    {
        HideTask = Run();
        return HideTask;

        async Task Run()
        {
            await Fade(0, 130);
            var released = DetachNative(minimize: true, restoreLook: false);
            Hide();
            // L'app termine sa réduction de son côté : on attend qu'elle soit vraiment rangée
            // avant de lui rendre son opacité, sinon elle clignote en noir à l'écran.
            for (int i = 0; i < 20 && released != IntPtr.Zero && !Native.IsIconic(released); i++) await Task.Delay(15);
            await Task.Delay(150);
            RestoreLook(released);
        }
    }

    /// <summary>L'app est gardée dans sa bulle, invisible et hors écran, prête à réapparaître.</summary>
    public bool HasParkedNative => _native != IntPtr.Zero && Native.IsWindow(_native) && Native.IsWindowVisible(_native);

    /// <summary>
    /// Fondu de sortie, puis l'app est « garée » hors de l'écran au lieu d'être réduite :
    /// pas besoin de la redessiner à la prochaine ouverture, elle réapparaît instantanément.
    /// </summary>
    public Task ParkAnimatedAsync()
    {
        HideTask = Run();
        return HideTask;

        async Task Run()
        {
            await Fade(0, 130);
            _sync.Stop();
            if (_native != IntPtr.Zero && Native.IsWindow(_native))
            {
                var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
                Native.SetWindowPos(_native, Native.HWND_NOTOPMOST, screen.Right + 200, screen.Top, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            Hide();
        }
    }

    /// <summary>Ramène l'app garée dans le cadre. Retourne true si elle doit d'abord se redessiner.</summary>
    public bool Unpark()
    {
        bool mustRepaint = false;
        // Réduite ou agrandie entre-temps (bouton de l'app) : on la remet en état normal, toujours invisible.
        for (int i = 0; i < 3 && (Native.IsIconic(_native) || Native.IsZoomed(_native)); i++)
        {
            Native.ShowWindow(_native, Native.SW_RESTORE);
            mustRepaint = true;
        }
        if (mustRepaint)
            Native.SetStyle(_native, Native.GetStyle(_native) & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MAXIMIZE));
        RaiseNative();
        SyncNative();
        ForceRepaint();
        _sync.Start();
        return mustRepaint;
    }

    /// <summary>
    /// Chrome met son affichage en pause quand sa fenêtre est cachée ou hors écran, et ne le reprend
    /// pas toujours en revenant (contenu vide). Un redimensionnement d'un pixel le force à se redessiner.
    /// </summary>
    private async void ForceRepaint()
    {
        if (_native == IntPtr.Zero || !Native.GetWindowRect(_native, out var r)) return;
        Native.SetWindowPos(_native, IntPtr.Zero, r.Left, r.Top, r.Width - 1, r.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        await Task.Delay(50);
        SyncNative();
    }

    private async Task Fade(byte to, int durationMs)
    {
        byte from = _alpha;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1, clock.ElapsedMilliseconds / (double)durationMs);
            double eased = 1 - Math.Pow(1 - t, 3);
            SetAlphaBoth((byte)Math.Round(from + (to - from) * eased));
            if (t >= 1) return;
            await Task.Delay(10);
        }
    }

    private void SetAlphaBoth(byte alpha)
    {
        _alpha = alpha;
        Opacity = alpha / 255.0;
        if (_native != IntPtr.Zero) SetAlpha(_native, alpha);
    }

    private static void SetAlpha(IntPtr h, byte alpha)
    {
        Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_LAYERED);
        Native.SetLayeredWindowAttributes(h, 0, alpha, Native.LWA_ALPHA);
    }

    private FrameworkElement BuildHeader()
    {
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 6, 0) };

        if (HeaderIcon(Bubble, 18) is { } img)
        {
            img.Margin = new Thickness(0, 0, 8, 0);
            img.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(img, Dock.Left);
            bar.Children.Add(img);
        }

        var hide = new Button { Content = Glyph(""), ToolTip = "Cacher" };
        StyleButton(hide);
        hide.Click += (_, _) => _c.HideFrame(this);
        DockPanel.SetDock(hide, Dock.Right);
        bar.Children.Add(hide);
        DockPanel.SetDock(_pin, Dock.Right);
        bar.Children.Add(_pin);
        var mini = new Button { Content = Glyph(""), ToolTip = "Mini-lecteur (petite fenêtre flottante)" };
        StyleButton(mini);
        mini.Click += (_, _) => _c.ToggleMini(this);
        DockPanel.SetDock(mini, Dock.Right);
        bar.Children.Add(mini);
        var through = new Button { Content = Glyph(""), ToolTip = "Traversable : la souris passe à travers (clique sur sa bulle pour la reprendre)" };
        StyleButton(through);
        through.Click += (_, _) => _c.ToggleClickThrough(this);
        DockPanel.SetDock(through, Dock.Right);
        bar.Children.Add(through);
        DockPanel.SetDock(_opacity, Dock.Right);
        bar.Children.Add(_opacity);
        if (Bubble.IsWeb || Bubble.IsWindowApp)
        {
            var volume = BuildVolumeButton();
            DockPanel.SetDock(volume, Dock.Right);
            bar.Children.Add(volume);
        }

        if (Bubble.IsWeb)
        {
            var back = new Button { Content = Glyph(""), ToolTip = "Page précédente" };
            var reload = new Button { Content = Glyph(""), ToolTip = "Recharger" };
            StyleButton(back);
            StyleButton(reload);
            back.Click += (_, _) => { if (_web?.CanGoBack == true) _web.GoBack(); };
            reload.Click += (_, _) => _web?.Reload();
            DockPanel.SetDock(back, Dock.Left);
            DockPanel.SetDock(reload, Dock.Left);
            bar.Children.Add(back);
            bar.Children.Add(reload);

            if (Bubble.Url == WebPresets.BrowserUrl)
            {
                _address = new TextBox
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    Foreground = Brushes.White, CaretBrush = Brushes.White, BorderThickness = new Thickness(0),
                    Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5,
                };
                _address.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter || _web?.CoreWebView2 == null) return;
                    var text = _address.Text.Trim();
                    var url = WebPresets.AsUrl(text) ?? "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
                    _web.CoreWebView2.Navigate(url);
                    _web.Focus();
                };
                bar.Children.Add(_address);
                return Wrap(bar);
            }
        }

        var name = new TextBlock
        {
            Text = Bubble.Name, Foreground = HeaderFg, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0),
        };
        _nameTexts.Add(name);
        bar.Children.Add(name);
        return Wrap(bar);
    }

    private static Border Wrap(FrameworkElement bar) => new() { Child = bar, Background = Brushes.Transparent };

    // ---------- Mini-lecteur et mode traversable ----------

    private const double MiniHeaderHeight = 28;
    private readonly RowDefinition _headerRow;
    private readonly FrameworkElement _header, _miniHeader;
    private int _opacityBeforeClickThrough;

    /// <summary>Petite fenêtre flottante, indépendante de la cascade.</summary>
    public bool IsMini { get; private set; }

    /// <summary>La souris passe à travers la bulle (pour jouer avec un guide affiché par-dessus).</summary>
    public bool ClickThrough { get; private set; }

    /// <summary>Barre compacte du mini-lecteur : on la tient pour déplacer, et deux boutons.</summary>
    private FrameworkElement BuildMiniHeader()
    {
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 0, 4, 0) };
        var close = new Button { Content = Glyph(""), ToolTip = "Fermer" };
        var restore = new Button { Content = Glyph(""), ToolTip = "Agrandir à côté de sa bulle" };
        var through = new Button { Content = Glyph(""), ToolTip = "Traversable" };
        foreach (var b in new[] { close, restore, through })
        {
            StyleButton(b);
            b.Height = 24;
            b.Width = 26;
            DockPanel.SetDock(b, Dock.Right);
            bar.Children.Add(b);
        }
        close.Click += (_, _) => _c.HideFrame(this);
        restore.Click += (_, _) => _c.ToggleMini(this);
        through.Click += (_, _) => _c.ToggleClickThrough(this);

        if (HeaderIcon(Bubble, 14) is { } img)
        {
            img.Margin = new Thickness(0, 0, 6, 0);
            img.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(img, Dock.Left);
            bar.Children.Add(img);
        }
        var name = new TextBlock
        {
            Text = Bubble.Name, Foreground = HeaderFg, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _nameTexts.Add(name);
        bar.Children.Add(name);
        var grip = new Border { Child = bar, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, ToolTip = "Tiens pour déplacer" };
        grip.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBlock { Parent: ButtonBase }) DragMove(); };
        return grip;
    }

    public void EnterMini(Rect rect)
    {
        IsMini = true;
        _header.Visibility = Visibility.Collapsed;
        _miniHeader.Visibility = Visibility.Visible;
        _headerRow.Height = new GridLength(MiniHeaderHeight);
        MinWidth = 200;
        MinHeight = 120;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
    }

    public void ExitMini()
    {
        if (!IsMini) return;
        IsMini = false;
        _miniHeader.Visibility = Visibility.Collapsed;
        _header.Visibility = Visibility.Visible;
        _headerRow.Height = new GridLength(HeaderHeight);
        MinWidth = Math.Max(MinWidth, DefaultMinWidth);
        MinHeight = Math.Max(MinHeight, DefaultMinHeight);
    }

    public void SetClickThrough(bool on)
    {
        if (ClickThrough == on) return;
        ClickThrough = on;
        if (on)
        {
            // Semi-transparente pour bien voir le jeu derrière (ta valeur revient quand tu la reprends).
            _opacityBeforeClickThrough = _opacityPercent;
            if (_opacityPercent > 70) _opacity.Value = 70;
        }
        else if (_opacityBeforeClickThrough > 0)
        {
            _opacity.Value = _opacityBeforeClickThrough;
            _opacityBeforeClickThrough = 0;
        }
        ApplyClickThroughStyle();

        // La bulle entière laisse passer la souris : une pastille à part reste cliquable pour reprendre la main.
        if (on)
        {
            _handle = new ClickThroughHandle(this, () => _c.ToggleClickThrough(this));
            _handle.Show();
        }
        else
        {
            _handle?.Close();
            _handle = null;
        }
    }

    private ClickThroughHandle? _handle;

    private void ApplyClickThroughStyle()
    {
        foreach (var h in new[] { Hwnd, _native })
        {
            if (h == IntPtr.Zero) continue;
            long ex = Native.GetExStyle(h);
            Native.SetExStyle(h, ClickThrough ? ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED : ex & ~Native.WS_EX_TRANSPARENT);
        }
    }

    /// <summary>Charge la page web en arrière-plan, sans l'afficher : les pastilles de notification marchent dès le démarrage.</summary>
    public async Task PreloadWebAsync(CoreWebView2Environment env)
    {
        if (_web != null) return;
        PrepareShow();
        ShowActivated = false;
        Left = -32000;
        Top = -32000;
        Show();
        try { await InitWebAsync(env); }
        finally
        {
            Hide();
            ShowActivated = true;
        }
    }

    /// <summary>Icône image de la bulle, ou null si elle a un logo choisi (ou pas d'icône : lanceur, mélangeur).</summary>
    public static ImageSource? IconFor(BubbleConfig b)
    {
        if (b.Glyph.Length > 0 || b.IsLauncher || b.IsMixer) return null;
        return b.IsWeb && string.IsNullOrEmpty(b.IconPath) ? IconLoader.CachedFavicon(b.Url) : IconLoader.Load(b.IconPath, b.IconIndex, b.LaunchPath);
    }

    /// <summary>Logo (caractère d'icône) affiché à la place d'une image.</summary>
    public static string GlyphFor(BubbleConfig b) =>
        b.Glyph.Length > 0 ? b.Glyph : b.IsMixer ? LauncherWindow.MixerGlyph : b.IsLauncher ? BubbleGlyphs.Grid : "";

    /// <summary>Petite icône de la barre du haut : l'image de l'app, ou son logo choisi.</summary>
    private static FrameworkElement? HeaderIcon(BubbleConfig b, double size)
    {
        var image = IconFor(b);
        if (image != null) return new Image { Source = image, Width = size, Height = size };
        var glyph = GlyphFor(b);
        if (glyph.Length == 0) return null;
        return new TextBlock { Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = size * 0.9, Foreground = new SolidColorBrush(LauncherWindow.Accent) };
    }

    // ---------- Paramètres ----------

    public void InitSettings()
    {
        if (_host.Child is SettingsView) return;
        _host.Child = new SettingsView(_c);
    }

    // ---------- Lanceur ----------

    private LauncherView? _launcherView;

    public void InitLauncher()
    {
        if (_launcherView != null) { _launcherView.Refresh(); return; }
        _launcherView = new LauncherView(_c, Bubble);
        _host.Child = _launcherView;
    }

    public void RefreshLauncher() => _launcherView?.Refresh();

    public void FocusLauncherSearch() => _launcherView?.FocusSearch();

    /// <summary>Met à jour le nom affiché après « Personnaliser… ».</summary>
    public void UpdateLook()
    {
        Title = Bubble.Name;
        foreach (var t in _nameTexts) t.Text = Bubble.Name;
    }

    private readonly System.Collections.Generic.List<TextBlock> _nameTexts = new();

    // ---------- Mélangeur audio ----------

    /// <summary>Le mélangeur audio de Bulles, dessiné directement dans la bulle.</summary>
    public void InitMixer()
    {
        if (_host.Child is MixerView) return;
        _host.Child = new MixerView();
    }

    // ---------- Contenu web ----------

    public async Task InitWebAsync(CoreWebView2Environment env)
    {
        if (_web != null) return;
        _web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x15, 0x1C), UseLayoutRounding = true };
        RenderOptions.SetBitmapScalingMode(_web, BitmapScalingMode.NearestNeighbor);
        _host.Child = _web;
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.NewWindowRequested += (_, e) =>
        {
            // Popups de connexion (taille imposée) : on laisse WebView2 les ouvrir.
            // Liens classiques « nouvel onglet » : dans la bulle navigateur, sinon dans ton navigateur habituel.
            if (e.WindowFeatures.HasSize || e.WindowFeatures.HasPosition) return;
            e.Handled = true;
            if (_address != null) core.Navigate(e.Uri);
            else OpenExternal(e.Uri);
        };
        core.SourceChanged += (_, _) => { if (_address != null && !_address.IsKeyboardFocused) _address.Text = core.Source; };
        core.DocumentTitleChanged += (_, _) =>
        {
            Title = core.DocumentTitle;
            _c.OnContentTitle(Bubble, core.DocumentTitle);
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(VolumeScript(Bubble.Volume / 100.0));
        core.IsMuted = Bubble.Volume == 0;
        core.Navigate(Bubble.Url);
    }

    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    public void FocusContent()
    {
        if (_web != null) _web.Focus();
        else if (_native != IntPtr.Zero) Native.SetForegroundWindow(_native);
    }

    // ---------- Contenu app Windows ----------

    /// <summary>Met la fenêtre dans le cadre. Retourne true si elle était réduite ou agrandie (elle doit se redessiner).</summary>
    public bool AttachNative(IntPtr hwnd)
    {
        if (_native == hwnd) { SyncNative(); return false; }
        DetachNative(minimize: false);
        _native = hwnd;
        _nativePlacement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(hwnd, ref _nativePlacement);
        _nativeStyle = Native.GetStyle(hwnd);
        _nativeExStyle = Native.GetExStyle(hwnd);
        _nativeOwner = Native.GetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT);

        // Tout se passe dans l'invisible : pas d'animation Windows, fenêtre transparente
        // pendant qu'on la restaure et qu'on la place dans le cadre. Le fondu la fera apparaître.
        Native.SetTransitionsDisabled(hwnd, true);
        SetAlpha(hwnd, _alpha);

        // Une fenêtre réduite ou agrandie ignore les déplacements : on la repasse en état normal.
        // Réduite → restaurer peut d'abord la ramener agrandie (Chrome), d'où plusieurs passes.
        bool wasHidden = Native.IsIconic(hwnd) || Native.IsZoomed(hwnd);
        for (int i = 0; i < 3 && (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)); i++)
            Native.ShowWindow(hwnd, Native.SW_RESTORE);

        // Retire la barre de titre et les bords : c'est le cadre Bulles qui les remplace.
        Native.SetStyle(hwnd, Native.GetStyle(hwnd) & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_MAXIMIZE));
        // Le cadre devient « propriétaire » de la fenêtre : elle reste toujours au-dessus de lui.
        Native.SetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT, Hwnd);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        SyncNative();
        ForceRepaint();
        ApplyStoredVolume();
        _sync.Start();
        return wasHidden;
    }

    /// <summary>Rend la fenêtre de l'app. Retourne son handle si son opacité reste à rendre (restoreLook: false).</summary>
    public IntPtr DetachNative(bool minimize, bool restoreLook = true)
    {
        _sync.Stop();
        MinWidth = DefaultMinWidth;
        MinHeight = DefaultMinHeight;
        var h = _native;
        if (h == IntPtr.Zero) return IntPtr.Zero;
        _native = IntPtr.Zero;
        if (!Native.IsWindow(h)) return IntPtr.Zero;

        Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, _nativeOwner);
        // On rend les styles d'origine SAUF les bits d'état réduit/agrandi : recopier « réduit » sur une
        // fenêtre affichée la laisse en fantôme gris à l'écran. L'état est rendu par SetWindowPlacement.
        const long stateBits = Native.WS_MINIMIZE | Native.WS_MAXIMIZE;
        Native.SetStyle(h, (_nativeStyle & ~stateBits) | (Native.GetStyle(h) & stateBits));
        int corner = Native.DWMWCP_DEFAULT;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Native.SetWindowPos(h, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // Vraie réduction (avec son bouton dans la barre des tâches), puis remise de la position
        // d'origine et de l'état agrandi pour quand tu la rouvriras. La fenêtre est encore
        // transparente et sans animation : rien ne bouge à l'écran.
        if (minimize) Native.ShowWindow(h, Native.SW_SHOWMINNOACTIVE);
        var p = _nativePlacement;
        if (minimize) p.showCmd = Native.SW_SHOWMINNOACTIVE;
        Native.SetWindowPlacement(h, ref p);

        _releasedExStyle = _nativeExStyle;
        if (!restoreLook) return h;
        RestoreLook(h);
        return IntPtr.Zero;
    }

    private long _releasedExStyle;

    /// <summary>Une fois rangée, la fenêtre retrouve son opacité et ses animations d'origine.</summary>
    private void RestoreLook(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.IsWindow(h) || h == _native) return;
        Native.SetOpacity(h, 100, _releasedExStyle);
        Native.SetExStyle(h, _releasedExStyle);
        Native.SetTransitionsDisabled(h, false);
    }

    private void SyncNative()
    {
        if (_native == IntPtr.Zero || !IsVisible || _enforcing) return;
        if (!Native.IsWindow(_native) || !Native.IsWindowVisible(_native))
        {
            // L'app a été fermée (ou s'est cachée dans la zone de notification) : on lui rend son état d'origine.
            DetachNative(minimize: false);
            _c.HideFrame(this);
            return;
        }
        // Bouton « réduire » de l'app : on cache simplement la bulle.
        if (Native.IsIconic(_native))
        {
            _sync.Stop();
            _c.HideFrame(this);
            return;
        }
        // L'app s'est ré-agrandie toute seule : on la remet à sa place dans le cadre.
        if (Native.IsZoomed(_native)) Native.ShowWindow(_native, Native.SW_RESTORE);
        if (_host.ActualWidth < 1) return;
        var topLeft = _host.PointToScreen(new Point(0, 0));
        var bottomRight = _host.PointToScreen(new Point(_host.ActualWidth, _host.ActualHeight));
        int x = (int)Math.Round(topLeft.X), y = (int)Math.Round(topLeft.Y);
        int w = (int)Math.Round(bottomRight.X - topLeft.X), h = (int)Math.Round(bottomRight.Y - topLeft.Y);
        if (Native.GetWindowRect(_native, out var r) && r.Left == x && r.Top == y && r.Width == w && r.Height == h) return;
        Native.SetWindowPos(_native, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        EnforceNativeMinimum(w, h);
    }

    /// <summary>
    /// Chaque app a une taille minimale (Chrome ~500 px de large) : si elle refuse de rétrécir,
    /// la bulle s'arrête à cette taille au lieu de laisser l'app déborder hors du cadre.
    /// </summary>
    private void EnforceNativeMinimum(int requestedW, int requestedH)
    {
        if (!Native.GetWindowRect(_native, out var actual)) return;
        int extraW = actual.Width - requestedW, extraH = actual.Height - requestedH;
        if (extraW <= 1 && extraH <= 1) return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = ActualWidth + Math.Max(0, extraW) / dpi;
        double height = ActualHeight + Math.Max(0, extraH) / dpi;
        // Bulle collée à droite de l'écran : elle grandit vers la gauche (bord droit fixe) pour rester visible.
        double right = Left + ActualWidth;
        double left = _c.Settings.IsLeft ? Left : Math.Max(Screens.WorkAreaDip(_c.Settings).Left, right - width);

        _enforcing = true;
        try
        {
            MinWidth = Math.Max(MinWidth, width);
            MinHeight = Math.Max(MinHeight, height);
            Left = left;
            Width = width;
            Height = height;
        }
        finally
        {
            _enforcing = false;
        }
        UpdateLayout();
        if (_resyncing) return;
        _resyncing = true;
        try { SyncNative(); }
        finally { _resyncing = false; }
    }

    private bool _resyncing;

    private bool _enforcing;

    private void RaiseNative()
    {
        if (_native == IntPtr.Zero) return;
        Native.SetWindowPos(_native, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---------- Opacité, apparence ----------

    private void ApplyOpacity(int percent)
    {
        _opacityPercent = percent;
        _alpha = TargetAlpha;
        Opacity = percent / 100.0;
        if (_native != IntPtr.Zero) Native.SetOpacity(_native, percent, _nativeExStyle);
        if (ClickThrough) ApplyClickThroughStyle();
    }

    // ---------- Volume ----------

    private TextBlock? _volumeIcon;

    /// <summary>Haut-parleur dans la barre : un clic ouvre une jauge de volume pour cette bulle.</summary>
    private FrameworkElement BuildVolumeButton()
    {
        _volumeIcon = Glyph(VolumeGlyph(Bubble.Volume));
        var button = new ToggleButton { Content = _volumeIcon, ToolTip = "Volume de cette bulle" };
        StyleButton(button);

        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = Bubble.Volume, Width = 150,
            IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock { Foreground = Brushes.White, FontSize = 12.5, Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var mute = new Button { Content = Glyph(""), ToolTip = "Couper / remettre le son" };
        StyleButton(mute);

        int beforeMute = Bubble.Volume > 0 ? Bubble.Volume : 100;
        mute.Click += (_, _) =>
        {
            if (slider.Value > 0) { beforeMute = (int)slider.Value; slider.Value = 0; }
            else slider.Value = beforeMute;
        };
        void Update()
        {
            int v = (int)slider.Value;
            label.Text = $"{v} %";
            _volumeIcon.Text = VolumeGlyph(v);
            SetVolume(v);
        }
        slider.ValueChanged += (_, _) => Update();
        label.Text = $"{Bubble.Volume} %";

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mute);
        row.Children.Add(slider);
        row.Children.Add(label);
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
            HorizontalOffset = -80,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1C, 0x21, 0x2B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4, 10, 4),
                Child = row,
            },
        };
        button.Checked += (_, _) => popup.IsOpen = true;
        button.Unchecked += (_, _) => popup.IsOpen = false;
        popup.Closed += (_, _) => button.IsChecked = false;
        // La molette sur le haut-parleur règle aussi le volume, sans ouvrir la jauge.
        button.MouseWheel += (_, e) => slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 5 : -5), 0, 100);
        return button;
    }

    private static string VolumeGlyph(int v) => v == 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";

    private void SetVolume(int percent)
    {
        Bubble.Volume = percent;
        if (Bubble.IsWeb) ApplyWebVolume();
        else AppAudio.SetVolumeAsync(Bubble.ProcessName, percent);
    }

    /// <summary>Remet le volume mémorisé quand l'app entre dans sa bulle.</summary>
    private void ApplyStoredVolume()
    {
        if (!Bubble.IsWeb && Bubble.Volume != 100) AppAudio.SetVolumeAsync(Bubble.ProcessName, Bubble.Volume);
    }

    private void ApplyWebVolume()
    {
        if (_web?.CoreWebView2 is not { } core) return;
        core.IsMuted = Bubble.Volume == 0;
        var factor = (Bubble.Volume / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        _ = core.ExecuteScriptAsync($"window.__bullesSetVolume && window.__bullesSetVolume({factor})");
    }

    /// <summary>
    /// Script injecté dans les pages web : le volume de la bulle s'applique PAR-DESSUS celui du site
    /// (le curseur de YouTube garde sa propre valeur, on la multiplie juste par la jauge de la bulle).
    /// </summary>
    private static string VolumeScript(double factor) => """
        (() => {
          const d = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'volume');
          if (!d || window.__bullesSetVolume) return;
          const wanted = new WeakMap();
          let factor = FACTOR;
          const own = m => wanted.has(m) ? wanted.get(m) : d.get.call(m);
          const apply = m => { const w = own(m); wanted.set(m, w); d.set.call(m, Math.max(0, Math.min(1, w * factor))); };
          Object.defineProperty(HTMLMediaElement.prototype, 'volume', {
            configurable: true,
            get() { return own(this); },
            set(v) { wanted.set(this, v); d.set.call(this, Math.max(0, Math.min(1, v * factor))); },
          });
          window.__bullesSetVolume = f => { factor = f; document.querySelectorAll('audio,video').forEach(apply); };
          document.addEventListener('play', e => { if (e.target instanceof HTMLMediaElement) apply(e.target); }, true);
        })();
        """.Replace("FACTOR", factor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

    private static TextBlock Glyph(string code) => new()
    {
        Text = code, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13,
        Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    private static void StyleButton(ButtonBase b)
    {
        var template = new ControlTemplate(b.GetType());
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bg";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ButtonHover, "bg"));
        template.Triggers.Add(hover);
        if (b is ToggleButton)
        {
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(LauncherWindow.Accent), "bg"));
            template.Triggers.Add(on);
        }
        b.Template = template;
        b.Width = 30;
        b.Height = 28;
        b.Cursor = Cursors.Hand;
        b.Focusable = false;
        b.VerticalAlignment = VerticalAlignment.Center;
    }

    public void DisposeContent()
    {
        DetachNative(minimize: false);
        _web?.Dispose();
        _web = null;
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
