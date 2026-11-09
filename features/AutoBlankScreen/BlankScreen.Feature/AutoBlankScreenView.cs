using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateToolbox.Contracts;
using UltimateToolbox.BlankScreen;

namespace UltimateToolbox.AutoBlankScreen.Feature;

internal sealed class AutoBlankScreenView : StackPanel
{
    private readonly AutoBlankScreenFeature _feature;
    private readonly NumericUpDown _hours;
    private readonly NumericUpDown _minutes;
    private readonly CheckBox _monitorAllAudio;
    private readonly TextBox _processEntry;
    private readonly TextBlock _processFeedback;
    private readonly StackPanel _processes;
    private readonly ComboBox _recentProcesses;
    private readonly Button _addRecent;
    private readonly TextBlock _status;
    private readonly TextBlock _idle;
    private readonly TextBlock _audio;
    private readonly TextBlock _capabilities;
    private readonly TextBlock _error;
    private readonly TextBlock _diagnostics;
    private bool _refreshing;

    public AutoBlankScreenView(AutoBlankScreenFeature feature)
    {
        _feature = feature;
        Spacing = 14;
        MaxWidth = 900;

        Children.Add(new TextBlock
        {
            Text = "Auto Blank Screen",
            FontSize = 26,
            FontWeight = FontWeight.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = "Cover every display after inactivity, wake when input resumes, and keep selected audio apps from triggering a blank screen.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });

        var timeoutCard = new FeatureCard("Idle timeout");
        timeoutCard.Add(new TextBlock
        {
            Text = "The original ten-minute default and 500 ms idle checks are retained. Audio playback resets the idle clock.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });

        var timeoutRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _hours = CreateNumber(0, 999);
        _minutes = CreateNumber(0, 59);
        timeoutRow.Children.Add(NumberRow("Hours", _hours));
        timeoutRow.Children.Add(NumberRow("Minutes", _minutes));
        var apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => _feature.SetTimeout((int)_hours.Value.GetValueOrDefault(), (int)_minutes.Value.GetValueOrDefault());
        timeoutRow.Children.Add(apply);
        timeoutCard.Add(timeoutRow);

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddPreset(presetRow, "1 hour", 1, 0);
        AddPreset(presetRow, "10 minutes", 0, 10);
        AddPreset(presetRow, "1 minute", 0, 1);
        timeoutCard.Add(presetRow);
        var manualBlank = new Button
        {
            Content = "Blank displays now",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        manualBlank.Click += (_, _) => _feature.RequestManualBlank();
        timeoutCard.Add(manualBlank);
        Children.Add(timeoutCard);

        var audioCard = new FeatureCard("Audio blocking apps");
        audioCard.Add(new TextBlock
        {
            Text = "Names match case-insensitive substrings in active output-session process or app labels. An empty list monitors every audio app.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });
        _monitorAllAudio = new CheckBox { Content = "Monitor all audio apps" };
        _monitorAllAudio.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetMonitorAllAudio(_monitorAllAudio.IsChecked == true);
            }
        };
        audioCard.Add(_monitorAllAudio);

        var processRow = new Grid { ColumnSpacing = 8 };
        processRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        processRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _processEntry = new TextBox
        {
            PlaceholderText = "Process name or app-label match",
            MinWidth = 240
        };
        _processEntry.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
            {
                AddProcessEntry();
            }
        };
        var addProcess = new Button { Content = "Add app" };
        addProcess.Click += (_, _) => AddProcessEntry();
        Grid.SetColumn(_processEntry, 0);
        Grid.SetColumn(addProcess, 1);
        processRow.Children.Add(_processEntry);
        processRow.Children.Add(addProcess);
        audioCard.Add(processRow);

        _processFeedback = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#AAB4C5") };
        audioCard.Add(_processFeedback);
        _processes = new StackPanel { Spacing = 5 };
        audioCard.Add(_processes);

        var recentRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _recentProcesses = new ComboBox
        {
            MinWidth = 240,
            PlaceholderText = "Recently detected audio app"
        };
        _addRecent = new Button { Content = "Add selected" };
        _addRecent.Click += (_, _) =>
        {
            if (_recentProcesses.SelectedItem is string processName)
            {
                SetProcessFeedback(_feature.TryAddAudioProcess(processName, out var message), message);
            }
        };
        recentRow.Children.Add(_recentProcesses);
        recentRow.Children.Add(_addRecent);
        audioCard.Add(recentRow);
        Children.Add(audioCard);

        var statusCard = new FeatureCard("Status");
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
        _idle = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#C3C9D4") };
        _audio = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#C3C9D4") };
        _capabilities = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#AAB4C5") };
        _error = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#F0B46A") };
        _diagnostics = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#AAB4C5") };
        statusCard.Add(_status);
        statusCard.Add(_idle);
        statusCard.Add(_audio);
        statusCard.Add(_capabilities);
        statusCard.Add(_error);
        statusCard.Add(_diagnostics);
        Children.Add(statusCard);
    }

    public void Refresh(AutoBlankScreenViewSnapshot snapshot)
    {
        _refreshing = true;
        _hours.Value = snapshot.Settings.TimeoutHours;
        _minutes.Value = snapshot.Settings.TimeoutMinutes;
        var monitorAll = snapshot.Settings.MonitoredAudioProcesses.Count == 0;
        _monitorAllAudio.IsChecked = monitorAll;
        _processEntry.IsEnabled = !monitorAll;
        _addRecent.IsEnabled = !monitorAll;

        var signature = string.Join("|", snapshot.Settings.MonitoredAudioProcesses);
        if (!string.Equals(signature, _renderedProcesses, StringComparison.Ordinal))
        {
            _renderedProcesses = signature;
            RenderProcesses(snapshot.Settings.MonitoredAudioProcesses);
        }

        _recentProcesses.ItemsSource = snapshot.RecentlyDetectedProcesses.ToArray();
        _recentProcesses.IsEnabled = !monitorAll && snapshot.RecentlyDetectedProcesses.Count > 0;
        _addRecent.IsEnabled = !monitorAll && snapshot.RecentlyDetectedProcesses.Count > 0;
        _refreshing = false;

        _status.Text = snapshot.StatusMessage;
        _idle.Text = snapshot.IdleTime is { } idle
            ? $"Idle time: {FormatDuration(idle)}"
            : "Idle time: unavailable";
        _audio.Text = snapshot.AudioStatus.IsAvailable
            ? snapshot.AudioStatus.HasAudio
                ? snapshot.AudioStatus.HasMeasuredLevel
                    ? $"Audio blocking: {snapshot.AudioStatus.ProcessName ?? "audio"} · {snapshot.AudioStatus.PeakLevel * 100:0}% peak"
                    : $"Audio blocking: active stream from {snapshot.AudioStatus.ProcessName ?? "audio"} · live peak meter unavailable"
                : "Audio blocking: no selected audio is active"
            : "Audio blocking: unavailable";
        _capabilities.Text = $"Input source: {snapshot.IdleSourceDescription}";
        _error.Text = snapshot.ErrorMessage ?? string.Empty;
        _diagnostics.Text = string.Join(Environment.NewLine, snapshot.Diagnostics);
    }

    private void AddPreset(StackPanel row, string label, int hours, int minutes)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => _feature.SetTimeout(hours, minutes);
        row.Children.Add(button);
    }

    private static NumericUpDown CreateNumber(int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Width = 90,
        Increment = 1
    };

    private static StackPanel NumberRow(string label, NumericUpDown value) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center },
            value
        }
    };

    private void AddProcessEntry()
    {
        if (!_feature.TryAddAudioProcess(_processEntry.Text, out var message))
        {
            SetProcessFeedback(false, message);
            return;
        }

        _processEntry.Clear();
        SetProcessFeedback(true, message);
    }

    private void SetProcessFeedback(bool succeeded, string message)
    {
        _processFeedback.Text = message;
        _processFeedback.Foreground = Brush(succeeded ? "#75D7A6" : "#F0B46A");
    }

    private void RenderProcesses(IReadOnlyList<string> processNames)
    {
        _processes.Children.Clear();
        foreach (var processName in processNames)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var name = new TextBlock { Text = processName, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button { Content = "Remove", Padding = new Thickness(8, 3) };
            remove.Click += (_, _) => _feature.RemoveAudioProcess(processName);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(remove, 1);
            row.Children.Add(name);
            row.Children.Add(remove);
            _processes.Children.Add(row);
        }
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} h {value.Minutes:00} min"
        : value.TotalMinutes >= 1
            ? $"{value.Minutes} min {value.Seconds:00} s"
            : $"{Math.Ceiling(Math.Max(0, value.TotalSeconds)):0} s";

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

    private string _renderedProcesses = string.Empty;
}
