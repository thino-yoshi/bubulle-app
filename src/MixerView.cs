using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace Bulles;

/// <summary>
/// Mélangeur audio de Bulles : volume général de la sortie, puis une carte par app qui joue du son
/// (jauge, bouton muet, vumètre en direct). Les sessions d'un même programme sont regroupées.
/// </summary>
public sealed class MixerView : UserControl
{
    private static readonly Brush CardBg = Frozen(new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x29)));
    private static readonly Brush CardBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextMain = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush TextSoft = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush MeterTrack = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush MeterFill = Frozen(new LinearGradientBrush(
        Color.FromRgb(0x3D, 0xA5, 0xFF), Color.FromRgb(0x7C, 0xF2, 0xD0), 0));
    private static readonly Brush Danger = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly ListBox _devices;
    private Popup? _devicePopup;
    private TextBlock? _deviceLabel;
    private readonly StackPanel _appsPanel;
    private readonly TextBlock _empty;
    private readonly ChannelRow _master;
    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _meters, _refresh;
    private MMDevice? _device;
    private bool _syncingDevices;

    public MixerView()
    {
        _devices = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = TextMain };
        _devices.SelectionChanged += (_, _) =>
        {
            if (_syncingDevices) return;
            _devicePopup!.IsOpen = false;
            SelectDevice(_devices.SelectedItem as ListBoxItem);
        };
        var deviceButton = BuildDeviceButton();

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
            Foreground = TextSoft, FontSize = 13, Margin = new Thickness(4, 6, 0, 0),
        };

        var header = new DockPanel { Margin = new Thickness(2, 0, 2, 10) };
        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = "Mélangeur audio", Foreground = TextMain, FontSize = 17, FontWeight = FontWeights.SemiBold });
        titleStack.Children.Add(new TextBlock { Text = "Sortie", Foreground = TextSoft, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) });
        titleStack.Children.Add(deviceButton);
        header.Children.Add(titleStack);

        var root = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        root.Children.Add(header);
        root.Children.Add(MakeCard(_master));
        root.Children.Add(new TextBlock { Text = "APPLICATIONS", Foreground = TextSoft, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 16, 0, 8) });
        root.Children.Add(_appsPanel);
        root.Children.Add(_empty);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        _meters = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meters.Tick += (_, _) => UpdateMeters();
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _refresh.Tick += (_, _) => RefreshSessions();

        // Ne travaille que quand la bulle est visible.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { LoadDevices(); RefreshSessions(); _meters.Start(); _refresh.Start(); }
            else { _meters.Stop(); _refresh.Stop(); }
        };
    }

    // ---------- Sorties audio ----------

    /// <summary>Sélecteur de sortie sombre : un bouton avec le nom de la sortie, qui déroule la liste.</summary>
    private FrameworkElement BuildDeviceButton()
    {
        _deviceLabel = new TextBlock { Foreground = TextMain, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock
        {
            Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            Foreground = TextSoft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        var content = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        content.Children.Add(_deviceLabel);

        var button = new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 0), Cursor = Cursors.Hand, Child = content,
        };
        _devicePopup = new Popup
        {
            PlacementTarget = button, Placement = PlacementMode.Bottom, VerticalOffset = 4,
            StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF8, 0x1C, 0x21, 0x2B)), BorderBrush = CardBorder,
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
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
                var item = new ListBoxItem { Content = label, Tag = d.ID, Foreground = TextMain, Padding = new Thickness(8, 6, 8, 6) };
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
                    key = Path.GetFileNameWithoutExtension(path);
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

    /// <summary>Le moteur web des bulles s'appelle « msedgewebview2 » : on l'affiche comme « Bulles (pages web) ».</summary>
    private static string FriendlyName(string exe, string path) =>
        exe.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) ? "Bulles · pages web" : AppCatalog.FriendlyName(path);

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

    private static Border MakeCard(UIElement child) => new()
    {
        Background = CardBg,
        BorderBrush = CardBorder,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12, 10, 12, 10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child,
    };

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
            Card = MakeCard(row);
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

    /// <summary>Une ligne : icône, nom, pourcentage, bouton muet, jauge et vumètre.</summary>
    private sealed class ChannelRow : Grid
    {
        private readonly Slider _slider;
        private readonly TextBlock _percent;
        private readonly TextBlock _muteGlyph;
        private readonly Border _meter;
        private readonly Border _meterTrack;
        private double _shownPeak;
        private bool _fromSystem;
        private bool _muted;

        public event Action<float>? VolumeChanged;
        public event Action? MuteToggled;
        public float Volume => (float)(_slider.Value / 100);

        public ChannelRow(string name, ImageSource? icon, bool big)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            double iconSize = big ? 34 : 28;
            FrameworkElement iconEl = icon != null
                ? new Image { Source = icon, Width = iconSize, Height = iconSize }
                : new TextBlock
                {
                    Text = big ? "" : "",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = big ? 24 : 20, Foreground = new SolidColorBrush(LauncherWindow.Accent),
                    Width = iconSize, TextAlignment = TextAlignment.Center,
                };
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            iconEl.Margin = new Thickness(0, 0, 12, 0);
            RenderOptions.SetBitmapScalingMode(iconEl, BitmapScalingMode.HighQuality);
            Children.Add(iconEl);

            _percent = new TextBlock { Foreground = TextSoft, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var top = new DockPanel();
            DockPanel.SetDock(_percent, Dock.Right);
            top.Children.Add(_percent);
            top.Children.Add(new TextBlock
            {
                Text = name, Foreground = TextMain, FontSize = big ? 14.5 : 13.5,
                FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            });

            _slider = new Slider { Minimum = 0, Maximum = 100, Style = Theme.Slider, Margin = new Thickness(0, 2, 0, 0) };
            _slider.ValueChanged += (_, _) =>
            {
                _percent.Text = $"{(int)Math.Round(_slider.Value)} %";
                if (!_fromSystem) VolumeChanged?.Invoke(Volume);
            };
            _slider.MouseWheel += (_, e) => _slider.Value = Math.Clamp(_slider.Value + (e.Delta > 0 ? 5 : -5), 0, 100);

            _meterTrack = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = MeterTrack, Margin = new Thickness(0, 2, 0, 0) };
            _meter = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = MeterFill, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            var meterHost = new Grid();
            meterHost.Children.Add(_meterTrack);
            meterHost.Children.Add(_meter);
            meterHost.Margin = new Thickness(0, 2, 0, 0);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            middle.Children.Add(top);
            middle.Children.Add(_slider);
            middle.Children.Add(meterHost);
            SetColumn(middle, 1);
            Children.Add(middle);

            _muteGlyph = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var mute = new Button { Content = _muteGlyph, ToolTip = "Couper / remettre le son", Margin = new Thickness(10, 0, 0, 0) };
            StyleRoundButton(mute);
            mute.Click += (_, _) => MuteToggled?.Invoke();
            SetColumn(mute, 2);
            Children.Add(mute);
            UpdateMuteLook();
        }

        /// <summary>Met à jour la ligne avec les valeurs de Windows, sauf si tu es en train de bouger la jauge.</summary>
        public void SetFromSystem(float volume, bool muted)
        {
            _muted = muted;
            UpdateMuteLook();
            if (_slider.IsMouseCaptureWithin) return;
            _fromSystem = true;
            _slider.Value = Math.Round(volume * 100);
            _percent.Text = $"{(int)_slider.Value} %";
            _fromSystem = false;
        }

        /// <summary>Vumètre : monte instantanément, redescend en douceur.</summary>
        public void SetPeak(float peak)
        {
            double target = _muted ? 0 : peak;
            _shownPeak = target > _shownPeak ? target : Math.Max(target, _shownPeak - 0.04);
            _meter.Width = Math.Max(0, _meterTrack.ActualWidth * _shownPeak);
        }

        private void UpdateMuteLook()
        {
            _muteGlyph.Text = _muted ? "" : "";
            _muteGlyph.Foreground = _muted ? Danger : Brushes.White;
            _slider.Opacity = _muted ? 0.45 : 1;
        }

        private static void StyleRoundButton(Button b)
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border)) { Name = "bg" };
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            template.VisualTree = border;
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), "bg"));
            template.Triggers.Add(hover);
            b.Template = template;
            b.Width = 32;
            b.Height = 32;
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
            b.VerticalAlignment = VerticalAlignment.Center;
        }
    }
}
