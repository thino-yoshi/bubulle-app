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
    private const double HeaderHeight = 38, Inset = 4;

    private static readonly Brush FrameBg = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x15, 0x1C)));
    private static readonly Brush HeaderFg = Frozen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ButtonHover = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    private readonly AppController _c;
    private readonly Border _host;
    private readonly Slider _opacity;
    private readonly ToggleButton _pin;
    private readonly DispatcherTimer _sync;
    private WebView2? _web;
    private TextBox? _address;
    private long _frameExStyle;
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
        Background = FrameBg;
        MinWidth = 300;
        MinHeight = 220;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(7),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _host = new Border { Margin = new Thickness(Inset, 0, Inset, Inset), Background = Brushes.Black };
        _opacity = new Slider
        {
            Minimum = 30, Maximum = 100, Value = opacity, Width = 80,
            VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1,
            ToolTip = "Opacité", Margin = new Thickness(6, 0, 6, 0),
        };
        _pin = new ToggleButton { Content = Glyph(""), ToolTip = "Épingler (reste affichée quand tu cliques ailleurs)" };
        StyleButton(_pin);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        Grid.SetRow(_host, 1);
        root.Children.Add(header);
        root.Children.Add(_host);
        Content = root;

        _opacity.ValueChanged += (_, _) => ApplyOpacity((int)_opacity.Value);
        _sync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sync.Tick += (_, _) => SyncNative();

        SourceInitialized += (_, _) =>
        {
            var h = Hwnd;
            Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_TOOLWINDOW);
            _frameExStyle = Native.GetExStyle(h);
            int corner = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var a = LauncherWindow.Accent;
            int border = a.R | (a.G << 8) | (a.B << 16);
            Native.DwmSetWindowAttribute(h, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            ApplyOpacity((int)_opacity.Value);
        };
        LocationChanged += (_, _) => SyncNative();
        SizeChanged += (_, _) => SyncNative();
        Activated += (_, _) => RaiseNative();
    }

    public int OpacityPercent => _opacityPercent;

    private FrameworkElement BuildHeader()
    {
        var bar = new DockPanel { LastChildFill = true, Margin = new Thickness(10, 0, 6, 0) };

        var icon = IconFor(Bubble);
        if (icon != null)
        {
            var img = new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
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
        DockPanel.SetDock(_opacity, Dock.Right);
        bar.Children.Add(_opacity);

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

        bar.Children.Add(new TextBlock
        {
            Text = Bubble.Name, Foreground = HeaderFg, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 0, 0),
        });
        return Wrap(bar);
    }

    private static Border Wrap(FrameworkElement bar) => new() { Child = bar, Background = Brushes.Transparent };

    public static ImageSource? IconFor(BubbleConfig b) =>
        b.IsWeb && string.IsNullOrEmpty(b.IconPath) ? IconLoader.CachedFavicon(b.Url) : IconLoader.Load(b.IconPath, b.IconIndex, b.LaunchPath);

    // ---------- Contenu web ----------

    public async Task InitWebAsync(CoreWebView2Environment env)
    {
        if (_web != null) return;
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x12, 0x15, 0x1C) };
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
        core.DocumentTitleChanged += (_, _) => Title = core.DocumentTitle;
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

    public void AttachNative(IntPtr hwnd)
    {
        if (_native == hwnd) { SyncNative(); return; }
        DetachNative(minimize: false);
        _native = hwnd;
        _nativePlacement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(hwnd, ref _nativePlacement);
        _nativeStyle = Native.GetStyle(hwnd);
        _nativeExStyle = Native.GetExStyle(hwnd);
        _nativeOwner = Native.GetWindowLongPtr(hwnd, Native.GWLP_HWNDPARENT);

        // Une fenêtre réduite ou agrandie ignore les déplacements : on la repasse en état normal.
        // Réduite → restaurer peut d'abord la ramener agrandie (Chrome), d'où plusieurs passes.
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
        Native.SetOpacity(hwnd, _opacityPercent, _nativeExStyle);
        _sync.Start();
    }

    public void DetachNative(bool minimize)
    {
        _sync.Stop();
        var h = _native;
        if (h == IntPtr.Zero) return;
        _native = IntPtr.Zero;
        if (!Native.IsWindow(h)) return;

        Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, _nativeOwner);
        Native.SetStyle(h, _nativeStyle);
        Native.SetOpacity(h, 100, _nativeExStyle);
        Native.SetExStyle(h, _nativeExStyle);
        int corner = Native.DWMWCP_DEFAULT;
        Native.DwmSetWindowAttribute(h, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        Native.SetWindowPos(h, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        // Remet la position d'origine (et l'état agrandi si besoin), réduite pour ne pas gêner le jeu.
        var p = _nativePlacement;
        if (minimize) p.showCmd = Native.SW_SHOWMINNOACTIVE;
        Native.SetWindowPlacement(h, ref p);
    }

    private void SyncNative()
    {
        if (_native == IntPtr.Zero || !IsVisible) return;
        if (!Native.IsWindow(_native) || !Native.IsWindowVisible(_native))
        {
            // L'app a été fermée (ou s'est cachée dans la zone de notification) : on lui rend son état d'origine.
            DetachNative(minimize: false);
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
    }

    private void RaiseNative()
    {
        if (_native == IntPtr.Zero) return;
        Native.SetWindowPos(_native, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---------- Opacité, apparence ----------

    private void ApplyOpacity(int percent)
    {
        _opacityPercent = percent;
        var h = Hwnd;
        if (h != IntPtr.Zero) Native.SetOpacity(h, percent, _frameExStyle);
        if (_native != IntPtr.Zero) Native.SetOpacity(_native, percent, _nativeExStyle);
    }

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
