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
        Title = "Bubulle";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = StripWidth;
        Content = _canvas;

        _main = CreateMainBubble();
        _modeSwitch = CreateModeSwitch();
        _canvas.Children.Add(_modeSwitch);
        Panel.SetZIndex(_modeSwitch, 11);
        _canvas.Children.Add(_main);
        Panel.SetZIndex(_main, 10);
        ReloadNotificationGif();

        SourceInitialized += (_, _) =>
        {
            // Fenêtre outil : absente d'Alt+Tab.
            var h = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_TOOLWINDOW);
            // Alt+F4 pendant que la bande a le focus (après un clic sur une bulle) : ignoré, sinon c'est
            // tout Bubulle qui se fermait. Une vraie demande de fermeture (mise à jour, Windows) passe toujours.
            HwndSource.FromHwnd(h)?.AddHook(IgnoreAltF4);
        };
        DpiChanged += (_, _) => UpdatePlacement();
        // Demande de fermeture externe (mise à jour, fermeture de session) : on quitte proprement
        // pour rendre les fenêtres gardées dans les bulles.
        Closing += (_, _) =>
        {
            App.Session("Demande de fermeture reçue (mise à jour, Windows ou autre programme)");
            _c.Quit();
        };
    }

    /// <summary>Alt+F4 arrive en WM_SYSCOMMAND / SC_CLOSE ; une fermeture par programme arrive en WM_CLOSE direct.</summary>
    internal static IntPtr IgnoreAltF4(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112, SC_CLOSE = 0xF060;
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_CLOSE) handled = true;
        return IntPtr.Zero;
    }

    private bool ExpandsDown => _mainY < Height / 2;

    /// <summary>Centre de la bulle n° index ; les mini-bulles des bulles au-dessus la décalent.</summary>
    private double TargetCenter(int index) => _mainY + (ExpandsDown ? 1 : -1) * (FirstOffset + index * Spacing + MinisBefore(index) * MiniStep);

    // ---------- Mini-bulles (fenêtres liées : stream détaché, lecteur miniature…) ----------

    private const double MiniSize = 26, MiniFirst = 38, MiniStep = 30;
    private Dictionary<BubbleConfig, List<(BubbleConfig Child, bool Unfolded)>> _linked = new();
    private readonly List<BubbleVisual> _minis = new();
    private readonly Dictionary<BubbleVisual, (int Parent, int Slot)> _miniInfo = new();

    private int MinisOf(int index) =>
        index < _c.Settings.Bubbles.Count && _linked.TryGetValue(_c.Settings.Bubbles[index], out var list) ? list.Count : 0;

    private int MinisBefore(int index)
    {
        int n = 0;
        for (int j = 0; j < index && j < _c.Settings.Bubbles.Count; j++) n += MinisOf(j);
        return n;
    }

    private double MiniCenter(int parent, int slot) => TargetCenter(parent) + (ExpandsDown ? 1 : -1) * (MiniFirst + slot * MiniStep);

    private double CenterOf(BubbleVisual el, int index) =>
        _miniInfo.TryGetValue(el, out var info) ? MiniCenter(info.Parent, info.Slot) : TargetCenter(index);

    /// <summary>Position d'une mini-bulle (pour placer sa petite bulle-fenêtre juste à côté).</summary>
    public (double CenterY, double StripLeft, double StripRight) LinkedAnchorDip(BubbleConfig parent, int slot)
    {
        int i = Math.Max(0, _c.Settings.Bubbles.IndexOf(parent));
        return (Top + MiniCenter(i, slot), Left, Left + StripWidth);
    }

    /// <summary>Les fenêtres liées changent : les mini-bulles sont refaites et la cascade se réorganise.</summary>
    public void SetLinked(Dictionary<BubbleConfig, List<(BubbleConfig Child, bool Unfolded)>> linked)
    {
        _linked = linked;
        Rebuild(replayOpen: false);
    }

    private void BuildMinis()
    {
        _minis.Clear();
        _miniInfo.Clear();
        var bubbles = _c.Settings.Bubbles;
        for (int i = 0; i < bubbles.Count; i++)
        {
            if (!_linked.TryGetValue(bubbles[i], out var list)) continue;
            var icon = BubbleFrame.IconFor(bubbles[i]);
            for (int k = 0; k < list.Count; k++)
            {
                var (child, unfolded) = list[k];
                var v = new BubbleVisual(MiniSize, icon, icon == null ? "\uE8A7" : null) { ToolTip = child.Name };
                v.SetLinkedLook(unfolded);
                var target = child;
                v.MouseLeftButtonUp += (_, e) => { e.Handled = true; _c.ToggleLinked(target); };
                _minis.Add(v);
                _miniInfo[v] = (i, k);
            }
        }
    }

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

    /// <summary>Engrenage noir à 8 grosses dents avec un trou au centre (logo des paramètres, dans les deux modes).</summary>
    private static readonly DrawingImage GearLogo = Frozen(new DrawingImage(new DrawingGroup
    {
        Children =
        {
            // Cadre transparent plus grand que l'engrenage : il est dessiné un peu plus petit dans la bulle.
            new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(-12, -12, 124, 124))),
            new GeometryDrawing(Brushes.Black, null, Gear()),
        },
    }));

    private static Geometry Gear()
    {
        const int teeth = 8;
        const double outer = 50, root = 37, hole = 15;
        var gear = new StreamGeometry();
        using (var c = gear.Open())
        {
            // Chaque dent : un plateau sur le rayon extérieur, des flancs légèrement inclinés, un creux sur le rayon intérieur.
            double step = 2 * Math.PI / teeth;
            Point At(double r, double a) => new(50 + r * Math.Cos(a), 50 + r * Math.Sin(a));
            for (int i = 0; i < teeth; i++)
            {
                // Décalé d'une demi-dent : un creux pile en haut (au nord).
                double a = i * step - Math.PI / 2 + step / 2;
                var points = new[]
                {
                    At(root, a - step * 0.30), At(outer, a - step * 0.20), At(outer, a + step * 0.20), At(root, a + step * 0.30),
                };
                if (i == 0) c.BeginFigure(points[0], true, true);
                else c.LineTo(points[0], true, true);
                c.LineTo(points[1], true, true);
                c.LineTo(points[2], true, true);
                c.LineTo(points[3], true, true);
                c.ArcTo(At(root, a + step * 0.70), new Size(root, root), 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        var result = new CombinedGeometry(GeometryCombineMode.Exclude, gear, new EllipseGeometry(new Point(50, 50), hole, hole));
        result.Freeze();
        return result;
    }

    /// <summary>Position de la bulle engrenage : de l'autre côté de la bulle principale que la cascade.</summary>
    private double SettingsCenter =>
        Math.Clamp(_mainY + (ExpandsDown ? -1 : 1) * FirstOffset, AppSize / 2 + 4, Height - AppSize / 2 - 4);

    public void ShowSettingsBubble()
    {
        if (_settings == null)
        {
            _settings = new BubbleVisual(AppSize, GearLogo, null) { ToolTip = "Paramètres" };
            _settings.MouseLeftButtonUp += (_, _) => _c.ToggleSettings();
            _canvas.Children.Add(_settings);
        }
        _settings.SetMonochromeLook();
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
            PlaceCenter(el, CenterOf(el, i++));
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
            OpenOnDragHover(v, () => _c.OpenForDrop(b));
            var menu = new ContextMenu();
            if (b.IsWindowApp)
            {
                // Passage manuel d'un mode à l'autre, pour cette app seulement. Le texte est choisi juste
                // avant l'ouverture du menu (sinon il pouvait s'afficher vide, ou périmé après un changement de mode).
                var mode = new MenuItem { Header = "Ouvrir dans sa bulle" };
                mode.Click += (_, _) =>
                {
                    if (_c.IsInBubble(b)) _c.ReleaseToWindow(b);
                    else _c.OpenInBubble(b);
                };
                v.ContextMenuOpening += (_, _) => mode.Header = _c.IsInBubble(b) ? "Sortir en fenêtre normale" : "Ouvrir dans sa bulle";
                menu.Items.Add(mode);
                menu.Items.Add(new Separator());
            }
            else if (b.IsWeb)
            {
                // Les sites restent en bulle, mais on peut toujours les ouvrir dans le navigateur.
                var browser = new MenuItem { Header = "Ouvrir dans mon navigateur" };
                browser.Click += (_, _) => _c.OpenInBrowser(b);
                menu.Items.Add(browser);
                menu.Items.Add(new Separator());
            }
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
        BuildMinis();
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
        foreach (var m in _minis) yield return m;
    }

    public void Open()
    {
        if (!IsOpen) _c.Sounds.PlayOpen();
        IsOpen = true;
        UpdateMainProgress();
        // Cascade déployée : les messages sont vus, l'animation s'arrête.
        _notifyPending = false;
        UpdateNotifyAnimation();
        UpdateActiveArrow();
        ShowModeSwitch(true);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        int i = 0;
        foreach (var el in AllItems())
        {
            el.Visibility = Visibility.Visible;
            Animate(el, CenterOf(el, i), 1, i * 35, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
            i++;
        }
    }

    public void Close(bool animated = true)
    {
        if (IsOpen && animated) _c.Sounds.PlayClose();
        IsOpen = false;
        UpdateMainProgress();
        UpdateActiveArrow();
        ShowModeSwitch(false);
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
        int old = _badgeCounts.GetValueOrDefault(bubble);
        _badgeCounts[bubble] = count;
        int i = _c.Settings.Bubbles.IndexOf(bubble);
        if (i >= 0 && i < _apps.Count) _apps[i].SetBadge(count);
        UpdateBadge(_mainBadge, total);

        // Nouveau message (plus de non-lus, ou un point qui apparaît) pendant que la cascade est repliée.
        static int Weight(int c) => c == DotBadge ? 1 : Math.Max(0, c);
        if (!IsOpen && Weight(count) > Weight(old)) _notifyPending = true;
        if (total == 0) _notifyPending = false;
        UpdateNotifyAnimation();
    }

    // ---------- Animation de notification ----------

    private Image? _notifyImage;
    private GifAnimation? _notifyGif;
    private bool _notifyPending;
    private bool _notifyPlaying;

    /// <summary>GIF de base fourni avec l'app.</summary>
    public static string BundledNotificationGif => System.IO.Path.Combine(AppContext.BaseDirectory, "notification.gif");

    /// <summary>Recharge le GIF choisi dans les paramètres (vide = celui fourni avec l'app).</summary>
    public void ReloadNotificationGif()
    {
        var path = string.IsNullOrEmpty(_c.Settings.NotificationGifPath) ? BundledNotificationGif : _c.Settings.NotificationGifPath;
        _notifyGif = GifAnimation.Load(path, MainSize * 4);
        _notifyPlaying = false;
        UpdateNotifyAnimation();
    }

    /// <summary>Le GIF tourne en boucle tant qu'un message n'a pas été vu (cascade repliée).</summary>
    private void UpdateNotifyAnimation()
    {
        if (_notifyImage == null) return;
        bool play = _notifyPending && !IsOpen && _c.Settings.NotifyAnimation && _notifyGif != null;
        if (play == _notifyPlaying) return;
        _notifyPlaying = play;
        if (play) _notifyGif!.Play(_notifyImage, true);
        else _notifyImage.BeginAnimation(Image.SourceProperty, null);
        _notifyImage.Visibility = play ? Visibility.Visible : Visibility.Collapsed;
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
        int index = bubble == null ? -1 : list.IndexOf(bubble);
        _arrowY = index >= 0 && index < _apps.Count ? TargetCenter(index)
            : bubble != null && _settings is { Visibility: Visibility.Visible } ? SettingsCenter : null;
        UpdateActiveArrow();
    }

    // ---------- Flèche de la bulle active (façon SAO) ----------

    /// <summary>Écart laissé entre la bande et la bulle-fenêtre : la pointe de la flèche touche la fenêtre.</summary>
    public const double ArrowRoom = 1;
    private const double ArrowLength = 13, ArrowHalf = 10;
    public static readonly Brush ActiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x18)));
    private Polygon? _activeArrow;
    private double? _arrowY;

    private void UpdateActiveArrow()
    {
        bool show = _arrowY != null && (IsOpen || _settings is { Visibility: Visibility.Visible });
        if (!show)
        {
            if (_activeArrow != null) _activeArrow.Visibility = Visibility.Collapsed;
            return;
        }
        if (_activeArrow == null)
        {
            _activeArrow = new Polygon { Fill = ActiveBrush, IsHitTestVisible = false };
            _canvas.Children.Add(_activeArrow);
            Panel.SetZIndex(_activeArrow, 5);
        }
        double y = _arrowY!.Value;
        // Bulles à droite : la fenêtre est à gauche, la flèche pointe vers la gauche (et inversement).
        _activeArrow.Points = _c.Settings.IsLeft
            ? new PointCollection { new(StripWidth - ArrowLength, y - ArrowHalf), new(StripWidth, y), new(StripWidth - ArrowLength, y + ArrowHalf) }
            : new PointCollection { new(ArrowLength, y - ArrowHalf), new(0, y), new(ArrowLength, y + ArrowHalf) };
        bool wasHidden = _activeArrow.Visibility != Visibility.Visible;
        _activeArrow.Visibility = Visibility.Visible;
        if (wasHidden) _activeArrow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(120) });
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

    /// <summary>Logo de la bulle principale : un rond blanc cerclé de noir avec une silhouette, en vectoriel (net à toute taille).</summary>
    private static readonly DrawingImage MainLogo = Frozen(new DrawingImage(new DrawingGroup
    {
        Children =
        {
            new GeometryDrawing(Brushes.Black, null, new EllipseGeometry(new Point(620, 620), 597, 597)),
            new GeometryDrawing(Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7))), null, new EllipseGeometry(new Point(620, 620), 520, 520)),
            new GeometryDrawing(Brushes.Black, null, Geometry.Parse(
                "M588,411 L657,411 722,476 722,540 717,545 717,612 674,655 674,693 855,738 878,840 " +
                "368,840 386,742 570,693 570,657 527,614 527,560 521,555 521,476 Z")),
        },
    }));

    // ---------- Profils : logo de la bulle principale et petite bulle de changement de mode ----------

    private static readonly Brush LogoOrange = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xB5, 0x00)));
    private static readonly Brush LogoOrangeFill = Frozen(new LinearGradientBrush(Color.FromRgb(0xF7, 0xB5, 0x00), Color.FromRgb(0xE3, 0x95, 0x02), 90));
    private static readonly Brush LogoWhite = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)));
    private static readonly Geometry OnePerson = Frozen(Geometry.Parse(
        "M588,411 L657,411 722,476 722,540 717,545 717,612 674,655 674,693 855,738 878,840 " +
        "368,840 386,742 570,693 570,657 527,614 527,560 521,555 521,476 Z"));
    private static readonly Geometry TwoPeople = Frozen(Geometry.Parse(
        "M460,448 L528,448 584,510 578,626 532,668 531,690 457,710 430,838 252,838 276,749 443,703 442,673 399,628 393,510 Z " +
        "M689,414 L771,414 830,478 820,611 772,656 772,695 958,743 978,838 462,838 484,743 672,695 672,660 625,612 619,476 Z"));

    /// <summary>Logo rond : blanc cerclé de noir (mode Bubulle) ou orange cerclé de blanc (mode bureau).</summary>
    private static DrawingImage Logo(Geometry people, bool orange)
    {
        var center = new Point(620, 620);
        var group = new DrawingGroup();
        if (orange)
        {
            group.Children.Add(new GeometryDrawing(LogoOrange, null, new EllipseGeometry(center, 610, 610)));
            group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(center, 576, 576)));
            group.Children.Add(new GeometryDrawing(LogoOrangeFill, null, new EllipseGeometry(center, 516, 516)));
            group.Children.Add(new GeometryDrawing(Brushes.White, null, people));
        }
        else
        {
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, new EllipseGeometry(center, 597, 597)));
            group.Children.Add(new GeometryDrawing(LogoWhite, null, new EllipseGeometry(center, 520, 520)));
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, people));
        }
        return Frozen(new DrawingImage(group));
    }

    private static readonly DrawingImage DesktopLogo = Logo(OnePerson, orange: true);
    private static readonly DrawingImage SwitchToDesktop = Logo(TwoPeople, orange: true);
    private static readonly DrawingImage SwitchToBubble = Logo(TwoPeople, orange: false);
    private const double SwitchSize = MainSize / 2;

    private Image? _mainLogo;
    private readonly Grid _modeSwitch;
    private Image? _switchLogo;

    /// <summary>Petite bulle (moitié de la bulle principale), en diagonale : un clic change de mode.</summary>
    private Grid CreateModeSwitch()
    {
        _switchLogo = new Image { Stretch = Stretch.Uniform };
        var g = new Grid
        {
            Width = SwitchSize, Height = SwitchSize, Cursor = Cursors.Hand, Visibility = Visibility.Hidden, Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
        };
        g.Children.Add(_switchLogo);
        g.MouseEnter += (_, _) => BubbleVisual.Scale(g, 1.15);
        g.MouseLeave += (_, _) => BubbleVisual.Scale(g, 1);
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; _c.ToggleDesktopMode(); };
        UpdateModeLook(g);
        return g;
    }

    public void UpdateModeLook() => UpdateModeLook(_modeSwitch);

    private void UpdateModeLook(Grid modeSwitch)
    {
        bool desktop = _c.Settings.DesktopMode;
        if (_mainLogo != null) _mainLogo.Source = desktop ? DesktopLogo : MainLogo;
        if (_switchLogo != null) _switchLogo.Source = desktop ? SwitchToBubble : SwitchToDesktop;
        modeSwitch.ToolTip = desktop ? "Passer en mode Bubulle (les apps s'ouvrent en bulle)" : "Passer en mode bureau (les apps s'ouvrent en fenêtre normale)";
    }

    /// <summary>
    /// La petite bulle se place en diagonale de la bulle principale, côté intérieur de l'écran,
    /// à l'opposé de la cascade (au-dessus quand la cascade descend).
    /// </summary>
    private void ShowModeSwitch(bool show)
    {
        if (show)
        {
            double cx = _c.Settings.IsLeft ? StripWidth - SwitchSize / 2 : SwitchSize / 2;
            double cy = _mainY + (ExpandsDown ? -1 : 1) * 35;
            Canvas.SetLeft(_modeSwitch, cx - SwitchSize / 2);
            Canvas.SetTop(_modeSwitch, cy - SwitchSize / 2);
            _modeSwitch.Visibility = Visibility.Visible;
            _modeSwitch.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(80) });
            var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            var scale = (ScaleTransform)_modeSwitch.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => { if (!IsOpen) _modeSwitch.Visibility = Visibility.Hidden; };
            _modeSwitch.BeginAnimation(OpacityProperty, fade);
        }
    }

    private Grid CreateMainBubble()
    {
        var g = new Grid { Width = MainSize, Height = MainSize, Cursor = Cursors.Hand, ToolTip = "Bubulle" };
        _mainLogo = new Image { Source = _c.Settings.DesktopMode ? DesktopLogo : MainLogo, Stretch = Stretch.Uniform };
        g.Children.Add(_mainLogo);
        _notifyImage = new Image
        {
            Stretch = Stretch.Uniform, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Clip = new EllipseGeometry(new Point(MainSize / 2.0, MainSize / 2.0), MainSize / 2.0, MainSize / 2.0),
        };
        RenderOptions.SetBitmapScalingMode(_notifyImage, BitmapScalingMode.HighQuality);
        g.Children.Add(_notifyImage);
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
        OpenOnDragHover(g, () => { if (!IsOpen) Open(); });
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
        // Changer de mode aussi depuis le clic droit (pas seulement avec la petite bulle en diagonale).
        var switchMode = new MenuItem();
        switchMode.Click += (_, _) => _c.ToggleDesktopMode();
        g.ContextMenuOpening += (_, _) => switchMode.Header = _c.Settings.DesktopMode
            ? "Passer en mode Bubulle (apps en bulle)" : "Passer en mode bureau (fenêtres normales)";
        menu.Items.Add(switchMode);
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Paramètres" };
        settings.Click += (_, _) => _c.OpenSettings();
        var quit = new MenuItem { Header = "Quitter Bubulle" };
        quit.Click += (_, _) => { App.Session("Quitter (clic droit sur la bulle principale)"); _c.Quit(); };
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

    /// <summary>
    /// Glisser-déposer entre bulles : tenir un fichier (ou un texte, une image…) au-dessus d'une bulle
    /// pendant un court instant l'ouvre, comme les boutons de la barre des tâches de Windows.
    /// La bulle principale, elle, déploie la cascade.
    /// </summary>
    private static void OpenOnDragHover(FrameworkElement target, Action open)
    {
        target.AllowDrop = true;
        DispatcherTimer? timer = null;
        void Stop()
        {
            timer?.Stop();
            timer = null;
        }
        target.DragEnter += (_, e) =>
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            Stop();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            timer.Tick += (_, _) =>
            {
                Stop();
                open();
            };
            timer.Start();
        };
        target.DragOver += (_, e) => { e.Effects = DragDropEffects.None; e.Handled = true; };
        target.DragLeave += (_, _) => Stop();
        target.Drop += (_, e) => { Stop(); e.Handled = true; };
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class BubbleVisual : Grid
    {
        private readonly Ellipse _ring, _fill;
        private readonly FrameworkElement? _content;
        private Brush? _glyphBrush;
        private Ellipse? _activeOutline;

        public BubbleVisual(double size, ImageSource? icon, string? glyph)
        {
            Width = size;
            Height = size;
            Cursor = Cursors.Hand;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = new ScaleTransform(1, 1);

            _fill = new Ellipse { Fill = BubbleFill };
            Children.Add(_fill);
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

        /// <summary>Mini-bulle d'une fenêtre liée : contour orange, remplie d'orange quand sa fenêtre est dépliée.</summary>
        public void SetLinkedLook(bool unfolded)
        {
            _ring.Stroke = ActiveBrush;
            _ring.StrokeThickness = 1.6;
            _fill.Fill = unfolded ? ActiveBrush : BubbleFill;
        }

        /// <summary>Bulle des paramètres : blanche cerclée de noir, engrenage noir (comme le logo de la bulle principale).</summary>
        public void SetMonochromeLook()
        {
            _fill.Fill = MonoWhite;
            _ring.Stroke = Brushes.Black;
            _ring.StrokeThickness = 3.5;
            if (_content is TextBlock text) text.Foreground = Brushes.Black;
        }

        private static readonly Brush MonoWhite = Frozen(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)));

        /// <summary>Bulle ouverte, façon SAO : remplie d'orange, cerclée de blanc puis d'orange.</summary>
        public void SetActive(bool active)
        {
            _fill.Fill = active ? ActiveBrush : BubbleFill;
            _ring.Stroke = active ? Brushes.White : RingBrush;
            _ring.StrokeThickness = active ? 3 : 1.2;
            if (_content is TextBlock text)
            {
                _glyphBrush ??= text.Foreground;
                text.Foreground = active ? Brushes.White : _glyphBrush;
            }
            if (active && _activeOutline == null)
            {
                _activeOutline = new Ellipse { Stroke = ActiveBrush, StrokeThickness = 2, Margin = new Thickness(-2.5), IsHitTestVisible = false };
                Children.Add(_activeOutline);
            }
            else if (!active && _activeOutline != null)
            {
                Children.Remove(_activeOutline);
                _activeOutline = null;
            }
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
