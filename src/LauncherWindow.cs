using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace Bulles;

/// <summary>
/// Bande transparente collée au bord de l'écran qui porte la bulle principale et la cascade.
/// Les zones transparentes laissent passer les clics vers le jeu.
/// </summary>
public sealed class LauncherWindow : Window
{
    public const double StripWidth = 76;
    private const double MainSize = 54, AppSize = 46, PlusSize = 34, Spacing = 56, FirstOffset = 60;

    public static readonly Color Accent = Color.FromRgb(0x3D, 0xA5, 0xFF);
    public const string MixerGlyph = "";
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Accent));
    private static readonly Brush BubbleFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x16, 0x1A, 0x22)));
    private static readonly Brush RingBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));

    private readonly AppController _c;
    private readonly Canvas _canvas = new();
    private readonly Grid _main;
    private readonly List<BubbleVisual> _apps = new();
    private BubbleVisual? _plus;
    private double _mainY;

    private bool _pressed, _dragging;
    private Point _pressPos;

    public bool IsOpen { get; private set; }

    public LauncherWindow(AppController controller)
    {
        _c = controller;
        Title = "Bulles";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = StripWidth;
        Content = _canvas;

        _main = CreateMainBubble();
        _canvas.Children.Add(_main);
        Panel.SetZIndex(_main, 10);

        SourceInitialized += (_, _) =>
        {
            // Fenêtre outil : absente d'Alt+Tab.
            var h = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_TOOLWINDOW);
        };
        DpiChanged += (_, _) => UpdatePlacement();
        // Demande de fermeture externe (mise à jour, fermeture de session) : on quitte proprement
        // pour rendre les fenêtres gardées dans les bulles.
        Closing += (_, _) => _c.Quit();
    }

    private bool ExpandsDown => _mainY < Height / 2;

    private double TargetCenter(int index) => _mainY + (ExpandsDown ? 1 : -1) * (FirstOffset + index * Spacing);

    public void UpdatePlacement()
    {
        var wa = Screens.WorkAreaDip(_c.Settings);
        Height = wa.Height;
        Top = wa.Top;
        Left = _c.Settings.IsLeft ? wa.Left : wa.Right - StripWidth;
        _mainY = ClampY(_c.Settings.MainY * Height);
        PlaceCenter(_main, _mainY);
        foreach (var el in AllItems()) Reset(el);
        if (IsOpen) Open();
    }

    private double ClampY(double y) => Math.Clamp(y, MainSize / 2 + 8, Math.Max(MainSize / 2 + 8, Height - MainSize / 2 - 8));

    public void Rebuild()
    {
        foreach (var el in AllItems()) _canvas.Children.Remove(el);
        _apps.Clear();

        var bubbles = _c.Settings.Bubbles;
        for (int i = 0; i < bubbles.Count; i++)
        {
            var b = bubbles[i];
            var icon = BubbleFrame.IconFor(b);
            string? glyph = icon != null ? null : b.IsMixer ? MixerGlyph : b.Name[..1].ToUpperInvariant();
            var v = new BubbleVisual(AppSize, icon, glyph) { ToolTip = b.Name };
            v.MouseLeftButtonUp += (_, _) => _c.ToggleApp(b);
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = "Retirer la bulle" };
            remove.Click += (_, _) => _c.RemoveBubble(b);
            menu.Items.Add(remove);
            v.ContextMenu = menu;
            _apps.Add(v);
        }
        _plus = new BubbleVisual(PlusSize, null, "+") { ToolTip = "Ajouter une application" };
        _plus.MouseLeftButtonUp += (_, _) => _c.ToggleSearch();

        foreach (var el in AllItems())
        {
            _canvas.Children.Add(el);
            Reset(el);
        }
        if (IsOpen) Open();
    }

    private IEnumerable<BubbleVisual> AllItems()
    {
        foreach (var a in _apps) yield return a;
        if (_plus != null) yield return _plus;
    }

    public void Open()
    {
        IsOpen = true;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        int i = 0;
        foreach (var el in AllItems())
        {
            el.Visibility = Visibility.Visible;
            Animate(el, TargetCenter(i), 1, i * 35, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
            i++;
        }
    }

    public void Close(bool animated = true)
    {
        IsOpen = false;
        foreach (var el in AllItems())
        {
            if (!animated)
            {
                Reset(el);
                continue;
            }
            var target = el;
            Animate(el, _mainY, 0, 0, new QuadraticEase { EasingMode = EasingMode.EaseIn }, () =>
            {
                if (!IsOpen) target.Visibility = Visibility.Hidden;
            });
        }
    }

    public void SetActive(BubbleConfig? bubble)
    {
        var list = _c.Settings.Bubbles;
        for (int i = 0; i < _apps.Count; i++) _apps[i].SetActive(bubble != null && i < list.Count && list[i] == bubble);
    }

    public void SetLoading(BubbleConfig bubble, bool loading)
    {
        int i = _c.Settings.Bubbles.IndexOf(bubble);
        if (i >= 0 && i < _apps.Count) _apps[i].SetLoading(loading);
    }

    /// <summary>Centre vertical de la bulle et bords de la bande, en unités WPF écran.</summary>
    public (double CenterY, double StripLeft, double StripRight) AnchorDip(BubbleConfig bubble)
    {
        int i = Math.Max(0, _c.Settings.Bubbles.IndexOf(bubble));
        return (Top + TargetCenter(i), Left, Left + StripWidth);
    }

    /// <summary>Haut et bas de la bulle « + » et bords de la bande, en unités WPF écran.</summary>
    public (double Top, double Bottom, double StripLeft, double StripRight, bool Down) PlusAnchor()
    {
        double cy = Top + TargetCenter(_apps.Count);
        return (cy - PlusSize / 2, cy + PlusSize / 2, Left, Left + StripWidth, ExpandsDown);
    }

    private static void PlaceCenter(FrameworkElement el, double centerY)
    {
        el.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetLeft(el, StripWidth / 2 - el.Width / 2);
        Canvas.SetTop(el, centerY - el.Height / 2);
    }

    private void Reset(BubbleVisual el)
    {
        el.BeginAnimation(OpacityProperty, null);
        PlaceCenter(el, _mainY);
        el.Opacity = 0;
        el.Visibility = Visibility.Hidden;
    }

    private static void Animate(FrameworkElement el, double centerY, double opacity, int delayMs, IEasingFunction ease, Action? done = null)
    {
        var duration = TimeSpan.FromMilliseconds(230);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        Canvas.SetLeft(el, StripWidth / 2 - el.Width / 2);
        var top = new DoubleAnimation(centerY - el.Height / 2, duration) { BeginTime = begin, EasingFunction = ease };
        var fade = new DoubleAnimation(opacity, duration) { BeginTime = begin };
        if (done != null) fade.Completed += (_, _) => done();
        el.BeginAnimation(Canvas.TopProperty, top);
        el.BeginAnimation(OpacityProperty, fade);
    }

    private Grid CreateMainBubble()
    {
        var g = new Grid { Width = MainSize, Height = MainSize, Cursor = Cursors.Hand, ToolTip = "Bulles" };
        g.Children.Add(new Ellipse { Fill = AccentBrush });
        g.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 2.5, Margin = new Thickness(3) });
        g.Children.Add(new Ellipse { Fill = Brushes.White, Width = 16, Height = 16 });
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.RenderTransform = new ScaleTransform(1, 1);
        g.MouseEnter += (_, _) => BubbleVisual.Scale(g, 1.08);
        g.MouseLeave += (_, _) => BubbleVisual.Scale(g, 1);

        g.MouseLeftButtonDown += (_, e) =>
        {
            _pressed = true;
            _dragging = false;
            _pressPos = e.GetPosition(this);
            g.CaptureMouse();
        };
        g.MouseMove += (_, e) => OnMainDrag(e);
        g.MouseLeftButtonUp += (_, _) =>
        {
            g.ReleaseMouseCapture();
            if (!_pressed) return;
            _pressed = false;
            if (_dragging)
            {
                _dragging = false;
                _c.Settings.MainY = _mainY / Height;
                _c.Settings.Save();
            }
            else
            {
                _c.ToggleLauncher();
            }
        };

        var menu = new ContextMenu();
        var settings = new MenuItem { Header = "Paramètres" };
        settings.Click += (_, _) => _c.OpenSettings();
        var quit = new MenuItem { Header = "Quitter Bulles" };
        quit.Click += (_, _) => _c.Quit();
        menu.Items.Add(settings);
        menu.Items.Add(quit);
        g.ContextMenu = menu;
        return g;
    }

    private void OnMainDrag(MouseEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(this);
        if (!_dragging)
        {
            if ((p - _pressPos).Length < 6) return;
            _dragging = true;
            _c.CloseLauncher();
        }

        _mainY = ClampY(p.Y);
        PlaceCenter(_main, _mainY);

        // Glisser la bulle vers l'autre moitié de l'écran (ou sur un autre écran) la colle au bord le plus proche.
        var pt = PointToScreen(p);
        var target = Forms.Screen.FromPoint(new System.Drawing.Point((int)pt.X, (int)pt.Y));
        var wa = target.WorkingArea;
        bool wantLeft = pt.X < wa.Left + wa.Width / 2.0;
        if (wantLeft != _c.Settings.IsLeft || target.DeviceName != Screens.Current(_c.Settings).DeviceName)
        {
            _c.Settings.Side = wantLeft ? "Left" : "Right";
            _c.Settings.Screen = target.DeviceName;
            _c.Settings.MainY = _mainY / Height;
            UpdatePlacement();
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class BubbleVisual : Grid
    {
        private readonly Ellipse _ring;
        private readonly FrameworkElement? _content;

        public BubbleVisual(double size, ImageSource? icon, string? glyph)
        {
            Width = size;
            Height = size;
            Cursor = Cursors.Hand;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = new ScaleTransform(1, 1);

            Children.Add(new Ellipse { Fill = BubbleFill });
            if (icon != null)
            {
                _content = new Image { Source = icon, Width = size * 0.58, Height = size * 0.58 };
                RenderOptions.SetBitmapScalingMode(_content, BitmapScalingMode.HighQuality);
            }
            else
            {
                // Icône de police (ex. mélangeur) ou lettre / « + ».
                bool isIcon = glyph is { Length: 1 } && glyph[0] >= '';
                _content = new TextBlock
                {
                    Text = glyph ?? "?",
                    Foreground = isIcon ? AccentBrush : Brushes.White,
                    FontFamily = isIcon ? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") : SystemFonts.MessageFontFamily,
                    FontSize = size * (isIcon ? 0.44 : 0.55),
                    FontWeight = FontWeights.Light,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, isIcon ? 0 : size * 0.08),
                };
            }
            Children.Add(_content);
            _ring = new Ellipse { Stroke = RingBrush, StrokeThickness = 1.2 };
            Children.Add(_ring);

            MouseEnter += (_, _) => Scale(this, 1.1);
            MouseLeave += (_, _) => Scale(this, 1);
        }

        public void SetActive(bool active)
        {
            _ring.Stroke = active ? AccentBrush : RingBrush;
            _ring.StrokeThickness = active ? 3 : 1.2;
        }

        public void SetLoading(bool loading)
        {
            if (_content == null) return;
            if (loading)
                _content.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            else
                _content.BeginAnimation(OpacityProperty, null);
        }

        public static void Scale(FrameworkElement el, double to)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(110));
            var st = (ScaleTransform)el.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }
}
