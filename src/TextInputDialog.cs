using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Petite fenêtre « saisir un texte » (ex. renommer une app d'un lanceur), au style de Bubulle.</summary>
public sealed class TextInputDialog : Window
{
    private readonly TextBox _box;

    /// <summary>Texte validé, ou null si annulé.</summary>
    public string? Result { get; private set; }

    public TextInputDialog(string title, string label, string initial)
    {
        Title = "Bubulle";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _box = new TextBox
        {
            Text = initial, Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Foreground = Brushes.White,
            CaretBrush = Brushes.White, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7, 10, 7), FontSize = 14,
        };
        var cancel = new Button { Content = "Annuler", Style = Theme.Button };
        var ok = new Button { Content = "Enregistrer", Style = Theme.AccentButton, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        ok.Click += (_, _) => Submit();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)), FontSize = 12.5, Margin = new Thickness(0, 12, 0, 6) });
        stack.Children.Add(_box);
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
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) DragMove(); };
        Loaded += (_, _) => { Activate(); _box.Focus(); _box.SelectAll(); };
    }

    private void Submit()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0) { _box.Focus(); return; }
        Result = text;
        Close();
    }
}
