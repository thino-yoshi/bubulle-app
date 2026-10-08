using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bulles;

/// <summary>Champ qui capture une combinaison de touches (ex. Alt+Espace), au style sombre de Bulles.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _value;

    /// <summary>Nouvelle combinaison choisie (vide = aucun raccourci).</summary>
    public event Action<Hotkey>? Changed;

    public HotkeyBox(Hotkey value)
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 150;
        Padding = new Thickness(10, 5, 10, 5);
        Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        Foreground = Brushes.White;
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        TextAlignment = TextAlignment.Center;
        Cursor = Cursors.Hand;
        ToolTip = "Clique puis appuie sur la combinaison. Retour arrière pour effacer.";
        Value = value;
        PreviewKeyDown += OnKey;
        GotKeyboardFocus += (_, _) =>
        {
            BorderBrush = new SolidColorBrush(LauncherWindow.Accent);
            Text = "Appuie sur les touches…";
        };
        LostKeyboardFocus += (_, _) =>
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            Value = _value;
        };
    }

    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            Text = value.IsEmpty ? "Aucun" : value.ToString();
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete or Key.Escape)
        {
            Value = default;
            Changed?.Invoke(_value);
            return;
        }
        // Sans modificateur, seules les touches F1-F24 ont du sens en raccourci global.
        if (mods == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24)) return;
        Value = new Hotkey(mods, key);
        Changed?.Invoke(_value);
    }
}
