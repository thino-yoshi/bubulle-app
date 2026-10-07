using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace Bulles;

public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    public uint NativeModifiers
    {
        get
        {
            uint m = Native.MOD_NOREPEAT;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) m |= Native.MOD_ALT;
            if (Modifiers.HasFlag(ModifierKeys.Control)) m |= Native.MOD_CONTROL;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) m |= Native.MOD_SHIFT;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) m |= Native.MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => (k - Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (k - Key.NumPad0),
        _ => k.ToString(),
    };

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        var mods = ModifierKeys.None;
        var key = Key.None;
        foreach (var raw in text.Split('+'))
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (p.Length == 1 && char.IsDigit(p[0])) key = Key.D0 + (p[0] - '0');
                    else if (p.Length == 4 && p.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(p[3])) key = Key.NumPad0 + (p[3] - '0');
                    else if (!Enum.TryParse(p, true, out key)) key = Key.None;
                    break;
            }
        }
        return new Hotkey(mods, key);
    }
}

/// <summary>Raccourcis clavier globaux (fonctionnent même quand le jeu a le focus).</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)!.AddHook(Hook);
    }

    public bool Register(Hotkey hotkey, Action action)
    {
        if (hotkey.IsEmpty) return true;
        int id = _nextId++;
        if (!Native.RegisterHotKey(_hwnd, id, hotkey.NativeModifiers, hotkey.VirtualKey)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose() => UnregisterAll();
}
