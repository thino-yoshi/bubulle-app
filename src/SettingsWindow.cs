using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Bulles;

/// <summary>Champ qui capture une combinaison de touches (ex. Alt+Espace).</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _value;

    public HotkeyBox(Hotkey value)
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 150;
        Padding = new Thickness(6, 3, 6, 3);
        ToolTip = "Clique puis appuie sur la combinaison. Retour arrière pour effacer.";
        Value = value;
        PreviewKeyDown += OnKey;
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
            return;
        }
        // Sans modificateur, seules les touches F1-F24 ont du sens en raccourci global.
        if (mods == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24)) return;
        Value = new Hotkey(mods, key);
    }
}

public sealed class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private readonly HotkeyBox _mainHotkey;
    private readonly CheckBox _perBubble, _autoHide, _startup, _preload;
    private readonly ComboBox _screen;
    private readonly Slider _opacity, _width, _height;
    private readonly List<(BubbleConfig bubble, HotkeyBox box, Slider opacity)> _rows = new();

    public event Action? Saved;

    private readonly CheckBox _soundOn;
    private readonly Slider _soundVolume;
    private string _openSound, _closeSound;

    public SettingsWindow(AppSettings settings)
    {
        _s = settings;
        Title = "Bulles — paramètres (v0.0.2)";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(Section("Général"));
        _mainHotkey = new HotkeyBox(Hotkey.Parse(_s.MainHotkey));
        root.Children.Add(Row("Raccourci de la bulle principale", _mainHotkey));
        _opacity = MakeSlider(30, 100, _s.DefaultOpacity);
        root.Children.Add(Row("Opacité par défaut des fenêtres", SliderWithValue(_opacity, "%")));
        _width = MakeSlider(20, 90, _s.DefaultWidthPct);
        root.Children.Add(Row("Largeur par défaut (% de l'écran)", SliderWithValue(_width, "%")));
        _height = MakeSlider(20, 95, _s.DefaultHeightPct);
        root.Children.Add(Row("Hauteur par défaut (% de l'écran)", SliderWithValue(_height, "%")));
        _autoHide = new CheckBox { Content = "Cacher la fenêtre quand je clique ailleurs (ex. retour au jeu)", IsChecked = _s.AutoHide, Margin = new Thickness(0, 8, 0, 0) };
        _preload = new CheckBox
        {
            Content = "Garder les sites web connectés en arrière-plan (pastilles de messages dès le démarrage)",
            IsChecked = _s.PreloadWeb, Margin = new Thickness(0, 8, 0, 0),
        };
        root.Children.Add(_autoHide);
        root.Children.Add(_preload);
        _screen = new ComboBox { Width = 260 };
        var screens = System.Windows.Forms.Screen.AllScreens;
        var currentScreen = Screens.Current(_s).DeviceName;
        for (int i = 0; i < screens.Length; i++)
        {
            _screen.Items.Add(new ComboBoxItem { Content = Screens.Label(screens[i], i), Tag = screens[i].DeviceName });
            if (screens[i].DeviceName == currentScreen) _screen.SelectedIndex = i;
        }
        root.Children.Add(Row("Écran des bulles", _screen));
        _startup = new CheckBox { Content = "Lancer Bulles au démarrage de Windows", IsChecked = _s.StartWithWindows, Margin = new Thickness(0, 8, 0, 0) };
        root.Children.Add(_startup);
        root.Children.Add(new TextBlock
        {
            Text = "Astuce : glisse la bulle principale pour la déplacer, ou vers l'autre moitié de l'écran pour changer de côté.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        });

        root.Children.Add(Section("Sons"));
        _openSound = _s.OpenSoundPath;
        _closeSound = _s.CloseSoundPath;
        _soundOn = new CheckBox { Content = "Jouer un son quand les bulles se déploient / se replient", IsChecked = _s.SoundEnabled };
        root.Children.Add(_soundOn);
        _soundVolume = MakeSlider(0, 100, _s.SoundVolume);
        root.Children.Add(Row("Volume des sons", SliderWithValue(_soundVolume, "%")));
        root.Children.Add(SoundRow("Son de déploiement", () => _openSound, p => _openSound = p, "open"));
        root.Children.Add(SoundRow("Son de repli", () => _closeSound, p => _closeSound = p, "close"));

        root.Children.Add(Section("Bulles"));
        _perBubble = new CheckBox { Content = "Raccourci clavier par bulle", IsChecked = _s.PerBubbleHotkeys, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(_perBubble);

        var list = new StackPanel();
        if (_s.Bubbles.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "Aucune bulle pour l'instant. Ajoute une app avec la bulle « + ».", Foreground = Brushes.Gray });
        }
        for (int i = 0; i < _s.Bubbles.Count; i++)
        {
            var b = _s.Bubbles[i];
            var box = new HotkeyBox(Hotkey.Parse(b.Hotkey));
            var op = MakeSlider(30, 100, b.Opacity > 0 ? b.Opacity : _s.DefaultOpacity);
            op.Width = 110;
            _rows.Add((b, box, op));

            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var icon = IconLoader.Load(b.IconPath, b.IconIndex, b.LaunchPath);
            if (icon != null)
            {
                var img = new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(img, Dock.Left);
                row.Children.Add(img);
            }
            var opHost = SliderWithValue(op, "%");
            opHost.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(opHost, Dock.Right);
            row.Children.Add(opHost);
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock { Text = $"Bulle {i + 1} · {b.Name}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            list.Children.Add(row);
        }
        root.Children.Add(list);

        void SyncEnabled()
        {
            foreach (var r in _rows) r.box.IsEnabled = _perBubble.IsChecked == true;
        }
        _perBubble.Checked += (_, _) => SyncEnabled();
        _perBubble.Unchecked += (_, _) => SyncEnabled();
        SyncEnabled();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = "Annuler", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Enregistrer", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        cancel.Click += (_, _) => Close();
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // Empêche Alt+Espace d'ouvrir le menu système pendant qu'on le capture dans un champ.
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(
            (IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Native.WM_SYSCOMMAND && (w.ToInt64() & 0xFFF0) == Native.SC_KEYMENU && Keyboard.FocusedElement is HotkeyBox)
                    handled = true;
                return IntPtr.Zero;
            });
    }

    private void Save()
    {
        _s.MainHotkey = _mainHotkey.Value.ToString();
        _s.DefaultOpacity = (int)_opacity.Value;
        _s.DefaultWidthPct = (int)_width.Value;
        _s.DefaultHeightPct = (int)_height.Value;
        _s.AutoHide = _autoHide.IsChecked == true;
        _s.PreloadWeb = _preload.IsChecked == true;
        _s.SoundEnabled = _soundOn.IsChecked == true;
        _s.SoundVolume = (int)_soundVolume.Value;
        _s.OpenSoundPath = _openSound;
        _s.CloseSoundPath = _closeSound;
        _s.StartWithWindows = _startup.IsChecked == true;
        if (_screen.SelectedItem is ComboBoxItem { Tag: string device }) _s.Screen = device;
        _s.PerBubbleHotkeys = _perBubble.IsChecked == true;
        foreach (var (bubble, box, opacity) in _rows)
        {
            bubble.Hotkey = box.Value.ToString();
            int op = (int)opacity.Value;
            bubble.Opacity = op == _s.DefaultOpacity ? 0 : op;
        }
        _s.Save();
        Saved?.Invoke();
        Close();
    }

    /// <summary>Ligne « son » : nom du fichier, Choisir… (copié dans Bulles), Tester, Retirer.</summary>
    private DockPanel SoundRow(string label, Func<string> get, Action<string> set, string importName)
    {
        var file = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 };
        void Refresh() => file.Text = string.IsNullOrEmpty(get()) ? "Aucun" : System.IO.Path.GetFileName(get());
        Refresh();

        var choose = new Button { Content = "Choisir…", Padding = new Thickness(8, 2, 8, 2) };
        var test = new Button { Content = "▶", ToolTip = "Écouter", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
        var clear = new Button { Content = "✕", ToolTip = "Pas de son", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = label,
                Filter = "Sons (*.wav, *.mp3, *.m4a, *.wma, *.aac)|*.wav;*.mp3;*.m4a;*.wma;*.aac",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
            };
            if (dialog.ShowDialog(this) != true) return;
            try { set(Sounds.Import(dialog.FileName, importName)); }
            catch (Exception ex) { App.Log(ex); MessageBox.Show(this, "Impossible de copier ce son.", "Bulles"); }
            Refresh();
        };
        test.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(get()) || !System.IO.File.Exists(get())) return;
            var preview = new MediaPlayer { Volume = _soundVolume.Value / 100.0 };
            preview.Open(new Uri(get()));
            preview.Play();
        };
        clear.Click += (_, _) => { set(""); Refresh(); };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(file);
        buttons.Children.Add(choose);
        buttons.Children.Add(test);
        buttons.Children.Add(clear);
        return Row(label, buttons);
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 14, 0, 8),
    };

    private static DockPanel Row(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Slider MakeSlider(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        Width = 160,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static StackPanel SliderWithValue(Slider slider, string unit)
    {
        var label = new TextBlock { Width = 42, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        void Update() => label.Text = $"{(int)slider.Value} {unit}";
        slider.ValueChanged += (_, _) => Update();
        Update();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }
}
