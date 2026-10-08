using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bulles;

/// <summary>
/// Paramètres de Bulles, affichés dans une bulle-fenêtre. Chaque réglage s'applique tout de suite
/// (pas de bouton « Enregistrer »).
/// </summary>
public sealed class SettingsView : UserControl
{
    private static readonly Brush CardBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)));
    private static readonly Brush CardBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Divider = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextMain = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(LauncherWindow.Accent));

    private readonly AppController _c;
    private readonly AppSettings _s;
    private readonly StackPanel _root = new() { Margin = new Thickness(16, 14, 16, 16) };

    public SettingsView(AppController controller)
    {
        _c = controller;
        _s = controller.Settings;
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Build();

        // Pendant la saisie d'un raccourci, Alt+Espace ne doit pas ouvrir le menu système de la fenêtre.
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w && PresentationSource.FromVisual(w) is HwndSource src) src.AddHook(SuppressAltSpace);
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) Build(); };
    }

    private static IntPtr SuppressAltSpace(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == Native.WM_SYSCOMMAND && (w.ToInt64() & 0xFFF0) == Native.SC_KEYMENU && Keyboard.FocusedElement is HotkeyBox) handled = true;
        return IntPtr.Zero;
    }

    /// <summary>(Re)construit tout le contenu à partir des paramètres actuels.</summary>
    private void Build()
    {
        _root.Children.Clear();
        var title = new DockPanel { Margin = new Thickness(2, 0, 2, 14) };
        var version = new TextBlock { Text = "v" + typeof(App).Assembly.GetName().Version?.ToString(3), Foreground = TextSoft, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(version, Dock.Right);
        title.Children.Add(version);
        title.Children.Add(new TextBlock { Text = "Paramètres", Foreground = TextMain, FontSize = 19, FontWeight = FontWeights.SemiBold });
        _root.Children.Add(title);

        BuildGeneral();
        BuildWindows();
        BuildSounds();
        BuildNotifications();
        BuildCursor();
        BuildHotkeys();
        BuildAbout();
    }

    // ---------- Sections ----------

    private void BuildGeneral()
    {
        var card = Card("Général", "");

        var hotkey = new HotkeyBox(Hotkey.Parse(_s.MainHotkey));
        hotkey.GotKeyboardFocus += (_, _) => _c.SuspendHotkeys();
        hotkey.LostKeyboardFocus += (_, _) => _c.ResumeHotkeys();
        hotkey.Changed += k => { _s.MainHotkey = k.ToString(); _s.Save(); };
        Add(card, Row("Raccourci de la bulle principale", "Ouvre et ferme la cascade, même en jeu.", hotkey));

        var screens = new WrapPanel();
        var all = System.Windows.Forms.Screen.AllScreens;
        var current = Screens.Current(_s).DeviceName;
        for (int i = 0; i < all.Length; i++)
        {
            var device = all[i].DeviceName;
            var pill = Pill($"Écran {i + 1}{(all[i].Primary ? " · principal" : "")}", device == current);
            pill.Click += (_, _) => { _c.MoveToScreen(device); Build(); };
            screens.Children.Add(pill);
        }
        Add(card, Row("Écran des bulles", "Tu peux aussi glisser la bulle principale vers un autre écran.", screens));

        Add(card, Toggle("Lancer Bubulle au démarrage de Windows", null, _s.StartWithWindows, v => { _s.StartWithWindows = v; _s.Save(); _c.ApplyStartupSetting(); }));
        Add(card, Toggle("Cacher la fenêtre quand je clique ailleurs", "Par exemple en revenant au jeu (sauf si elle est épinglée).", _s.AutoHide, v => { _s.AutoHide = v; _s.Save(); }));
        Add(card, Toggle("Garder les sites web connectés en arrière-plan", "Pastilles de messages dès le démarrage. Prend effet au prochain lancement.", _s.PreloadWeb, v => { _s.PreloadWeb = v; _s.Save(); }));
        _root.Children.Add(card.Border);
    }

    private void BuildWindows()
    {
        var card = Card("Bulles-fenêtres", "");
        Add(card, SliderRow("Opacité par défaut", 30, 100, _s.DefaultOpacity, "%", v => { _s.DefaultOpacity = v; _s.Save(); }));
        Add(card, SliderRow("Largeur par défaut (% de l'écran)", 20, 90, _s.DefaultWidthPct, "%", v => { _s.DefaultWidthPct = v; _s.Save(); }));
        Add(card, SliderRow("Hauteur par défaut (% de l'écran)", 20, 95, _s.DefaultHeightPct, "%", v => { _s.DefaultHeightPct = v; _s.Save(); }));
        Add(card, Hint("Chaque bulle garde sa propre taille et son opacité une fois réglées dans sa barre."));
        _root.Children.Add(card.Border);
    }

    private void BuildSounds()
    {
        var card = Card("Sons", "");
        Add(card, Toggle("Sons quand les bulles se déploient ou se replient", null, _s.SoundEnabled, v => { _s.SoundEnabled = v; _s.Save(); }));
        Add(card, SliderRow("Volume des sons", 0, 100, _s.SoundVolume, "%", v => { _s.SoundVolume = v; _s.Save(); }));
        Add(card, SoundRow("Son de déploiement", () => _s.OpenSoundPath, p => _s.OpenSoundPath = p, "open"));
        Add(card, SoundRow("Son de repli", () => _s.CloseSoundPath, p => _s.CloseSoundPath = p, "close"));
        _root.Children.Add(card.Border);
    }

    private void BuildNotifications()
    {
        var card = Card("Notifications", "");
        Add(card, Toggle("Animer la bulle principale quand un message arrive", "Le GIF tourne en boucle tant que la cascade n'a pas été déployée pour voir le message.", _s.NotifyAnimation,
            v => { _s.NotifyAnimation = v; _s.Save(); _c.ReloadNotificationGif(); }));

        var file = new TextBlock { Foreground = TextSoft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130, Margin = new Thickness(0, 0, 10, 0) };
        void Refresh() => file.Text = string.IsNullOrEmpty(_s.NotificationGifPath) ? "GIF de base" : Path.GetFileName(_s.NotificationGifPath);
        Refresh();
        var choose = new Button { Content = "Choisir…", Style = Theme.Button };
        var reset = new Button { Content = "Par défaut", Style = Theme.Button, Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Choisir un GIF",
                Filter = "Images animées (*.gif)|*.gif",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                // Copié dans les données de Bubulle : il reste même si l'original est déplacé.
                Directory.CreateDirectory(AppSettings.Dir);
                var target = Path.Combine(AppSettings.Dir, "notification.gif");
                File.Copy(dialog.FileName, target, true);
                _s.NotificationGifPath = target;
            }
            catch (Exception ex) { App.Log(ex); }
            _s.Save();
            _c.ReloadNotificationGif();
            Refresh();
        };
        reset.Click += (_, _) => { _s.NotificationGifPath = ""; _s.Save(); _c.ReloadNotificationGif(); Refresh(); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(file);
        controls.Children.Add(choose);
        controls.Children.Add(reset);
        Add(card, Row("Animation", null, controls));
        _root.Children.Add(card.Border);
    }

    private void BuildCursor()
    {
        var card = Card("Curseur", "");
        Add(card, Toggle("Utiliser mon curseur au-dessus de Bubulle", "Ailleurs, Windows garde son curseur habituel.", _s.UseCustomCursor,
            v => { _s.UseCustomCursor = v; _s.Save(); AppCursor.Apply(_s); }));

        var preview = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.NearestNeighbor);
        var status = new TextBlock { Foreground = TextSoft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        void Refresh()
        {
            preview.Source = AppCursor.Preview(_s.CursorPath);
            status.Text = File.Exists(_s.CursorPath) ? Path.GetFileName(_s.CursorPath) : "Aucun curseur choisi";
        }
        Refresh();

        var capture = new Button { Content = "Récupérer le curseur du jeu", Style = Theme.AccentButton };
        var choose = new Button { Content = "Choisir un fichier…", Style = Theme.Button, Margin = new Thickness(8, 0, 0, 0) };
        capture.Click += async (_, _) =>
        {
            capture.IsEnabled = false;
            for (int s = 5; s > 0; s--)
            {
                status.Text = $"Passe sur ton jeu (clique une fois dedans)… {s}";
                await System.Threading.Tasks.Task.Delay(1000);
            }
            status.Text = "Bouge doucement la souris sur différentes zones du jeu pendant 6 secondes…";
            _c.Announce("Bouge doucement la souris sur différentes zones du jeu pendant 6 secondes.");
            var (path, message) = await GameCursorFinder.RecoverAsync();
            capture.IsEnabled = true;
            _c.Announce(message);
            if (path == null)
            {
                status.Text = message;
                return;
            }
            _s.CursorPath = path;
            _s.UseCustomCursor = true;
            AskCursorScope();
            _s.Save();
            AppCursor.Apply(_s);
            Build();
        };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choisir un curseur", Filter = "Curseurs (*.cur, *.ani)|*.cur;*.ani" };
            if (dialog.ShowDialog() != true) return;
            _s.CursorPath = dialog.FileName;
            _s.UseCustomCursor = true;
            AskCursorScope();
            _s.Save();
            AppCursor.Apply(_s);
            Build();
        };
        var line = new DockPanel();
        DockPanel.SetDock(preview, Dock.Left);
        line.Children.Add(preview);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(capture);
        buttons.Children.Add(choose);
        var texts = new StackPanel();
        texts.Children.Add(status);
        texts.Children.Add(buttons);
        line.Children.Add(texts);
        Add(card, Padded(line));

        Add(card, SliderRow("Taille du curseur", 50, 300, _s.CursorScale, "%", v =>
        {
            _s.CursorScale = v;
            _s.Save();
            AppCursor.Apply(_s);
        }, step: 10));

        // Où utiliser le curseur : Bubulle seulement (et ses apps), ou tout le PC.
        var scope = new WrapPanel();
        foreach (var (label, value) in new[] { ("Seulement Bubulle", "Bubulle"), ("Partout sur le PC", "System") })
        {
            var pill = Pill(label, _s.CursorScope == value);
            pill.Click += (_, _) =>
            {
                _s.CursorScope = value;
                _s.Save();
                AppCursor.Apply(_s);
                Build();
            };
            scope.Children.Add(pill);
        }
        Add(card, Row("Où utiliser mon curseur", "« Seulement Bubulle » inclut les apps et sites ouverts dans les bulles. « Partout » remplace la flèche de Windows (ta flèche d'origine revient si tu changes d'avis).", scope));
        _root.Children.Add(card.Border);
    }

    /// <summary>Après avoir récupéré ou choisi un curseur : on demande où l'utiliser.</summary>
    private void AskCursorScope()
    {
        var dialog = new ChoiceDialog(
            "Où utiliser ce curseur ?",
            "Tu pourras changer d'avis à tout moment dans Paramètres → Curseur.",
            "Seulement dans Bubulle", "Partout sur le PC",
            "Bubulle, ses bulles, et les apps et sites ouverts dedans.",
            "Remplace la flèche de Windows partout. Ta flèche d'origine revient si tu changes d'avis.");
        dialog.ShowDialog();
        if (dialog.Result == 1) _s.CursorScope = "Bubulle";
        else if (dialog.Result == 2) _s.CursorScope = "System";
    }

    private void BuildHotkeys()
    {
        var card = Card("Raccourcis des bulles", "");
        var list = new StackPanel();
        void SyncList()
        {
            list.Children.Clear();
            if (!_s.PerBubbleHotkeys) return;
            for (int i = 0; i < _s.Bubbles.Count; i++)
            {
                var b = _s.Bubbles[i];
                var box = new HotkeyBox(Hotkey.Parse(b.Hotkey)) { Width = 130 };
                box.GotKeyboardFocus += (_, _) => _c.SuspendHotkeys();
                box.LostKeyboardFocus += (_, _) => _c.ResumeHotkeys();
                box.Changed += k => { b.Hotkey = k.ToString(); _s.Save(); };
                var row = new DockPanel { Margin = new Thickness(14, 6, 14, 6) };
                DockPanel.SetDock(box, Dock.Right);
                row.Children.Add(box);
                if (BubbleIcon(b) is { } icon)
                {
                    icon.Margin = new Thickness(0, 0, 10, 0);
                    DockPanel.SetDock(icon, Dock.Left);
                    row.Children.Add(icon);
                }
                row.Children.Add(new TextBlock { Text = b.Name, Foreground = TextMain, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
        }
        Add(card, Toggle("Un raccourci clavier par bulle", "Ouvre directement la bulle (Alt+1, Alt+2… par défaut).", _s.PerBubbleHotkeys, v =>
        {
            _s.PerBubbleHotkeys = v;
            _s.Save();
            _c.ResumeHotkeys();
            SyncList();
        }));
        SyncList();
        card.Body.Children.Add(list);
        _root.Children.Add(card.Border);
    }

    private void BuildAbout()
    {
        var card = Card("Bubulle", "");
        var folder = new Button { Content = "Ouvrir le dossier des données", Style = Theme.Button };
        folder.Click += (_, _) => { try { Process.Start("explorer.exe", $"\"{AppSettings.Dir}\""); } catch (Exception ex) { App.Log(ex); } };
        var quit = new Button { Content = "Quitter Bubulle", Style = Theme.Button, Margin = new Thickness(8, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) };
        quit.Click += (_, _) => _c.Quit();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(folder);
        buttons.Children.Add(quit);
        Add(card, Padded(buttons));

        // Mises à jour : vérifiées au démarrage ; bouton pour vérifier tout de suite ou installer.
        var status = new TextBlock { Text = _c.UpdateStatus.Length > 0 ? _c.UpdateStatus : $"Version installée : v{Updater.Current}", Foreground = TextSoft, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var update = new Button
        {
            Content = Updater.Ready != null ? $"Redémarrer pour installer v{Updater.Ready.Value.Version}" : "Rechercher une mise à jour",
            Style = Updater.Ready != null ? Theme.AccentButton : Theme.Button,
        };
        update.Click += async (_, _) =>
        {
            if (Updater.Ready != null) { _c.InstallUpdateNow(); return; }
            update.IsEnabled = false;
            status.Text = "Recherche d'une mise à jour…";
            await _c.CheckForUpdate(TimeSpan.Zero, quiet: true);
            Build();
        };
        Add(card, Row("Mises à jour", "Vérifiées au démarrage sur GitHub ; tes réglages sont toujours gardés.", update));
        Add(card, Padded(status));
        Add(card, Hint("Astuces : glisse la bulle principale pour la déplacer · appui long sur une bulle pour la ranger · clic droit pour la personnaliser."));
        _root.Children.Add(card.Border);
    }

    // ---------- Briques d'interface ----------

    private sealed record CardParts(Border Border, StackPanel Body);

    private static CardParts Card(string title, string glyph)
    {
        var body = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 12, 14, 6) };
        header.Children.Add(new TextBlock { Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = 15, Foreground = AccentBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        header.Children.Add(new TextBlock { Text = title, Foreground = TextMain, FontSize = 14.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);
        var border = new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(0, 0, 0, 6), Child = stack,
        };
        return new CardParts(border, body);
    }

    private static void Add(CardParts card, FrameworkElement row)
    {
        if (card.Body.Children.Count > 0)
            card.Body.Children.Add(new Border { Height = 1, Background = Divider, Margin = new Thickness(14, 0, 14, 0) });
        card.Body.Children.Add(row);
    }

    /// <summary>Ligne : titre et description à gauche, réglage à droite.</summary>
    private static DockPanel Row(string label, string? description, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = label, Foreground = TextMain, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        if (description != null)
            texts.Children.Add(new TextBlock { Text = description, Foreground = TextSoft, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(texts);
        return row;
    }

    private static DockPanel Toggle(string label, string? description, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox { Style = Theme.Toggle, IsChecked = value };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return Row(label, description, toggle);
    }

    private static FrameworkElement SliderRow(string label, int min, int max, int value, string unit, Action<int> changed, int step = 1)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Style = Theme.Slider,
            IsSnapToTickEnabled = true, TickFrequency = step,
        };
        var text = new TextBlock { Foreground = TextSoft, FontSize = 12.5, Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        void Show() => text.Text = $"{(int)slider.Value} {unit}";
        Show();
        // Applique quand tu lâches le curseur (ou à la molette), pas à chaque pixel du glissé.
        slider.ValueChanged += (_, _) => Show();
        slider.PreviewMouseLeftButtonUp += (_, _) => changed((int)slider.Value);
        slider.MouseWheel += (_, e) =>
        {
            slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? step : -step), min, max);
            changed((int)slider.Value);
        };

        var row = new DockPanel { Margin = new Thickness(14, 8, 14, 8) };
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(text);
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = label, Foreground = TextMain, FontSize = 13 });
        top.Children.Add(slider);
        row.Children.Add(top);
        return row;
    }

    private FrameworkElement SoundRow(string label, Func<string> get, Action<string> set, string importName)
    {
        var file = new TextBlock { Foreground = TextSoft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130, Margin = new Thickness(0, 0, 10, 0) };
        void Refresh() => file.Text = string.IsNullOrEmpty(get()) ? "Aucun"
            : string.Equals(get(), Sounds.BundledOpen, StringComparison.OrdinalIgnoreCase) ? "Son de base" : Path.GetFileName(get());
        Refresh();

        var choose = new Button { Content = "Choisir…", Style = Theme.Button };
        var test = new Button { Content = new TextBlock { Text = "", FontFamily = BubbleGlyphs.Font }, Style = Theme.Button, ToolTip = "Écouter", Margin = new Thickness(6, 0, 0, 0) };
        var clear = new Button { Content = new TextBlock { Text = "", FontFamily = BubbleGlyphs.Font }, Style = Theme.Button, ToolTip = "Pas de son", Margin = new Thickness(6, 0, 0, 0) };
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = label,
                Filter = "Sons (*.wav, *.mp3, *.m4a, *.wma, *.aac)|*.wav;*.mp3;*.m4a;*.wma;*.aac",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            if (dialog.ShowDialog() != true) return;
            try { set(Sounds.Import(dialog.FileName, importName)); }
            catch (Exception ex) { App.Log(ex); }
            _s.Save();
            _c.Sounds.Reload();
            Refresh();
        };
        test.Click += (_, _) =>
        {
            if (!File.Exists(get())) return;
            var preview = new MediaPlayer { Volume = _s.SoundVolume / 100.0 };
            preview.Open(new Uri(get()));
            preview.Play();
        };
        clear.Click += (_, _) => { set(""); _s.Save(); _c.Sounds.Reload(); Refresh(); };

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(file);
        controls.Children.Add(choose);
        controls.Children.Add(test);
        controls.Children.Add(clear);
        return Row(label, null, controls);
    }

    private static Button Pill(string text, bool selected) => new()
    {
        Content = text,
        Style = selected ? Theme.AccentButton : Theme.Button,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, Foreground = TextSoft, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 8, 14, 8),
    };

    private static Border Padded(FrameworkElement content) => new() { Padding = new Thickness(14, 10, 14, 10), Child = content };

    private static FrameworkElement? BubbleIcon(BubbleConfig b)
    {
        var image = BubbleFrame.IconFor(b);
        if (image != null) return new Image { Source = image, Width = 20, Height = 20 };
        var glyph = BubbleFrame.GlyphFor(b);
        return glyph.Length == 0 ? null : new TextBlock { Text = glyph, FontFamily = BubbleGlyphs.Font, FontSize = 17, Foreground = AccentBrush, VerticalAlignment = VerticalAlignment.Center };
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
