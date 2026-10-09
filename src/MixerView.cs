using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Bulles;

/// <summary>
/// Mélangeur audio de Bubulle, façon fenêtre du menu de SAO (saison 1) : panneau clair, titre centré
/// souligné, croix rose pour fermer, jauges vertes comme la barre de vie et vumètre rouge comme celle des boss.
/// Volume général de la sortie, puis une ligne par app qui joue du son (sessions regroupées par programme).
/// </summary>
public sealed class MixerView : UserControl
{
    public const double ViewWidth = 400, ViewHeight = 600;

    private static readonly Brush Orange = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0xA8, 0x18)));
    private static readonly Brush OrangeSoft = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xA6)));
    private static readonly Brush PanelBg = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)));
    private static readonly Brush TitleBg = Brushes.White;
    private static readonly Brush SectionBg = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)));
    private static readonly Brush RowLine = Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));
    private static readonly Brush TextDark = Frozen(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
    private static readonly Brush TextSoft = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
    private static readonly Brush DotDark = Frozen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0x23, 0x6A)));
    private static readonly Brush MeterTrack = Frozen(new SolidColorBrush(Color.FromRgb(0xDA, 0xDA, 0xDA)));
    // Retour du son comme la barre de vie d'un boss de SAO : rouge.
    private static readonly Brush MeterFill = Frozen(new LinearGradientBrush(
        Color.FromRgb(0xC8, 0x1E, 0x1E), Color.FromRgb(0xFF, 0x5A, 0x3C), 0));
    // Fond « presque invisible » : la fenêtre reste transparente au-dessus du jeu, mais garde les clics
    // (un fond 100 % transparent laisserait passer le clic jusqu'au jeu).
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0x00, 0x00, 0x00)));
    private static readonly Brush HoverDark = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)));
    // Ombre sous les textes posés directement sur le jeu, pour qu'ils restent lisibles sur fond clair.
    private static readonly System.Windows.Media.Effects.DropShadowEffect TextShadow = Frozen(new System.Windows.Media.Effects.DropShadowEffect
    {
        Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.9,
    });
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ListBox _devices;
    private Popup? _devicePopup;
    private TextBlock? _deviceLabel;
    private readonly StackPanel _appsPanel;
    private readonly TextBlock _empty;
    private readonly ChannelRow _master;
    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _meters, _refresh;
    private readonly Border _panel;
    private MMDevice? _device;
    private bool _syncingDevices;

    /// <param name="close">Ferme la bulle (croix rose du titre).</param>
    public MixerView(Action? close = null)
    {
        _devices = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = TextDark };
        _devices.SelectionChanged += (_, _) =>
        {
            if (_syncingDevices) return;
            _devicePopup!.IsOpen = false;
            SelectDevice(_devices.SelectedItem as ListBoxItem);
        };

        _master = new ChannelRow("Volume général", null, big: true);
        _master.VolumeChanged += v => { if (_device != null) _device.AudioEndpointVolume.MasterVolumeLevelScalar = v; };
        _master.MuteToggled += () =>
        {
            if (_device == null) return;
            var ep = _device.AudioEndpointVolume;
            ep.Mute = !ep.Mute;
            _master.SetFromSystem(ep.MasterVolumeLevelScalar, ep.Mute);
        };

        _appsPanel = new StackPanel();
        _empty = new TextBlock
        {
            Text = "Aucune app ne joue de son pour l'instant.",
            Foreground = Brushes.White, Effect = TextShadow, FontSize = 12.5, Margin = new Thickness(6, 8, 6, 14),
        };

        var dock = new DockPanel { LastChildFill = true };
        void Top(UIElement el) { DockPanel.SetDock(el, Dock.Top); dock.Children.Add(el); }
        Top(BuildTitle(close));
        Top(Section("Sortie"));
        Top(BuildDeviceButton());
        Top(RowBox(_master));
        Top(Section("Applications"));
        var list = new StackPanel();
        list.Children.Add(_appsPanel);
        list.Children.Add(_empty);
        dock.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        _panel = new Border
        {
            // Seule la barre du titre est blanche : le reste est transparent, il ne reste que les logos,
            // les barres de vie et les stats au-dessus du jeu.
            Background = HitBrush, Child = dock, ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        Content = _panel;
        Background = Brushes.Transparent;

        _meters = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meters.Tick += (_, _) => UpdateMeters();
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _refresh.Tick += (_, _) => RefreshSessions();

        // Ne travaille que quand la bulle est visible.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { LoadDevices(); RefreshSessions(); _meters.Start(); _refresh.Start(); Unfold(); }
            else { _meters.Stop(); _refresh.Stop(); }
        };
    }

    /// <summary>Ouverture comme dans l'anime : la fenêtre se déplie depuis sa ligne centrale.</summary>
    private void Unfold()
    {
        _panel.RenderTransform = new ScaleTransform(1, 0.04);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _panel.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.04, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(200)));
    }

    // ---------- Titre et sections ----------

    private static FrameworkElement BuildTitle(Action? close)
    {
        var grid = new Grid { Height = 44, Background = TitleBg };
        var icon = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Orange,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            Child = new TextBlock { Text = "", FontFamily = Icons, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        grid.Children.Add(icon);
        grid.Children.Add(new TextBlock
        {
            Text = "Mélangeur audio", Foreground = TextDark, FontSize = 15, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (close != null)
        {
            var x = CloseButton(close);
            x.HorizontalAlignment = HorizontalAlignment.Right;
            x.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(x);
        }
        // Le titre est souligné d'un trait gris, avec un petit éclat orange au centre, comme dans l'anime.
        var underline = new Grid { Height = 2, VerticalAlignment = VerticalAlignment.Bottom };
        underline.Children.Add(new Border { Background = RowLine });
        underline.Children.Add(new Border { Background = Orange, Width = 90, HorizontalAlignment = HorizontalAlignment.Center });
        grid.Children.Add(underline);
        return grid;
    }

    private static FrameworkElement CloseButton(Action click)
    {
        var g = new Grid { Width = 24, Height = 24, Cursor = Cursors.Hand, ToolTip = "Fermer", VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(new Ellipse { Fill = MutedBrush });
        g.Children.Add(new Ellipse { Stroke = Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(2) });
        foreach (var flip in new[] { false, true })
            g.Children.Add(new Line
            {
                X1 = 8.5, Y1 = flip ? 15.5 : 8.5, X2 = 15.5, Y2 = flip ? 8.5 : 15.5,
                Stroke = Brushes.White, StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        g.RenderTransformOrigin = new Point(0.5, 0.5);
        g.MouseEnter += (_, _) => g.RenderTransform = new ScaleTransform(1.12, 1.12);
        g.MouseLeave += (_, _) => g.RenderTransform = null;
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return g;
    }

    private static Border Section(string title) => new()
    {
        Padding = new Thickness(6, 10, 6, 4),
        Child = new TextBlock { Text = title, Foreground = Brushes.White, Effect = TextShadow, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
    };

    private static Border RowBox(UIElement child) => new()
    {
        Padding = new Thickness(4, 6, 4, 8), Child = child,
    };

    // ---------- Sorties audio ----------

    /// <summary>Sélecteur de sortie : une ligne avec le nom de la sortie, qui déroule la liste.</summary>
    private FrameworkElement BuildDeviceButton()
    {
        _deviceLabel = new TextBlock { Foreground = Brushes.White, Effect = TextShadow, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock
        {
            Text = "", FontFamily = Icons, FontSize = 11,
            Foreground = Brushes.White, Effect = TextShadow, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        var content = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        content.Children.Add(_deviceLabel);

        var button = new Border
        {
            Background = HitBrush, Padding = new Thickness(6, 6, 6, 8), Cursor = Cursors.Hand, Child = content, CornerRadius = new CornerRadius(3),
        };
        button.MouseEnter += (_, _) => button.Background = HoverDark;
        button.MouseLeave += (_, _) => button.Background = HitBrush;
        _devicePopup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 2,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = Brushes.White, BorderBrush = RowLine,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(2),
                Child = _devices,
            },
        };
        button.MouseLeftButtonUp += (_, _) =>
        {
            ((Border)_devicePopup.Child).MinWidth = button.ActualWidth;
            _devicePopup.IsOpen = !_devicePopup.IsOpen;
        };
        return button;
    }

    private void LoadDevices()
    {
        _syncingDevices = true;
        try
        {
            string? selectedId = _device?.ID;
            _devices.Items.Clear();
            string defaultId = "";
            try { defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; } catch { /* Aucune sortie. */ }
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var label = d.FriendlyName + (d.ID == defaultId ? "  · par défaut" : "");
                var item = new ListBoxItem { Content = label, Tag = d.ID, Foreground = TextDark, Padding = new Thickness(10, 7, 10, 7) };
                _devices.Items.Add(item);
                if (d.ID == (selectedId ?? defaultId)) _devices.SelectedItem = item;
                d.Dispose();
            }
            if (_devices.SelectedItem == null && _devices.Items.Count > 0) _devices.SelectedIndex = 0;
        }
        finally
        {
            _syncingDevices = false;
        }
        SelectDevice(_devices.SelectedItem as ListBoxItem);
    }

    private void SelectDevice(ListBoxItem? item)
    {
        if (_deviceLabel != null) _deviceLabel.Text = item?.Content as string ?? "Aucune sortie audio";
        if (item?.Tag is not string id || _device?.ID == id) return;
        _device?.Dispose();
        try { _device = _enumerator.GetDevice(id); }
        catch (Exception ex) { App.Log(ex); _device = null; }
        foreach (var row in _rows.Values) row.Dispose();
        _rows.Clear();
        _appsPanel.Children.Clear();
        RefreshSessions();
    }

    // ---------- Apps ----------

    private void RefreshSessions()
    {
        if (_device == null) return;
        try
        {
            var ep = _device.AudioEndpointVolume;
            _master.SetFromSystem(ep.MasterVolumeLevelScalar, ep.Mute);

            _device.AudioSessionManager.RefreshSessions();
            var sessions = _device.AudioSessionManager.Sessions;
            var groups = new Dictionary<string, (string Name, string? Path, List<AudioSessionControl> List)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) { s.Dispose(); continue; }
                string key, name;
                string? path = null;
                if (s.IsSystemSoundsSession)
                {
                    key = "\u0000system";
                    name = "Sons système";
                }
                else
                {
                    path = Native.ProcessPathFromPid(s.GetProcessID);
                    if (path == null) { s.Dispose(); continue; }
                    key = System.IO.Path.GetFileNameWithoutExtension(path);
                    name = FriendlyName(key, path);
                }
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = (name, path, new List<AudioSessionControl>());
                g.List.Add(s);
            }

            foreach (var gone in _rows.Keys.Where(k => !groups.ContainsKey(k)).ToList())
            {
                _appsPanel.Children.Remove(_rows[gone].Card);
                _rows[gone].Dispose();
                _rows.Remove(gone);
            }
            foreach (var (key, g) in groups.OrderBy(kv => kv.Key.StartsWith('\u0000') ? 1 : 0).ThenBy(kv => kv.Value.Name))
            {
                if (!_rows.TryGetValue(key, out var row))
                {
                    var icon = g.Path != null ? IconLoader.Load(g.Path, 0, g.Path) : null;
                    row = new AppRow(new ChannelRow(g.Name, icon, big: false));
                    _rows[key] = row;
                    _appsPanel.Children.Add(row.Card);
                }
                row.SetSessions(g.List);
            }
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Le moteur web des bulles s'appelle « msedgewebview2 » : on l'affiche comme « Bubulle · pages web ».</summary>
    private static string FriendlyName(string exe, string path) =>
        exe.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ? "Bubulle · pages web" : AppCatalog.FriendlyName(path);

    private void UpdateMeters()
    {
        if (_device == null) return;
        try
        {
            _master.SetPeak(_device.AudioMeterInformation.MasterPeakValue);
            foreach (var row in _rows.Values) row.UpdatePeak();
        }
        catch
        {
            // Sortie débranchée entre deux mesures : le rafraîchissement suivant recharge tout.
        }
    }

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Une app (toutes ses sessions audio) dans le mélangeur.</summary>
    private sealed class AppRow : IDisposable
    {
        private readonly ChannelRow _row;
        private List<AudioSessionControl> _sessions = new();
        public Border Card { get; }

        public AppRow(ChannelRow row)
        {
            _row = row;
            Card = RowBox(row);
            row.VolumeChanged += v => Safely(() => { foreach (var s in _sessions) s.SimpleAudioVolume.Volume = v; });
            row.MuteToggled += () => Safely(() =>
            {
                bool mute = !_sessions.All(s => s.SimpleAudioVolume.Mute);
                foreach (var s in _sessions) s.SimpleAudioVolume.Mute = mute;
                _row.SetFromSystem(_row.Volume, mute);
            });
        }

        public void SetSessions(List<AudioSessionControl> sessions)
        {
            foreach (var old in _sessions) old.Dispose();
            _sessions = sessions;
            if (sessions.Count == 0) return;
            _row.SetFromSystem(sessions.Max(s => s.SimpleAudioVolume.Volume), sessions.All(s => s.SimpleAudioVolume.Mute));
        }

        public void UpdatePeak() => _row.SetPeak(_sessions.Count == 0 ? 0 : _sessions.Max(s => s.AudioMeterInformation.MasterPeakValue));

        /// <summary>Une app peut se fermer pendant qu'on règle son volume : on ignore l'erreur.</summary>
        private static void Safely(Action action)
        {
            try { action(); }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        public void Dispose()
        {
            foreach (var s in _sessions) s.Dispose();
            _sessions.Clear();
        }
    }

    /// <summary>
    /// Une ligne façon barre de vie de SAO : carré blanc avec l'icône, plaque sombre avec le nom, barre en biseau
    /// (verte, jaune puis rouge quand le volume baisse), enceinte à droite qui devient le badge jaune « paralysé »
    /// quand le son est coupé, onglet « 80 / 100 · ACTIF » et retour de son rouge comme la barre d'un boss.
    /// </summary>
    private sealed class ChannelRow : Grid
    {
        private static readonly Brush PlateBg = Frozen(new SolidColorBrush(Color.FromArgb(0xEE, 0x28, 0x2A, 0x30)));
        private static readonly Brush PlateBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush Paralyzed = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xD2, 0x1D)));
        private static readonly Brush ActiveText = Frozen(new SolidColorBrush(Color.FromRgb(0x9B, 0xE2, 0x7A)));
        private static readonly Brush BossTrack = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x2A)));

        private readonly HpBar _bar;
        private readonly TextBlock _value, _state;
        private readonly Border _mute;
        private readonly TextBlock _muteGlyph;
        private readonly System.Windows.Shapes.Path _bolt;
        private readonly Border _meter, _meterTrack;
        private double _shownPeak;
        private bool _fromSystem;
        private bool _muted;

        public event Action<float>? VolumeChanged;
        public event Action? MuteToggled;
        public float Volume => (float)(_bar.Value / 100);

        public ChannelRow(string name, ImageSource? icon, bool big)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Carré blanc avec l'icône de l'app (le « + » de la barre de vie de l'anime).
            FrameworkElement inner = icon != null
                ? new Image { Source = icon, Width = 22, Height = 22 }
                : new TextBlock { Text = big ? "" : "", FontFamily = Icons, FontSize = 17, Foreground = TextDark };
            RenderOptions.SetBitmapScalingMode(inner, BitmapScalingMode.HighQuality);
            inner.HorizontalAlignment = HorizontalAlignment.Center;
            inner.VerticalAlignment = VerticalAlignment.Center;
            var square = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(3), Background = Brushes.White, Child = inner,
                BorderBrush = RowLine, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
            };
            Panel.SetZIndex(square, 2);
            Children.Add(square);

            // Plaque sombre : nom, barre, enceinte.
            _bar = new HpBar { Height = 20, Margin = new Thickness(0, 0, 4, 0) };
            _bar.ValueChanged += () =>
            {
                UpdateTexts();
                if (!_fromSystem) VolumeChanged?.Invoke(Volume);
            };

            _muteGlyph = new TextBlock { FontFamily = Icons, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // Éclair de paralysie de SAO : un zigzag noir fin et penché.
            _bolt = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 9.5,0 L 2,9 L 6.6,9 L 4.2,16 L 12,6.4 L 7.4,6.4 L 10.6,0 Z"),
                Fill = TextDark, Width = 14, Height = 17, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            FrameworkElement MuteContent()
            {
                var g = new Grid();
                g.Children.Add(_muteGlyph);
                g.Children.Add(_bolt);
                return g;
            }
            _mute = new Border
            {
                Width = 28, Height = 20, Child = MuteContent(), Cursor = Cursors.Hand, Margin = new Thickness(4, 0, 14, 0),
                ToolTip = "Couper / remettre le son", Background = HitBrush, RenderTransformOrigin = new Point(0.5, 0.5),
            };
            _mute.MouseEnter += (_, _) => UpdateMuteLook(hover: true);
            _mute.MouseLeave += (_, _) => UpdateMuteLook(hover: false);
            _mute.MouseLeftButtonUp += (_, _) => MuteToggled?.Invoke();

            var label = new TextBlock
            {
                Text = name, Foreground = Brushes.White, FontSize = big ? 13.5 : 13, FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal,
                Width = big ? 112 : 96, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            var plateContent = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(label, Dock.Left);
            DockPanel.SetDock(_mute, Dock.Right);
            plateContent.Children.Add(label);
            plateContent.Children.Add(_mute);
            plateContent.Children.Add(_bar);
            var plate = new Border
            {
                Height = 30, Background = PlateBg, BorderBrush = PlateBorder, BorderThickness = new Thickness(1),
                Margin = new Thickness(-2, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Child = plateContent, VerticalAlignment = VerticalAlignment.Center,
            };
            SetColumn(plate, 1);
            Children.Add(plate);

            // Dessous : retour de son rouge (barre de boss) et onglet avec la valeur et l'état.
            _meterTrack = new Border { Height = 3, Background = BossTrack };
            _meter = new Border { Height = 3, Background = MeterFill, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var meterHost = new Grid { Margin = new Thickness(108, 2, 34, 0), VerticalAlignment = VerticalAlignment.Top };
            meterHost.Children.Add(_meterTrack);
            meterHost.Children.Add(_meter);

            _value = new TextBlock { Foreground = Brushes.White, FontSize = 11.5 };
            _state = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
            var tabContent = new StackPanel { Orientation = Orientation.Horizontal };
            tabContent.Children.Add(_value);
            tabContent.Children.Add(_state);
            var tab = new Border
            {
                Background = PlateBg, BorderBrush = PlateBorder, BorderThickness = new Thickness(1, 0, 1, 1), Padding = new Thickness(10, 1, 10, 2),
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 30, 0), Child = tabContent,
            };
            var under = new Grid { Margin = new Thickness(0, -1, 0, 0) };
            under.Children.Add(meterHost);
            under.Children.Add(tab);
            SetRow(under, 1);
            SetColumnSpan(under, 2);
            Children.Add(under);

            UpdateMuteLook(hover: false);
            UpdateTexts();
        }

        /// <summary>Met à jour la ligne avec les valeurs de Windows, sauf si tu es en train de bouger la barre.</summary>
        public void SetFromSystem(float volume, bool muted)
        {
            _muted = muted;
            UpdateMuteLook(hover: _mute.IsMouseOver);
            if (_bar.IsDragging) return;
            _fromSystem = true;
            _bar.Value = Math.Round(volume * 100);
            _fromSystem = false;
            UpdateTexts();
        }

        /// <summary>Retour de son : monte instantanément, redescend en douceur.</summary>
        public void SetPeak(float peak)
        {
            double target = _muted ? 0 : peak;
            _shownPeak = target > _shownPeak ? target : Math.Max(target, _shownPeak - 0.04);
            _meter.Width = Math.Max(0, _meterTrack.ActualWidth * _shownPeak);
        }

        private void UpdateTexts()
        {
            _value.Text = $"{(int)Math.Round(_bar.Value)} / 100";
            _state.Text = _muted ? "PARALYSÉ" : "ACTIF";
            _state.Foreground = _muted ? Paralyzed : ActiveText;
        }

        /// <summary>Son actif : enceinte blanche normale. Son coupé : badge jaune en biseau avec l'éclair de paralysie.</summary>
        private void UpdateMuteLook(bool hover)
        {
            _muteGlyph.Text = "";
            _muteGlyph.Visibility = _muted ? Visibility.Collapsed : Visibility.Visible;
            _muteGlyph.Foreground = hover ? Paralyzed : Brushes.White;
            _bolt.Visibility = _muted ? Visibility.Visible : Visibility.Collapsed;
            _mute.Background = _muted ? Paralyzed : HitBrush;
            // Badge « paralysé » penché comme le bout de la barre.
            double angle = Math.Atan(HpBar.EndSlope) * 180 / Math.PI;
            _mute.RenderTransform = _muted ? new SkewTransform(-angle, 0) : null;
            _bar.Opacity = _muted ? 0.4 : 1;
            UpdateTexts();
        }
    }

    /// <summary>Barre de vie en biseau : épaisse à gauche, plus fine après la marche. Glisser ou molette pour régler.</summary>
    private sealed class HpBar : FrameworkElement
    {
        private static readonly Brush FrameBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)));
        private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)));
        private static readonly Brush Green = Frozen(new LinearGradientBrush(Color.FromRgb(0x4F, 0xC2, 0x3A), Color.FromRgb(0x8B, 0xE0, 0x4E), 0));
        private static readonly Brush Yellow = Frozen(new LinearGradientBrush(Color.FromRgb(0xD6, 0xB8, 0x1A), Color.FromRgb(0xF0, 0xDE, 0x3A), 0));
        private static readonly Brush Red = Frozen(new LinearGradientBrush(Color.FromRgb(0xC8, 0x1E, 0x1E), Color.FromRgb(0xFF, 0x5A, 0x3C), 0));

        /// <summary>Pente commune des coupes en biais (bout de la barre, bout de la jauge, badge « paralysé »).</summary>
        public const double EndSlope = 0.38;

        private double _value = 100;
        public event Action? ValueChanged;
        public bool IsDragging { get; private set; }

        public double Value
        {
            get => _value;
            set
            {
                var v = Math.Clamp(value, 0, 100);
                if (v == _value) return;
                _value = v;
                InvalidateVisual();
                ValueChanged?.Invoke();
            }
        }

        public HpBar()
        {
            Cursor = Cursors.Hand;
            MouseLeftButtonDown += (_, e) => { IsDragging = true; CaptureMouse(); SetFrom(e); e.Handled = true; };
            MouseMove += (_, e) => { if (IsDragging) SetFrom(e); };
            MouseLeftButtonUp += (_, _) => { IsDragging = false; ReleaseMouseCapture(); };
            LostMouseCapture += (_, _) => IsDragging = false;
            MouseWheel += (_, e) => Value = Math.Round(Value + (e.Delta > 0 ? 5 : -5));
        }

        private void SetFrom(MouseEventArgs e) =>
            Value = Math.Round(e.GetPosition(this).X / Math.Max(1, ActualWidth) * 100);

        /// <summary>Forme de la barre : pleine hauteur jusqu'à 58-62 %, puis seulement la partie haute.</summary>
        private static Geometry Shape(Rect r)
        {
            double w = r.Width, h = r.Height, step = r.Top + h * 0.42;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(r.Left, r.Top), true, true);
                c.PolyLineTo(new[]
                {
                    // Le bout de la barre (partie fine) est coupé en diagonale.
                    new Point(r.Right, r.Top), new Point(r.Right - (step - r.Top) * EndSlope, step), new Point(r.Left + w * 0.62, step),
                    new Point(r.Left + w * 0.58, r.Bottom), new Point(r.Left, r.Bottom),
                }, true, true);
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 10 || h < 6) return;
            dc.DrawRectangle(HitBrush, null, new Rect(0, 0, w, h));
            dc.DrawGeometry(FrameBrush, null, Shape(new Rect(0, 0, w, h)));
            var inner = new Rect(2, 2, w - 4, h - 4);
            var innerShape = Shape(inner);
            dc.DrawGeometry(TrackBrush, null, innerShape);
            double filled = inner.Width * _value / 100;
            if (filled <= 0) return;
            // Le bout de la jauge est coupé en biais (comme dans l'anime) : à bas volume, il ne reste qu'un triangle.
            double slant = inner.Height * EndSlope;
            var fill = new StreamGeometry();
            using (var c = fill.Open())
            {
                c.BeginFigure(new Point(inner.Left, inner.Top), true, true);
                c.PolyLineTo(new[]
                {
                    new Point(inner.Left + filled, inner.Top),
                    new Point(Math.Max(inner.Left, inner.Left + filled - slant), inner.Bottom),
                    new Point(inner.Left, inner.Bottom),
                }, true, true);
            }
            fill.Freeze();
            dc.PushClip(innerShape);
            dc.DrawGeometry(_value > 50 ? Green : _value > 20 ? Yellow : Red, null, fill);
            dc.Pop();
        }
    }
}
