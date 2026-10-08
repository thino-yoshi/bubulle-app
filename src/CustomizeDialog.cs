using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Logos proposés pour les bulles (police d'icônes de Windows).</summary>
public static class BubbleGlyphs
{
    public const string Grid = "";

    public static readonly IReadOnlyList<(string Glyph, string Name)> All = new[]
    {
        (Grid, "Grille"), ("", "Manette"), ("", "Outils"), ("", "Musique"), ("", "Vidéo"),
        ("", "Palette"), ("", "Code"), ("", "École"), ("", "Travail"), ("", "Étoile"),
        ("", "Cœur"), ("", "Dossier"), ("", "Éclair"), ("", "Monde"), ("", "Drapeau"),
        ("", "Image"), ("", "Calculatrice"), ("", "Nuage"),
    };

    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>« Personnaliser… » : nouveau nom et logo d'une bulle.</summary>
public sealed class CustomizeDialog : Window
{
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TileBg = new SolidColorBrush(Color.FromRgb(0x1F, 0x25, 0x30));
    private static readonly Brush Accent = new SolidColorBrush(LauncherWindow.Accent);

    private readonly TextBox _name;
    private readonly WrapPanel _icons = new();
    private readonly bool _hasOriginalIcon;
    private string _glyph;

    public bool Saved { get; private set; }
    public string ResultName => _name.Text.Trim();
    public string ResultGlyph => _glyph;

    public CustomizeDialog(BubbleConfig bubble)
    {
        Title = "Bubulle — personnaliser";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _glyph = bubble.Glyph;
        // Une app ou un site a sa propre icône, qu'on peut garder ; un lanceur ou le mélangeur non.
        _hasOriginalIcon = bubble.IsWindowApp || bubble.IsWeb;

        _name = new TextBox
        {
            Text = bubble.Name, Background = FieldBg, Foreground = Brushes.White, CaretBrush = Brushes.White,
            BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), FontSize = 14,
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Personnaliser la bulle", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(Label("Nom"));
        stack.Children.Add(_name);
        stack.Children.Add(Label("Logo"));
        stack.Children.Add(_icons);
        RenderIcons(bubble);

        var cancel = Button("Annuler", false);
        var save = Button("Enregistrer", true);
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
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
            if (e.Key == Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) DragMove(); };
        Loaded += (_, _) => { Activate(); _name.Focus(); _name.SelectAll(); };
    }

    private void Save()
    {
        if (ResultName.Length == 0) { _name.Focus(); return; }
        Saved = true;
        Close();
    }

    private void RenderIcons(BubbleConfig bubble)
    {
        _icons.Children.Clear();
        if (_hasOriginalIcon)
        {
            var original = BubbleFrame.IconFor(new BubbleConfig { Kind = bubble.Kind, Url = bubble.Url, IconPath = bubble.IconPath, IconIndex = bubble.IconIndex, LaunchPath = bubble.LaunchPath });
            FrameworkElement content = original != null
                ? new Image { Source = original, Width = 22, Height = 22 }
                : new TextBlock { Text = "Auto", Foreground = Brushes.White, FontSize = 11 };
            _icons.Children.Add(Tile(content, "Icône d'origine", _glyph.Length == 0, () => _glyph = "", bubble));
        }
        foreach (var (glyph, name) in BubbleGlyphs.All)
        {
            var text = new TextBlock { Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = 19, Foreground = glyph == _glyph ? Accent : Brushes.White };
            string g = glyph;
            _icons.Children.Add(Tile(text, name, glyph == _glyph, () => _glyph = g, bubble));
        }
    }

    private Border Tile(FrameworkElement content, string tooltip, bool selected, System.Action pick, BubbleConfig bubble)
    {
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        var tile = new Border
        {
            Width = 44, Height = 40, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10),
            Background = selected ? new SolidColorBrush(Color.FromRgb(0x23, 0x32, 0x49)) : TileBg,
            BorderBrush = selected ? Accent : Brushes.Transparent, BorderThickness = new Thickness(1.5),
            Cursor = Cursors.Hand, ToolTip = tooltip, Child = content,
        };
        // Au clic (appui) et pas au relâchement : sinon le déplacement de la fenêtre « mange » le clic.
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            pick();
            RenderIcons(bubble);
        };
        return tile;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), FontSize = 12.5, Margin = new Thickness(0, 12, 0, 6),
    };

    private static Button Button(string text, bool primary) => new()
    {
        Content = text, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.White,
        Background = primary ? Accent : FieldBg, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
    };
}
