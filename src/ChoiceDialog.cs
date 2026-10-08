using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Petite question à deux réponses, au style de Bubulle. Result : 1, 2, ou 0 si fermée.</summary>
public sealed class ChoiceDialog : Window
{
    public int Result { get; private set; }

    public ChoiceDialog(string title, string message, string first, string second, string? firstHint = null, string? secondHint = null)
    {
        Title = "Bubulle";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock
        {
            Text = message, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
        });
        stack.Children.Add(Option(first, firstHint, 1, primary: true));
        stack.Children.Add(Option(second, secondHint, 2, primary: false));

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x16, 0x1A, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B)),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(18),
            Child = stack,
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) DragMove(); };
        Loaded += (_, _) => Activate();
    }

    private Button Option(string text, string? hint, int value, bool primary)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
        if (hint != null)
            content.Children.Add(new TextBlock { Text = hint, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
        var button = new Button
        {
            Content = content, Style = primary ? Theme.AccentButton : Theme.Button,
            HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 14, 10),
        };
        button.Click += (_, _) => { Result = value; Close(); };
        return button;
    }
}
