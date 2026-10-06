using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace AudioRouteControl;

public partial class TrayMixerWindow : Window
{
    private readonly AudioApp _audio;
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private readonly Action _hide;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TrayRow> _rows = new();
    private readonly TextBlock _emptyState;
    private bool _updating;
    private bool _refreshInFlight;

    public TrayMixerWindow(AudioApp audio, AppSettings settings, Action openSettings, Action hide)
    {
        InitializeComponent();
        _audio = audio;
        _settings = settings;
        _openSettings = openSettings;
        _hide = hide;
        _emptyState = new TextBlock
        {
            Text = "Нет активных источников",
            Foreground = (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(0, 20, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        Deactivated += (_, _) => { if (IsVisible) _hide(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        Closed += (_, _) => _timer.Stop();
    }

    public void ShowMixer()
    {
        _ = RefreshAsync();
        Show();
        UpdateLayout();
        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea;
        if (area is not null)
        {
            Left = area.Value.Right - ActualWidth - 14;
            Top = area.Value.Bottom - ActualHeight - 14;
        }
        Activate();
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        try
        {
            var snapshot = await Task.Run(AudioApp.Capture);
            var sessions = snapshot.Sessions;
            _updating = true;
            var seen = new HashSet<string>();

            foreach (var session in sessions)
            {
                seen.Add(session.Key);
                if (_rows.TryGetValue(session.Key, out var row))
                    UpdateSource(row, session);
                else
                    AddSource(session);
            }

            foreach (var stale in _rows.Keys.Where(key => !seen.Contains(key)).ToList())
            {
                SourcesPanel.Children.Remove(_rows[stale].Card);
                _rows.Remove(stale);
            }

            if (sessions.Count == 0)
            {
                if (!SourcesPanel.Children.Contains(_emptyState))
                    SourcesPanel.Children.Add(_emptyState);
            }
            else
            {
                SourcesPanel.Children.Remove(_emptyState);
            }
        }
        finally
        {
            _updating = false;
            _refreshInFlight = false;
        }
    }

    private void AddSource(AudioAppSession session)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 7)
        };
        var panel = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(dot, 0);
        heading.Children.Add(dot);

        var name = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(name, 1);
        heading.Children.Add(name);

        var percent = new TextBlock
        {
            Foreground = (Brush)FindResource("TextMutedBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(percent, 2);
        heading.Children.Add(percent);
        panel.Children.Add(heading);

        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rule = FindRule(session);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = session.Key
        };
        SliderClickBehavior.Attach(slider);
        Grid.SetColumn(slider, 0);
        row.Children.Add(slider);

        var mute = new ToggleButton
        {
            Style = (Style)FindResource("IconToggleStyle"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(mute, 1);
        row.Children.Add(mute);
        panel.Children.Add(row);
        card.Child = panel;

        var trayRow = new TrayRow(session, card, name, percent, slider, mute);
        _rows[session.Key] = trayRow;
        slider.ValueChanged += (_, _) =>
        {
            percent.Text = $"{slider.Value:0}%";
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
            SaveVolume(trayRow, (float)slider.Value / 100);
        mute.Checked += (_, _) =>
        {
            if (!_updating) SaveMute(trayRow, true);
        };
        mute.Unchecked += (_, _) =>
        {
            if (!_updating) SaveMute(trayRow, false);
        };

        SourcesPanel.Children.Add(card);
        UpdateSource(trayRow, session);
    }

    private void UpdateSource(TrayRow row, AudioAppSession session)
    {
        row.Session = session;
        var rule = FindRule(session);
        row.Name.Text = session.DisplayName;
        row.Slider.Tag = session.Key;

        var volume = (GetSavedVolume(rule, session) ?? session.Volume) * 100;
        if (!row.Slider.IsKeyboardFocusWithin && !row.Slider.IsMouseOver)
            row.Slider.Value = volume;
        row.Percent.Text = $"{row.Slider.Value:0}%";

        if (!row.Mute.IsKeyboardFocusWithin && !row.Mute.IsMouseOver)
        {
            var muted = GetSavedMute(rule, session) ?? session.Muted;
            row.Mute.IsChecked = muted;
            row.Mute.Content = muted ? "🔇" : "🔊";
        }
    }

    private void SaveVolume(TrayRow row, float value)
    {
        _ = _audio.SetVolumeAsync(row.Session.Key, value);
        var rule = GetOrCreateRule(row.Session);
        rule.SessionVolumes[row.Session.PersistenceKey ?? row.Session.Key] = value;
        SettingsStore.Save(_settings);
    }

    private void SaveMute(TrayRow row, bool muted)
    {
        _ = _audio.SetMuteAsync(row.Session.Key, muted);
        row.Mute.Content = muted ? "🔇" : "🔊";
        var rule = GetOrCreateRule(row.Session);
        rule.SessionMutes[row.Session.PersistenceKey ?? row.Session.Key] = muted;
        SettingsStore.Save(_settings);
    }

    private RouteRule? FindRule(AudioAppSession session) =>
        _settings.Rules.FirstOrDefault(x => string.Equals(x.Key, session.ProcessName, StringComparison.OrdinalIgnoreCase));

    private static float? GetSavedVolume(RouteRule? rule, AudioAppSession session)
    {
        if (rule is null) return null;
        var key = session.PersistenceKey ?? session.Key;
        return rule.SessionVolumes?.TryGetValue(key, out var sessionVolume) == true ? sessionVolume : rule.Volume;
    }

    private static bool? GetSavedMute(RouteRule? rule, AudioAppSession session)
    {
        if (rule is null) return null;
        var key = session.PersistenceKey ?? session.Key;
        return rule.SessionMutes?.TryGetValue(key, out var sessionMute) == true ? sessionMute : rule.Muted;
    }

    private RouteRule GetOrCreateRule(AudioAppSession session)
    {
        var rule = FindRule(session);
        if (rule is null)
        {
            rule = new RouteRule
            {
                Key = session.ProcessName,
                DisplayName = session.DisplayName,
                ExecutableName = session.ProcessName + ".exe"
            };
            _settings.Rules.Add(rule);
        }
        rule.SessionVolumes ??= new();
        rule.SessionMutes ??= new();
        return rule;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _hide();
        _openSettings();
    }

    private sealed class TrayRow
    {
        public TrayRow(AudioAppSession session, Border card, TextBlock name, TextBlock percent, Slider slider, ToggleButton mute)
        {
            Session = session;
            Card = card;
            Name = name;
            Percent = percent;
            Slider = slider;
            Mute = mute;
        }

        public AudioAppSession Session { get; set; }
        public Border Card { get; }
        public TextBlock Name { get; }
        public TextBlock Percent { get; }
        public Slider Slider { get; }
        public ToggleButton Mute { get; }
    }
}
