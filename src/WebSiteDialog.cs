using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Petite fenêtre « Ajouter un site web » : un nom et une adresse, et le site devient une bulle.</summary>
public sealed class WebSiteDialog : Window
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly TextBlock _error;

    /// <summary>Le site choisi, une fois validé.</summary>
    public AppEntry? Result { get; private set; }

    public WebSiteDialog()
    {
        Title = "Bulles — ajouter un site web";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _url = Field();
        _name = Field();
        _error = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)),
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        _url.TextChanged += (_, _) => _error.Visibility = Visibility.Collapsed;

        var add = Button("Ajouter", primary: true);
        var cancel = Button("Annuler", primary: false);
        add.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(add);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Ajouter un site web", Foreground = Fg, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        stack.Children.Add(Label("Adresse du site"));
        stack.Children.Add(Placeholder(_url, "music.youtube.com"));
        stack.Children.Add(_error);
        stack.Children.Add(Label("Nom de la bulle (facultatif)"));
        stack.Children.Add(Placeholder(_name, "YouTube Music"));
        stack.Children.Add(buttons);

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x16, 0x1A, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B)),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(18),
            Child = stack,
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Submit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        // La fenêtre se déplace en la tenant par n'importe où.
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) DragMove(); };
        Loaded += (_, _) => { Activate(); _url.Focus(); };
    }

    private void Submit()
    {
        var url = WebPresets.AsUrl(_url.Text);
        if (url == null)
        {
            _error.Text = "Cette adresse n'est pas valide. Exemple : music.youtube.com";
            _error.Visibility = Visibility.Visible;
            _url.Focus();
            return;
        }
        var name = _name.Text.Trim();
        Result = new AppEntry { Kind = "Web", Url = url, Name = name.Length > 0 ? name : NameFromHost(url) };
        Close();
    }

    /// <summary>Nom par défaut : l'adresse du site sans « www. » (ex. « music.youtube.com »).</summary>
    private static string NameFromHost(string url)
    {
        var host = WebPresets.HostOf(url);
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static TextBox Field() => new()
    {
        Background = FieldBg,
        Foreground = Fg,
        CaretBrush = Fg,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, 7, 10, 7),
        FontSize = 14,
    };

    /// <summary>Texte d'exemple grisé affiché tant que le champ est vide.</summary>
    private static Grid Placeholder(TextBox box, string example)
    {
        var hint = new TextBlock { Text = example, Foreground = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)), FontSize = 14, Margin = new Thickness(13, 7, 0, 0), IsHitTestVisible = false };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };

    private static Button Button(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(16, 6, 16, 6),
        Margin = new Thickness(8, 0, 0, 0),
        Foreground = Fg,
        Background = primary ? new SolidColorBrush(LauncherWindow.Accent) : FieldBg,
        BorderThickness = new Thickness(0),
        Cursor = Cursors.Hand,
    };
}
