using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>
/// Contenu d'une bulle Lanceur : tes apps en tuiles, avec une recherche. Un clic lance l'app
/// normalement (dans sa propre fenêtre, pas dans une bulle) et referme le lanceur.
/// </summary>
public sealed class LauncherView : UserControl
{
    private static readonly Brush TileBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)));
    private static readonly Brush TileHover = Frozen(new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x37)));
    private static readonly Brush TileBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextMain = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));

    private readonly AppController _c;
    private readonly BubbleConfig _launcher;
    private readonly TextBox _search;
    private readonly TextBlock _placeholder;
    private readonly WrapPanel _tiles = new() { Margin = new Thickness(0, 10, 0, 0) };

    public LauncherView(AppController controller, BubbleConfig launcher)
    {
        _c = controller;
        _launcher = launcher;

        _search = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), FontSize = 13.5,
        };
        _placeholder = new TextBlock { Text = "Rechercher…", Foreground = TextSoft, FontSize = 13.5, Margin = new Thickness(13, 7, 0, 0), IsHitTestVisible = false };
        _search.TextChanged += (_, _) => Refresh();
        _search.KeyDown += (_, e) =>
        {
            // Entrée : lance la première app trouvée.
            if (e.Key == Key.Enter && Filtered().FirstOrDefault() is { } first) Launch(first);
        };
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        searchHost.Children.Add(_placeholder);

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(searchHost);
        root.Children.Add(_tiles);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _search.Text = "";
            Refresh();
        };
        Refresh();
    }

    public void FocusSearch() => _search.Focus();

    private System.Collections.Generic.IEnumerable<LauncherApp> Filtered()
    {
        var q = _search.Text.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return _launcher.Apps.Where(a => q.Length == 0 || compare.IndexOf(a.Name, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }

    public void Refresh()
    {
        _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _tiles.Children.Clear();
        foreach (var app in Filtered()) _tiles.Children.Add(AppTile(app));
        if (_search.Text.Length == 0) _tiles.Children.Add(AddTile());
    }

    private Border AppTile(LauncherApp app)
    {
        var icon = app.Kind == "Web"
            ? (string.IsNullOrEmpty(app.IconPath) ? IconLoader.CachedFavicon(app.Url) : IconLoader.Load(app.IconPath, 0, ""))
            : IconLoader.Load(app.IconPath, app.IconIndex, app.LaunchPath);
        FrameworkElement visual = icon != null
            ? new Image { Source = icon, Width = 34, Height = 34 }
            : new TextBlock { Text = app.Kind == "Web" ? "" : "", FontFamily = BubbleGlyphs.Font, FontSize = 26, Foreground = TextMain };
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);

        var tile = MakeTile(visual, app.Name, TextMain);
        tile.ToolTip = app.Name;
        tile.Tag = app;
        // Clic court : lancer. Appui long : la tuile se décroche et se range ailleurs dans la grille.
        tile.MouseLeftButtonDown += (_, e) => BeginHold(tile, e);
        tile.MouseMove += (_, e) => OnHoldMove(tile, e);
        tile.MouseLeftButtonUp += (_, _) => EndHold(tile, app);

        var menu = new ContextMenu();
        // Ranger l'app dans un autre lanceur.
        var others = _c.Settings.Bubbles.Where(b => b.IsLauncher && b != _launcher).ToList();
        if (others.Count > 0)
        {
            var move = new MenuItem { Header = "Déplacer vers" };
            foreach (var other in others)
            {
                var target = other;
                var item = new MenuItem { Header = target.Name };
                item.Click += (_, _) => _c.MoveLauncherApp(app, _launcher, target);
                move.Items.Add(item);
            }
            menu.Items.Add(move);
        }
        var remove = new MenuItem { Header = "Retirer du lanceur" };
        remove.Click += (_, _) => { _launcher.Apps.Remove(app); _c.Settings.Save(); Refresh(); };
        menu.Items.Add(remove);
        if (app.Kind != "Web" && System.IO.File.Exists(app.LaunchPath))
        {
            var reveal = new MenuItem { Header = "Ouvrir l'emplacement du fichier" };
            reveal.Click += (_, _) => _c.RevealFile(app.LaunchPath);
            menu.Items.Add(reveal);
        }
        tile.ContextMenu = menu;
        return tile;
    }

    private Border AddTile()
    {
        var plus = new TextBlock { Text = "", FontFamily = BubbleGlyphs.Font, FontSize = 22, Foreground = TextSoft };
        var tile = MakeTile(plus, "Ajouter", TextSoft);
        tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
        tile.BorderThickness = new Thickness(1);
        tile.ToolTip = "Ajouter une app ou un site à ce lanceur";
        tile.MouseLeftButtonUp += (_, _) => _c.AddToLauncher(_launcher);
        return tile;
    }

    private void Launch(LauncherApp app) => _c.LaunchFromLauncher(_launcher, app);

    // ---------- Réorganiser les tuiles (appui long puis glisser) ----------

    private static readonly Brush HoldBorder = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xDC, 0xFF)));
    private Border? _held;
    private bool _dragging, _holdCancelled;
    private Point _holdStart, _grabOffset;
    private System.Windows.Threading.DispatcherTimer? _holdTimer;

    private void BeginHold(Border tile, MouseButtonEventArgs e)
    {
        _held = tile;
        _dragging = false;
        // Pendant une recherche, la grille est filtrée : on ne réordonne pas.
        _holdCancelled = _search.Text.Length > 0;
        _holdStart = e.GetPosition(_tiles);
        tile.CaptureMouse();
        e.Handled = true;
        if (_holdCancelled) return;

        // Le contour se charge en bleu clair pendant l'appui.
        tile.BorderBrush = HoldBorder;
        tile.BorderThickness = new Thickness(2);
        tile.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.75, TimeSpan.FromMilliseconds(500)) { AutoReverse = true });
        _holdTimer?.Stop();
        _holdTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer!.Stop();
            if (_held != tile || _holdCancelled) return;
            // Tuile décrochée : elle suit la souris.
            _dragging = true;
            tile.BeginAnimation(OpacityProperty, null);
            tile.Opacity = 0.9;
            Panel.SetZIndex(tile, 10);
            var origin = tile.TranslatePoint(new Point(0, 0), _tiles);
            _grabOffset = new Point(_holdStart.X - origin.X, _holdStart.Y - origin.Y);
            tile.RenderTransform = new TranslateTransform();
        };
        _holdTimer.Start();
    }

    private void OnHoldMove(Border tile, MouseEventArgs e)
    {
        if (_held != tile) return;
        var p = e.GetPosition(_tiles);
        if (!_dragging)
        {
            if (!_holdCancelled && (p - _holdStart).Length > 10) CancelHoldLook(tile);
            return;
        }

        // La tuile la plus proche de la souris donne la nouvelle place.
        int target = -1;
        double best = double.MaxValue;
        for (int i = 0; i < _launcher.Apps.Count && i < _tiles.Children.Count; i++)
        {
            if (_tiles.Children[i] is not Border other) continue;
            var c = other.TranslatePoint(new Point(other.ActualWidth / 2, other.ActualHeight / 2), _tiles);
            if (other == tile && other.RenderTransform is TranslateTransform tt) c = new Point(c.X - tt.X, c.Y - tt.Y);
            double d = (c - p).LengthSquared;
            if (d < best) { best = d; target = i; }
        }
        int current = _tiles.Children.IndexOf(tile);
        if (target >= 0 && target != current)
        {
            var app = (LauncherApp)tile.Tag;
            _launcher.Apps.Remove(app);
            _launcher.Apps.Insert(target, app);
            _tiles.Children.Remove(tile);
            _tiles.Children.Insert(target, tile);
            _tiles.UpdateLayout();
        }

        // Suit la souris depuis sa nouvelle place dans la grille.
        tile.RenderTransform = new TranslateTransform();
        var slot = tile.TranslatePoint(new Point(0, 0), _tiles);
        tile.RenderTransform = new TranslateTransform(p.X - _grabOffset.X - slot.X, p.Y - _grabOffset.Y - slot.Y);
    }

    private void EndHold(Border tile, LauncherApp app)
    {
        if (_held != tile) return;
        _holdTimer?.Stop();
        tile.ReleaseMouseCapture();
        _held = null;
        if (_dragging)
        {
            _dragging = false;
            tile.RenderTransform = null;
            Panel.SetZIndex(tile, 0);
            tile.Opacity = 1;
            CancelHoldLook(tile);
            _c.Settings.Save();
            return;
        }
        bool cancelled = _holdCancelled && _search.Text.Length == 0;
        CancelHoldLook(tile);
        if (!cancelled) Launch(app);
    }

    private void CancelHoldLook(Border tile)
    {
        _holdCancelled = true;
        _holdTimer?.Stop();
        tile.BeginAnimation(OpacityProperty, null);
        tile.Opacity = 1;
        tile.BorderBrush = TileBorder;
        tile.BorderThickness = new Thickness(1);
    }

    private static Border MakeTile(FrameworkElement visual, string name, Brush foreground)
    {
        visual.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(visual);
        stack.Children.Add(new TextBlock
        {
            Text = name, Foreground = foreground, FontSize = 11.5, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 6, 4, 0), MaxWidth = 84,
        });
        var tile = new Border
        {
            Width = 92, Height = 86, Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(12),
            Background = TileBg, BorderBrush = TileBorder, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = stack,
        };
        tile.MouseEnter += (_, _) => tile.Background = TileHover;
        tile.MouseLeave += (_, _) => tile.Background = TileBg;
        return tile;
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
