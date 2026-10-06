using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using System.Windows.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AudioRouteControl;

public partial class MainWindow : Window
{
    private readonly AudioApp _audio = new();
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly DispatcherTimer _timer;
    private readonly Forms.NotifyIcon _tray;
    private readonly TrayMixerWindow _mixer;
    private readonly Dictionary<string, AppRow> _rows = new();
    private readonly HashSet<string> _appliedRules = new();
    private List<AudioOutput> _outputs = new();
    private string? _defaultOutputId;
    private string _outputsSignature = "";
    private bool _allowClose;
    private bool _refreshInFlight;
    private bool _updatingCards;
    private bool _updatingStartupCheckBox;
    private bool _gridMode;

    public MainWindow()
    {
        InitializeComponent();
        _updatingStartupCheckBox = true;
        StartWithWindowsCheckBox.IsChecked = StartupManager.IsEnabled();
        _settings.StartWithWindows = StartWithWindowsCheckBox.IsChecked == true;
        _updatingStartupCheckBox = false;
        _tray = CreateTrayIcon();
        _mixer = new TrayMixerWindow(_audio, _settings, ShowFromTray, HideMixer);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _timer.Tick += async (_, _) => await RefreshSessionsAsync(false);
        Loaded += async (_, _) =>
        {
            await RefreshSessionsAsync(true);
            _timer.Start();
            if (_settings.StartMinimized) HideToTray();
        };
        Closing += OnClosing;
    }

    private async Task RefreshSessionsAsync(bool showStatus)
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;
        try
        {
            var snapshot = await Task.Run(AudioApp.Capture);
            _updatingCards = true;
            try
            {
                var outputs = snapshot.Outputs;
                _defaultOutputId = snapshot.DefaultOutputId;
                var outputSignature = string.Join("|", outputs.Select(x => x.Id + "=" + x.Name));
                var outputsChanged = outputSignature != _outputsSignature;
                _outputs = outputs;
                _outputsSignature = outputSignature;
                if (outputsChanged) UpdateOutputLists();

                var sessions = snapshot.Sessions;
                var seen = new HashSet<string>();
                foreach (var session in sessions)
                {
                    seen.Add(session.Key);
                    if (!_rows.ContainsKey(session.Key))
                    {
                        ApplySavedSettings(session);
                        AddRow(session);
                    }
                    else
                    {
                        UpdateRow(_rows[session.Key], session);
                    }
                }

                foreach (var stale in _rows.Keys.Where(key => !seen.Contains(key)).ToList())
                {
                    ListPanel.Children.Remove(_rows[stale].ListCard);
                    GridPanel.Children.Remove(_rows[stale].GridCard);
                    _rows.Remove(stale);
                    _appliedRules.Remove(stale);
                }

                ActiveCountText.Text = sessions.Count.ToString();
                DeviceCountText.Text = _outputs.Count.ToString();
                UpdateEmptyState(sessions.Count == 0);
                if (showStatus) StatusText.Text = $"Обновлено в {DateTime.Now:HH:mm:ss}";
            }
            finally
            {
                _updatingCards = false;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка аудиосистемы: " + ex.Message;
            UpdateEmptyState(true);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void ApplySavedSettings(AudioAppSession session)
    {
        var rule = FindRule(session);
        if (!_appliedRules.Add(session.Key)) return;
        var targetDeviceId = rule?.DeviceId;
        if (string.IsNullOrWhiteSpace(targetDeviceId)) targetDeviceId = _defaultOutputId;
        if (!string.IsNullOrWhiteSpace(targetDeviceId) && targetDeviceId != session.DeviceId && _outputs.Any(x => x.Id == targetDeviceId))
            _ = _audio.RouteAsync(session.Key, targetDeviceId);
        var volume = GetSavedVolume(rule, session);
        if (volume is float savedVolume && Math.Abs(savedVolume - session.Volume) > 0.01f)
            _ = _audio.SetVolumeAsync(session.Key, savedVolume);
        var muted = GetSavedMute(rule, session);
        if (muted is bool savedMute && savedMute != session.Muted)
            _ = _audio.SetMuteAsync(session.Key, savedMute);
    }

    private void AddRow(AudioAppSession session)
    {
        var row = new AppRow(session, CreateCard(session, false), CreateCard(session, true));
        _rows[session.Key] = row;
        ListPanel.Children.Add(row.ListCard);
        GridPanel.Children.Add(row.GridCard);
        UpdateOutputLists(row);
        UpdateRow(row, session);
    }

    private AppCard CreateCard(AudioAppSession session, bool compact)
    {
        var card = new Border
        {
            Background = (MediaBrush)FindResource("SurfaceBrush"),
            BorderBrush = (MediaBrush)FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = compact ? new Thickness(16) : new Thickness(18, 15, 14, 15),
            Margin = compact ? new Thickness(0, 0, 12, 12) : new Thickness(0, 0, 0, 10),
            Width = compact ? 350 : double.NaN
        };
        var shell = new Grid();
        var content = new StackPanel();
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = (MediaBrush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 11, 0) };
        Grid.SetColumn(dot, 0); heading.Children.Add(dot);
        var names = new StackPanel();
        var name = new TextBlock { FontSize = compact ? 14 : 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var process = new TextBlock { Foreground = (MediaBrush)FindResource("TextMutedBrush"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        names.Orientation = Orientation.Horizontal;
        process.Margin = new Thickness(10, 1, 0, 0);
        names.Children.Add(name); names.Children.Add(process); Grid.SetColumn(names, 1); heading.Children.Add(names);
        var percent = new TextBlock { Foreground = (MediaBrush)FindResource("TextMutedBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(percent, 2);
        content.Children.Add(heading);

        var controls = new Grid { Margin = new Thickness(0, 13, 0, 0) };
        if (!compact)
        {
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(39) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        }
        else
        {
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var route = new ComboBox { ToolTip = "Устройство вывода" };
        if (!compact) { Grid.SetColumn(route, 0); route.Margin = new Thickness(0, 0, 12, 0); }
        else { Grid.SetRow(route, 0); Grid.SetColumnSpan(route, 3); route.Margin = new Thickness(0, 0, 0, 10); }
        controls.Children.Add(route);

        var slider = new Slider { Minimum = 0, Maximum = 100, VerticalAlignment = VerticalAlignment.Center };
        SliderClickBehavior.Attach(slider);
        var label = new TextBlock { Foreground = (MediaBrush)FindResource("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var mute = new ToggleButton { Style = (Style)FindResource("MuteButtonStyle"), Content = "🔊", ToolTip = "Отключить звук" };
        if (!compact)
        {
            Grid.SetColumn(slider, 1); controls.Children.Add(slider);
            Grid.SetColumn(label, 2); controls.Children.Add(label);
            Grid.SetColumn(mute, 4); controls.Children.Add(mute);
        }
        else
        {
            var volumeRow = new Grid(); volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); volumeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetRow(slider, 1); controls.Children.Add(slider);
            Grid.SetRow(label, 1); Grid.SetColumn(label, 1); label.Margin = new Thickness(10, 0, 48, 0); controls.Children.Add(label);
            Grid.SetRow(mute, 1); Grid.SetColumn(mute, 2); mute.HorizontalAlignment = HorizontalAlignment.Right; controls.Children.Add(mute);
        }

        route.SelectionChanged += (_, _) =>
        {
            if (route.SelectedItem is not string name) return;
            var output = _outputs.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
            if (output is null) return;
            route.Tag = output.Id;
            if (!_updatingCards && output.Id != session.DeviceId) ApplyRoute(session.Key, output.Id);
        };
        slider.ValueChanged += (_, _) =>
        {
            label.Text = $"{slider.Value:0}%";
        };
        slider.PreviewMouseLeftButtonUp += (_, _) =>
            ApplyVolume(session.Key, (float)slider.Value / 100);
        mute.Checked += (_, _) => ApplyMute(session.Key, true, mute);
        mute.Unchecked += (_, _) => ApplyMute(session.Key, false, mute);
        content.Children.Add(controls);
        shell.Children.Add(content); card.Child = shell;
        return new AppCard(card, name, process, percent, route, slider, label, mute);
    }

    private void UpdateRow(AppRow row, AudioAppSession session)
    {
        row.Session = session;
        UpdateCard(row.ListView, session);
        UpdateCard(row.GridView, session);
    }

    private void UpdateCard(AppCard card, AudioAppSession session)
    {
        card.Name.Text = session.DisplayName;
        card.Process.Text = $"{session.ProcessName}.exe  ·  PID {session.ProcessId}";
        var rule = FindRule(session);
        var volume = (GetSavedVolume(rule, session) ?? session.Volume) * 100;
        if (!card.Volume.IsKeyboardFocusWithin && !card.Volume.IsMouseOver) card.Volume.Value = volume;
        card.Mute.IsChecked = GetSavedMute(rule, session) ?? session.Muted;
        card.Mute.Content = card.Mute.IsChecked == true ? "🔇" : "🔊";
        if (!card.Route.IsDropDownOpen && !card.Route.IsKeyboardFocusWithin)
        {
            var selectedId = GetAvailableOutputId(rule?.DeviceId, _defaultOutputId ?? session.DeviceId);
            card.Route.Tag = selectedId;
            card.Route.SelectedItem = _outputs.FirstOrDefault(x => x.Id == selectedId)?.Name;
        }
    }

    private void UpdateOutputLists(AppRow? only = null)
    {
        var cards = only is null ? _rows.Values.SelectMany(x => new[] { x.ListView, x.GridView }) : new[] { only.ListView, only.GridView };
        foreach (var card in cards)
        {
            var selectedId = card.Route.Tag as string;
            selectedId = GetAvailableOutputId(selectedId, _defaultOutputId);
            card.Route.Tag = selectedId;
            card.Route.ItemsSource = _outputs.Select(x => x.Name).ToList();
            card.Route.SelectedItem = _outputs.FirstOrDefault(x => x.Id == selectedId)?.Name;
        }
    }

    private string? GetAvailableOutputId(string? preferredId, string? fallbackId)
    {
        if (!string.IsNullOrWhiteSpace(preferredId) && _outputs.Any(x => x.Id == preferredId))
            return preferredId;
        if (!string.IsNullOrWhiteSpace(fallbackId) && _outputs.Any(x => x.Id == fallbackId))
            return fallbackId;
        return _outputs.FirstOrDefault()?.Id;
    }

    private void UpdateEmptyState(bool empty)
    {
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = empty || _gridMode ? Visibility.Collapsed : Visibility.Visible;
        GridScroll.Visibility = empty || !_gridMode ? Visibility.Collapsed : Visibility.Visible;
    }

    private RouteRule? FindRule(AudioAppSession session) => _settings.Rules.FirstOrDefault(r => string.Equals(r.Key, session.ProcessName, StringComparison.OrdinalIgnoreCase) || string.Equals(r.ExecutableName, session.ProcessName + ".exe", StringComparison.OrdinalIgnoreCase));
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
        if (rule is null) { rule = new RouteRule { Key = session.ProcessName, DisplayName = session.DisplayName, ExecutableName = session.ProcessName + ".exe" }; _settings.Rules.Add(rule); }
        rule.SessionVolumes ??= new();
        rule.SessionMutes ??= new();
        return rule;
    }
    private AudioAppSession? Current(string key) => _rows.TryGetValue(key, out var row) ? row.Session : null;

    private async void ApplyRoute(string key, string deviceId)
    {
        var session = Current(key); if (session is null) return;
        var ok = await _audio.RouteAsync(key, deviceId);
        var rule = GetOrCreateRule(session); rule.DeviceId = deviceId; SettingsStore.Save(_settings);
        if (ok) StatusText.Text = $"{session.ProcessName}: маршрут сохранён";
    }
    private void ApplyVolume(string key, float volume)
    {
        _ = _audio.SetVolumeAsync(key, volume);
        var session = Current(key);
        if (session is null) return;
        var rule = GetOrCreateRule(session);
        rule.SessionVolumes[session.PersistenceKey ?? session.Key] = volume;
        SettingsStore.Save(_settings);
    }
    private void ApplyMute(string key, bool muted, ToggleButton button)
    {
        _ = _audio.SetMuteAsync(key, muted); button.Content = muted ? "🔇" : "🔊";
        var session = Current(key); if (session is null) return;
        var rule = GetOrCreateRule(session); rule.SessionMutes[session.PersistenceKey ?? session.Key] = muted; SettingsStore.Save(_settings);
    }

    private void ListViewButton_Click(object sender, RoutedEventArgs e) { _gridMode = false; ListViewButton.IsChecked = true; GridViewButton.IsChecked = false; UpdateEmptyState(_rows.Count == 0); }
    private void GridViewButton_Click(object sender, RoutedEventArgs e) { _gridMode = true; ListViewButton.IsChecked = false; GridViewButton.IsChecked = true; UpdateEmptyState(_rows.Count == 0); }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2) ToggleMaximize(); else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingStartupCheckBox) return;

        var enabled = StartWithWindowsCheckBox.IsChecked == true;
        if (!StartupManager.SetEnabled(enabled))
        {
            _updatingStartupCheckBox = true;
            StartWithWindowsCheckBox.IsChecked = !enabled;
            _updatingStartupCheckBox = false;
            StatusText.Text = "Не удалось изменить настройку автозапуска.";
            return;
        }

        _settings.StartWithWindows = enabled;
        SettingsStore.Save(_settings);
        StatusText.Text = enabled ? "Автозапуск включён." : "Автозапуск отключён.";
    }
    private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□"; }

    private void HideToTray()
    {
        HideMixer();
        _timer.Stop();
        Hide();
        ShowInTaskbar = false;
        _tray.Visible = true;
    }
    private void ShowFromTray()
    {
        HideMixer();
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _timer.Start();
        _ = RefreshSessionsAsync(false);
    }
    private void ShowMixer() { _timer.Stop(); Hide(); ShowInTaskbar = false; _mixer.ShowMixer(); }
    private void HideMixer()
    {
        if (_mixer.IsVisible) _mixer.Hide();
    }
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _timer.Stop();
        _mixer.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _audio.Dispose();
    }
    private void ExitApplication() { _allowClose = true; Close(); Application.Current.Shutdown(); }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Быстрый микшер", null, (_, _) => ShowMixer());
        menu.Items.Add("Открыть настройки", null, (_, _) => ShowFromTray());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitApplication());
        var tray = new Forms.NotifyIcon { Text = "Audio Route Control", Icon = MakeIcon(), ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) ShowMixer(); };
        return tray;
    }

    private static Drawing.Icon MakeIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32); using var g = Drawing.Graphics.FromImage(bitmap); g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Drawing.Color.FromArgb(16, 19, 27));
        using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(95, 190, 255)); using var pen = new Drawing.Pen(Drawing.Color.FromArgb(95, 190, 255), 2.4f);
        g.FillPolygon(brush, new[] { new Drawing.Point(5, 13), new Drawing.Point(11, 13), new Drawing.Point(19, 7), new Drawing.Point(19, 25), new Drawing.Point(11, 19), new Drawing.Point(5, 19) }); g.DrawArc(pen, 14, 9, 14, 14, -55, 110);
        var handle = bitmap.GetHicon();
        using var source = Drawing.Icon.FromHandle(handle);
        var result = (Drawing.Icon)source.Clone();
        DestroyIcon(handle);
        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private sealed class AppRow
    {
        public AppRow(AudioAppSession session, AppCard listView, AppCard gridView) { Session = session; ListView = listView; GridView = gridView; }
        public AudioAppSession Session { get; set; }
        public AppCard ListView { get; }
        public AppCard GridView { get; }
        public Border ListCard => ListView.Card;
        public Border GridCard => GridView.Card;
    }
    private sealed class AppCard
    {
        public AppCard(Border card, TextBlock name, TextBlock process, TextBlock percent, ComboBox route, Slider volume, TextBlock label, ToggleButton mute) { Card = card; Name = name; Process = process; Percent = percent; Route = route; Volume = volume; Label = label; Mute = mute; }
        public Border Card { get; }
        public TextBlock Name { get; }
        public TextBlock Process { get; }
        public TextBlock Percent { get; }
        public ComboBox Route { get; }
        public Slider Volume { get; }
        public TextBlock Label { get; }
        public ToggleButton Mute { get; }
    }
}

internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioRouteControl";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch { return false; }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath)) return false;
                key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
    }
}


