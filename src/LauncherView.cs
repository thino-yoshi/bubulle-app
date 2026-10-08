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
        tile.MouseLeftButtonUp += (_, _) => Launch(app);

        var menu = new ContextMenu();
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
