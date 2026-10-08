using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Petite barre de recherche qui s'ouvre à côté de la bulle « + ».</summary>
public sealed class SearchWindow : Window
{
    private const double PanelWidth = 300;
    private const int MaxResults = 8;

    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Func<AppEntry, bool> _exclude;
    private readonly bool _growsUp;
    private readonly double _anchorY;
    private List<AppEntry> _all = new();
    private List<AppEntry> _open = new();
    private bool _closing;

    public event Action<AppEntry>? Picked;

    /// <param name="anchorY">Haut de la barre si elle s'ouvre vers le bas, bas de la barre si elle s'ouvre vers le haut.</param>
    public SearchWindow(Func<AppEntry, bool> exclude, double left, double anchorY, bool growsUp)
    {
        _exclude = exclude;
        _anchorY = anchorY;
        _growsUp = growsUp;

        Title = "Bulles — recherche";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        Left = left;
        Top = anchorY;

        var white = Brushes.White;
        _box = new TextBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            Foreground = white,
            CaretBrush = white,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 7, 10, 7),
            FontSize = 14,
        };
        _placeholder = new TextBlock
        {
            Text = "Rechercher une app…",
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            FontSize = 14,
            Margin = new Thickness(13, 7, 0, 0),
            IsHitTestVisible = false,
        };
        var boxHost = new Grid();
        boxHost.Children.Add(_box);
        boxHost.Children.Add(_placeholder);

        _status = new TextBlock
        {
            Text = "Chargement des applications…",
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            FontSize = 13,
            Margin = new Thickness(8, 8, 8, 4),
        };
        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = white,
            Margin = new Thickness(0, 6, 0, 0),
            MaxHeight = 420,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        var stack = new StackPanel();
        stack.Children.Add(boxHost);
        stack.Children.Add(_status);
        stack.Children.Add(_list);

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x16, 0x1A, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = stack,
        };

        _box.TextChanged += (_, _) => Refresh();
        _box.PreviewKeyDown += OnBoxKey;
        _list.MouseLeftButtonUp += (_, _) => PickSelected();
        Deactivated += (_, _) => SafeClose();
        SizeChanged += (_, _) => { if (_growsUp) Top = _anchorY - ActualHeight; };
        Loaded += async (_, _) =>
        {
            Activate();
            _box.Focus();
            _open = WindowFinder.OpenAppPaths().Select(AppCatalog.FromExe).ToList();
            Refresh();
            _all = await AppCatalog.GetAsync() ?? new List<AppEntry>();
            Refresh();
        };
        Closing += (_, _) => _closing = true;
    }

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void Refresh()
    {
        string q = _box.Text.Trim();
        _placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var web = Filter(WebPresets.All, q).Take(q.Length == 0 ? 4 : 3).ToList();
        var url = WebPresets.AsUrl(q);
        if (url != null && !web.Any(w => w.Url == url))
            web.Insert(0, new AppEntry { Kind = "Web", Name = WebPresets.HostOf(url), Url = url });
        var open = Filter(_open, q).Take(5).ToList();
        var apps = Filter(_all, q).Take(MaxResults).ToList();

        _list.Items.Clear();
        AddSection("Web · il reste juste à se connecter", web);
        AddSection("Fenêtres ouvertes", open);
        AddSection("Applications du PC", apps);
        // Toujours proposés : n'importe quel programme du PC, ou n'importe quel site web.
        AddSection("Outils Bulles", Filter(new[] { MixerEntry }, q).ToList());
        _list.Items.Add(SectionHeader("Ajouter le tien"));
        _list.Items.Add(MakeItem(BrowseEntry));
        _list.Items.Add(MakeItem(AddSiteEntry));
        _list.SelectedIndex = -1;
        Move(+1);

        bool loading = _all.Count == 0 && !AppCatalog.GetAsync().IsCompleted;
        bool empty = web.Count == 0 && open.Count == 0 && apps.Count == 0;
        _status.Text = loading ? "Chargement des applications…" : "Aucune app trouvée";
        _status.Visibility = loading || empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddSection(string title, List<AppEntry> entries)
    {
        if (entries.Count == 0) return;
        _list.Items.Add(SectionHeader(title));
        foreach (var app in entries) _list.Items.Add(MakeItem(app));
    }

    /// <summary>Entrée spéciale « Parcourir… » : ouvre le sélecteur de fichiers.</summary>
    public static readonly AppEntry BrowseEntry = new() { Kind = "Browse", Name = "Parcourir… (un programme ou un raccourci)" };

    /// <summary>Le mélangeur audio de Bulles, à ajouter comme une bulle.</summary>
    public static readonly AppEntry MixerEntry = new() { Kind = "Mixer", Name = "Mélangeur audio" };

    /// <summary>Entrée spéciale « Ajouter un site web… » : ouvre la fenêtre nom + adresse.</summary>
    public static readonly AppEntry AddSiteEntry = new() { Kind = "AddSite", Name = "Ajouter un site web… (ta propre page)" };

    private IEnumerable<AppEntry> Filter(IEnumerable<AppEntry> source, string q)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return source
            .Where(a => !_exclude(a))
            .Select(a => (app: a, pos: q.Length == 0 ? 0 : compare.IndexOf(a.Name, q, opts)))
            .Where(x => x.pos >= 0)
            .OrderBy(x => x.pos == 0 ? 0 : 1)
            .Select(x => x.app);
    }

    private static ListBoxItem SectionHeader(string text) => new()
    {
        Content = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) },
        IsEnabled = false,
        Focusable = false,
        Padding = new Thickness(6, 6, 6, 2),
    };

    private static ListBoxItem MakeItem(AppEntry app)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = app.IsWeb ? IconLoader.CachedFavicon(app.Url) : IconLoader.Load(app.IconPath, app.IconIndex, app.LaunchPath);
        if (icon != null)
            row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
        else
            row.Children.Add(new TextBlock
            {
                Text = app.Kind == "Mixer" ? LauncherWindow.MixerGlyph : app.Kind == "AddSite" ? "" : app.Kind == "Browse" ? "" : app.IsWeb ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16, Width = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        row.Children.Add(new TextBlock { Text = app.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return new ListBoxItem { Content = row, Tag = app, Padding = new Thickness(6, 5, 6, 5), Foreground = Brushes.White };
    }

    /// <summary>Pré-télécharge les icônes des apps web préréglées pour la prochaine ouverture.</summary>
    public static void WarmWebIcons()
    {
        foreach (var w in WebPresets.All) _ = IconLoader.FetchFaviconAsync(w.Url);
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                PickSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Déplace la sélection en sautant les titres de section.</summary>
    private void Move(int step)
    {
        for (int i = _list.SelectedIndex + step; i >= 0 && i < _list.Items.Count; i += step)
        {
            if (_list.Items[i] is ListBoxItem { Tag: AppEntry })
            {
                _list.SelectedIndex = i;
                _list.ScrollIntoView(_list.Items[i]);
                return;
            }
        }
    }

    private void PickSelected()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: AppEntry app })
        {
            Picked?.Invoke(app);
            SafeClose();
        }
    }
}
