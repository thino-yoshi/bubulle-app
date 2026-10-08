using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Bulles;

/// <summary>
/// Petite pastille toujours cliquable, accrochée au coin d'une bulle traversable : la bulle laisse passer
/// la souris (bouton compris), donc cette pastille est une fenêtre à part qui, elle, reçoit les clics.
/// </summary>
public sealed class ClickThroughHandle : Window
{
    private const double Size = 30;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private readonly Window _frame;

    public ClickThroughHandle(Window frame, Action onClick)
    {
        _frame = frame;
        Owner = frame;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Size;
        Height = Size;
        Cursor = Cursors.Hand;
        ToolTip = "Reprendre la main (la bulle redevient cliquable)";

        var glyph = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var circle = new Border
        {
            CornerRadius = new CornerRadius(Size / 2),
            Background = new SolidColorBrush(Color.FromArgb(0xEE, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5),
            Child = glyph,
        };
        Content = circle;
        MouseEnter += (_, _) => circle.Background = new SolidColorBrush(LauncherWindow.Accent);
        MouseLeave += (_, _) => circle.Background = new SolidColorBrush(Color.FromArgb(0xEE, LauncherWindow.Accent.R, LauncherWindow.Accent.G, LauncherWindow.Accent.B));
        MouseLeftButtonUp += (_, _) => onClick();

        // Ne prend jamais le focus (le jeu garde le clavier), et absente d'Alt+Tab.
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetExStyle(h, Native.GetExStyle(h) | Native.WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };

        frame.LocationChanged += Follow;
        frame.SizeChanged += Follow;
        Closed += (_, _) =>
        {
            frame.LocationChanged -= Follow;
            frame.SizeChanged -= Follow;
        };
        Follow(null, EventArgs.Empty);
    }

    /// <summary>Se place dans le coin haut-droit de la bulle.</summary>
    private void Follow(object? sender, EventArgs e)
    {
        Left = _frame.Left + _frame.ActualWidth - Size - 6;
        Top = _frame.Top - Size / 2 + 2;
    }
}
