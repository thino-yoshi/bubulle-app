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
    private const int MaxResults = 6;

    private readonly TextBox _box;
    private readonly TextBlock _placeholder;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly Func<AppEntry, bool> _exclude;
    private readonly bool _growsUp;
    private readonly double _anchorY;
    private List<AppEntry> _all = new();
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
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

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
        if (_all.Count == 0 && !AppCatalog.GetAsync().IsCompleted) return;

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        var results = _all
            .Where(a => !_exclude(a))
            .Select(a => (app: a, pos: q.Length == 0 ? 0 : compare.IndexOf(a.Name, q, opts)))
            .Where(x => x.pos >= 0)
            .OrderBy(x => x.pos == 0 ? 0 : 1)
            .ThenBy(x => x.app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(x => x.app)
            .ToList();

        _list.Items.Clear();
        foreach (var app in results) _list.Items.Add(MakeItem(app));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        _status.Text = results.Count == 0 ? "Aucune app trouvée" : "";
        _status.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ListBoxItem MakeItem(AppEntry app)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconLoader.Load(app.IconPath, app.IconIndex, app.LaunchPath);
        if (icon != null) row.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
        row.Children.Add(new TextBlock { Text = app.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return new ListBoxItem { Content = row, Tag = app, Padding = new Thickness(6, 5, 6, 5), Foreground = Brushes.White };
    }

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++;
                e.Handled = true;
                break;
            case Key.Up:
                if (_list.SelectedIndex > 0) _list.SelectedIndex--;
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

    private void PickSelected()
    {
        if (_list.SelectedItem is ListBoxItem { Tag: AppEntry app })
        {
            Picked?.Invoke(app);
            SafeClose();
        }
    }
}
