using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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
    private static readonly Brush HoldBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xDC, 0xFF)));
    private static readonly Brush BadgeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0x3F, 0x43)));

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
        if (_settings is { Visibility: Visibility.Visible }) PlaceCenter(_settings, SettingsCenter);
    }

    private double ClampY(double y) => Math.Clamp(y, MainSize / 2 + 8, Math.Max(MainSize / 2 + 8, Height - MainSize / 2 - 8));

    // ---------- Bulle « Paramètres » (au-dessus de la bulle principale) ----------

    private BubbleVisual? _settings;

    /// <summary>Position de la bulle engrenage : de l'autre côté de la bulle principale que la cascade.</summary>
    private double SettingsCenter =>
        Math.Clamp(_mainY + (ExpandsDown ? -1 : 1) * FirstOffset, AppSize / 2 + 4, Height - AppSize / 2 - 4);

    public void ShowSettingsBubble()
    {
        if (_settings == null)
        {
            _settings = new BubbleVisual(AppSize, null, "") { ToolTip = "Paramètres" };
            _settings.MouseLeftButtonUp += (_, _) => _c.ToggleSettings();
            _canvas.Children.Add(_settings);
        }
        _settings.SetActive(true);
        _settings.Visibility = Visibility.Visible;
        PlaceCenter(_settings, _mainY);
        _settings.Opacity = 0;
        Animate(_settings, SettingsCenter, 1, 0, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
    }

    public void HideSettingsBubble()
    {
        if (_settings == null || _settings.Visibility != Visibility.Visible) return;
        var s = _settings;
        Animate(s, _mainY, 0, 0, new QuadraticEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            if (_c.CurrentIsSettings) return;
            s.Visibility = Visibility.Hidden;
        });
    }

    /// <summary>Centre de la bulle engrenage et bords de la bande, pour placer la fenêtre des paramètres.</summary>
    public (double CenterY, double StripLeft, double StripRight) SettingsAnchorDip() => (Top + SettingsCenter, Left, Left + StripWidth);

    // ---------- Réorganiser les bulles (appui long puis glisser) ----------

    private const int HoldMs = 550;
    private BubbleVisual? _held;
    private bool _reordering, _holdCancelled;
    private Point _holdStart;
    private int _dragIndex;
    private DispatcherTimer? _holdTimer;

    private void BeginHold(BubbleVisual v, BubbleConfig b, MouseButtonEventArgs e)
    {
        _held = v;
        _reordering = false;
        _holdCancelled = false;
        _holdStart = e.GetPosition(this);
        v.CaptureMouse();
        v.StartHoldRing(TimeSpan.FromMilliseconds(HoldMs));
        _holdTimer?.Stop();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer!.Stop();
            if (_held != v || _holdCancelled) return;
            // Contour plein : la bulle se décroche et suit la souris.
            _reordering = true;
            _dragIndex = _apps.IndexOf(v);
            v.SetDragLook(true);
            Panel.SetZIndex(v, 20);
        };
        _holdTimer.Start();
        e.Handled = true;
    }

    private void OnHoldMove(BubbleVisual v, MouseEventArgs e)
    {
        if (_held != v) return;
        var p = e.GetPosition(this);
        if (!_reordering)
        {
            // La souris bouge avant la fin du chargement : on annule (ce n'était ni un clic ni un déplacement).
            if (!_holdCancelled && (p - _holdStart).Length > 10)
            {
                _holdCancelled = true;
                v.StopHoldRing();
            }
            return;
        }

        // La bulle suit la souris le long de la colonne ; les autres se décalent pour lui faire une place.
        double first = TargetCenter(0), last = TargetCenter(_apps.Count - 1);
        double y = Math.Clamp(p.Y, Math.Min(first, last), Math.Max(first, last));
        v.BeginAnimation(Canvas.TopProperty, null);
        Canvas.SetTop(v, y - v.Height / 2);

        int target = Math.Clamp((int)Math.Round(Math.Abs(y - _mainY - (ExpandsDown ? FirstOffset : -FirstOffset)) / Spacing), 0, _apps.Count - 1);
        if (target == _dragIndex) return;
        _dragIndex = target;
        int slot = 0;
        foreach (var other in _apps)
        {
            if (other == v) continue;
            if (slot == _dragIndex) slot++;
            Animate(other, TargetCenter(slot), 1, 0, new QuadraticEase { EasingMode = EasingMode.EaseOut });
            slot++;
        }
    }

    private void EndHold(BubbleVisual v, BubbleConfig b)
    {
        if (_held != v) return;
        _holdTimer?.Stop();
        v.ReleaseMouseCapture();
        v.StopHoldRing();
        _held = null;

        if (_reordering)
        {
            _reordering = false;
            v.SetDragLook(false);
            Panel.SetZIndex(v, 0);
            _c.MoveBubble(b, _dragIndex);
            return;
        }
        if (!_holdCancelled) _c.ToggleApp(b);
    }

    /// <summary>Après un déplacement : tout est remis en place sans rejouer l'animation de déploiement.</summary>
    private void ShowInPlace()
    {
        int i = 0;
        foreach (var el in AllItems())
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Visibility = Visibility.Visible;
            el.Opacity = 1;
            PlaceCenter(el, TargetCenter(i++));
        }
    }

    public void Rebuild(bool replayOpen = true)
    {
        foreach (var el in AllItems()) _canvas.Children.Remove(el);
        _apps.Clear();

        var bubbles = _c.Settings.Bubbles;
        for (int i = 0; i < bubbles.Count; i++)
        {
            var b = bubbles[i];
            var icon = BubbleFrame.IconFor(b);
            var logo = BubbleFrame.GlyphFor(b);
            string? glyph = icon != null ? null : logo.Length > 0 ? logo : b.Name[..1].ToUpperInvariant();
            var v = new BubbleVisual(AppSize, icon, glyph) { ToolTip = b.Name };
            // Clic court : ouvrir l'app. Appui long : le contour se remplit, puis la bulle se déplace.
            v.MouseLeftButtonDown += (_, e) => BeginHold(v, b, e);
            v.MouseMove += (_, e) => OnHoldMove(v, e);
            v.MouseLeftButtonUp += (_, _) => EndHold(v, b);
            var menu = new ContextMenu();
            var customize = new MenuItem { Header = "Personnaliser… (nom et logo)" };
            customize.Click += (_, _) => _c.CustomizeBubble(b);
            menu.Items.Add(customize);
            if (b.IsLauncher)
            {
                var addApp = new MenuItem { Header = "Ajouter une app à ce lanceur" };
                addApp.Click += (_, _) => _c.AddToLauncher(b);
                var newLauncher = new MenuItem { Header = "Nouveau lanceur" };
                newLauncher.Click += (_, _) => _c.AddLauncher();
                menu.Items.Add(addApp);
                menu.Items.Add(newLauncher);
            }
            menu.Items.Add(new Separator());
            var remove = new MenuItem { Header = "Retirer la bulle" };
            remove.Click += (_, _) => _c.RemoveBubble(b);
            menu.Items.Add(remove);
            v.ContextMenu = menu;
            _apps.Add(v);
            v.SetBadge(_badgeCounts.TryGetValue(b, out var count) ? count : 0);
            if (_progress.TryGetValue(b, out var p)) v.SetProgress(p);
        }
        _plus = new BubbleVisual(PlusSize, null, "+") { ToolTip = "Ajouter une application" };
        _plus.MouseLeftButtonUp += (_, _) => _c.ToggleSearch();

        foreach (var el in AllItems())
        {
            _canvas.Children.Add(el);
            Reset(el);
        }
        if (IsOpen && replayOpen) Open();
        else if (IsOpen) ShowInPlace();
    }

    private IEnumerable<BubbleVisual> AllItems()
    {
        foreach (var a in _apps) yield return a;
        if (_plus != null) yield return _plus;
    }

    public void Open()
    {
        if (!IsOpen) _c.Sounds.PlayOpen();
        IsOpen = true;
        UpdateMainProgress();
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
        if (IsOpen && animated) _c.Sounds.PlayClose();
        IsOpen = false;
        UpdateMainProgress();
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

    private readonly Dictionary<BubbleConfig, int> _badgeCounts = new();
    private Border? _mainBadge;

    /// <summary>Pastille de non-lus sur une bulle ; la bulle principale affiche le total de toutes les bulles.</summary>
    public void SetBadge(BubbleConfig bubble, int count, int total)
    {
        _badgeCounts[bubble] = count;
        int i = _c.Settings.Bubbles.IndexOf(bubble);
        if (i >= 0 && i < _apps.Count) _apps[i].SetBadge(count);
        UpdateBadge(_mainBadge, total);
    }

    internal static Border MakeBadge()
    {
        var text = new TextBlock { Foreground = Brushes.White, FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Background = BadgeBrush, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(9), MinWidth = 18, Height = 18, Padding = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -5, 0), Child = text, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
    }

    // ---------- Jauges de téléchargement ----------

    private static readonly Brush ProgressBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x5C, 0xE0, 0x8A)));
    private readonly Dictionary<BubbleConfig, double> _progress = new();
    private Ellipse? _mainProgress;

    /// <summary>Anneau de progression : un trait vert qui fait le tour de la bulle selon le pourcentage.</summary>
    internal static Ellipse? UpdateProgressRing(Grid host, Ellipse? ring, double? value, double size)
    {
        if (value == null)
        {
            if (ring != null) host.Children.Remove(ring);
            return null;
        }
        const double thickness = 3.5;
        double perimeter = Math.PI * (size + 4 - thickness) / thickness;
        if (ring == null)
        {
            ring = new Ellipse
            {
                Stroke = ProgressBrush, StrokeThickness = thickness, StrokeDashCap = PenLineCap.Round,
                Margin = new Thickness(-2), IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-90),
            };
            host.Children.Add(ring);
        }
        var rotate = (RotateTransform)ring.RenderTransform;
        if (value.Value < 0)
        {
            // Pourcentage inconnu : un quart d'anneau qui tourne.
            ring.StrokeDashArray = new DoubleCollection { perimeter * 0.25, perimeter };
            if (ring.Tag is not "spinning")
            {
                ring.Tag = "spinning";
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-90, 270, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            return ring;
        }
        if (ring.Tag is "spinning")
        {
            ring.Tag = null;
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            rotate.Angle = -90;
        }
        double filled = Math.Clamp(value.Value, 0, 1) * perimeter;
        ring.StrokeDashArray = new DoubleCollection { Math.Max(0.01, filled), perimeter + 1 };
        return ring;
    }

    /// <summary>Progression des téléchargements par bulle ; la bulle principale montre le plus avancé.</summary>
    public void SetProgress(Dictionary<BubbleConfig, double> progress)
    {
        _progress.Clear();
        foreach (var kv in progress) _progress[kv.Key] = kv.Value;
        var list = _c.Settings.Bubbles;
        for (int i = 0; i < _apps.Count && i < list.Count; i++)
            _apps[i].SetProgress(_progress.TryGetValue(list[i], out var v) ? v : null);
        UpdateMainProgress();
    }

    /// <summary>
    /// La bulle principale ne montre le téléchargement que quand la cascade est repliée ;
    /// une fois déployée, il n'apparaît plus que sur la bulle de l'app concernée.
    /// </summary>
    private void UpdateMainProgress()
    {
        double? value = null;
        if (!IsOpen && _progress.Count > 0)
        {
            var known = _progress.Values.Where(v => v >= 0).ToList();
            value = known.Count > 0 ? known.Max() : DownloadMonitor.Indeterminate;
        }
        _mainProgress = UpdateProgressRing(_main, _mainProgress, value, MainSize);
    }

    /// <summary>Valeur de pastille « point » : de l'activité, sans nombre connu (apps de bureau).</summary>
    public const int DotBadge = -1;

    internal static void UpdateBadge(Border? badge, int count)
    {
        if (badge == null) return;
        bool dot = count == DotBadge;
        badge.Visibility = count > 0 || dot ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)badge.Child).Text = dot ? "" : count > 99 ? "99+" : count.ToString();
        badge.MinWidth = dot ? 13 : 18;
        badge.Height = dot ? 13 : 18;
        badge.Padding = dot ? new Thickness(0) : new Thickness(4, 0, 4, 0);
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
        _mainBadge = MakeBadge();
        g.Children.Add(_mainBadge);
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
            _badge = MakeBadge();
            Children.Add(_badge);

            MouseEnter += (_, _) => Scale(this, 1.1);
            MouseLeave += (_, _) => Scale(this, 1);
        }

        private readonly Border _badge;
        private Ellipse? _holdRing;

        public void SetBadge(int count) => UpdateBadge(_badge, count);

        private Ellipse? _progressRing;

        /// <summary>Anneau vert de téléchargement (0 à 1), ou null pour le retirer.</summary>
        public void SetProgress(double? value) => _progressRing = UpdateProgressRing(this, _progressRing, value, Width);

        /// <summary>Anneau bleu clair qui se remplit autour de la bulle pendant l'appui long.</summary>
        public void StartHoldRing(TimeSpan duration)
        {
            StopHoldRing();
            const double thickness = 3;
            // Longueur du tour en « épaisseurs de trait » (unité des pointillés WPF).
            double perimeter = Math.PI * (Width - thickness) / thickness;
            _holdRing = new Ellipse
            {
                Stroke = HoldBrush,
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection { perimeter, perimeter },
                StrokeDashOffset = perimeter,
                StrokeDashCap = PenLineCap.Round,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(-90),
                IsHitTestVisible = false,
            };
            Children.Add(_holdRing);
            _holdRing.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(perimeter, 0, duration));
        }

        public void StopHoldRing()
        {
            if (_holdRing == null) return;
            Children.Remove(_holdRing);
            _holdRing = null;
        }

        /// <summary>Bulle « décrochée » : un peu plus grosse, contour bleu clair.</summary>
        public void SetDragLook(bool dragging)
        {
            Scale(this, dragging ? 1.18 : 1);
            _ring.Stroke = dragging ? HoldBrush : RingBrush;
            _ring.StrokeThickness = dragging ? 2.5 : 1.2;
            StopHoldRing();
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
