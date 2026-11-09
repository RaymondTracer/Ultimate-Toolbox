using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateToolbox.Contracts;
using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

internal sealed class PowerPlanView : StackPanel
{
    private readonly AutomaticPowerPlanFeature _feature;
    private readonly CheckBox _enabled;
    private readonly NumericUpDown _pollingInterval;
    private readonly NumericUpDown _gracePeriod;
    private readonly ComboBox _targetProfile;
    private readonly TextBlock _currentProfile;
    private readonly TextBlock _profileStatus;
    private readonly CheckBox _ignoreMsBuildWorkers;
    private readonly TextBox _processName;
    private readonly TextBlock _addFeedback;
    private readonly StackPanel _processList;
    private readonly StackPanel _dotNetVerbs;
    private readonly CheckBox[] _dotNetVerbChecks;
    private readonly TextBlock _runtimeStatus;
    private readonly TextBlock _activityStatus;
    private readonly TextBlock _ignoredStatus;
    private readonly TextBlock _errorStatus;
    private string _renderedProcesses = string.Empty;
    private string _renderedProfiles = string.Empty;
    private bool _refreshing;

    public PowerPlanView(AutomaticPowerPlanFeature feature)
    {
        _feature = feature;
        Spacing = 14;
        MaxWidth = 900;

        Children.Add(new TextBlock
        {
            Text = "Automatic Power Plan",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = "Switch to a temporary power profile while selected apps or build tools are active, then restore the profile that was active beforehand.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });

        var monitoring = Card("Monitoring");
        _enabled = new CheckBox { Content = "Enable automatic profile switching", IsChecked = true };
        _enabled.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetEnabled(_enabled.IsChecked == true);
            }
        };
        monitoring.Add(_enabled);
        var timing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pollingInterval = CreateNumber(1, 3600, 2, 1, value => _feature.SetPollingInterval(value));
        _gracePeriod = CreateNumber(0, 86400, 30, 1, value => _feature.SetGracePeriod(value));
        timing.Children.Add(NumberRow("Check every", _pollingInterval, "seconds"));
        timing.Children.Add(NumberRow("Restore after", _gracePeriod, "seconds without activity"));
        monitoring.Add(timing);
        monitoring.Add(new TextBlock
        {
            Text = "The default poll is two seconds with a 30-second restore grace period. The original script's personal app names are not preloaded; add any of your own below.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        });
        Children.Add(monitoring);

        var profileCard = Card("Temporary profile");
        profileCard.Add(new TextBlock
        {
            Text = "Windows resolves SCHEME_MIN (High performance) to an available plan GUID. Linux uses an available power profile such as performance when powerprofilesctl is installed.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });
        _targetProfile = new ComboBox
        {
            MinWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            PlaceholderText = "Choose an available profile"
        };
        _targetProfile.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _targetProfile.SelectedItem is PowerProfile profile)
            {
                _feature.SetTargetProfile(profile.Id);
            }
        };
        profileCard.Add(_targetProfile);
        _currentProfile = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        };
        _profileStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        };
        profileCard.Add(_currentProfile);
        profileCard.Add(_profileStatus);
        Children.Add(profileCard);

        var buildTools = Card("Build process rules");
        buildTools.Add(new TextBlock
        {
            Text = "The defaults watch exact compiler/build process names. Generic dotnet.exe is ignored; only selected direct CLI verbs count. Reusable MSBuild /nodemode workers are ignored by default.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });
        _ignoreMsBuildWorkers = new CheckBox
        {
            Content = "Ignore reusable MSBuild /nodemode worker processes",
            IsChecked = true
        };
        _ignoreMsBuildWorkers.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetIgnoreReusableMsBuildWorkers(_ignoreMsBuildWorkers.IsChecked == true);
            }
        };
        buildTools.Add(_ignoreMsBuildWorkers);

        _dotNetVerbs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _dotNetVerbChecks = WatchedProcessPolicy.DefaultDotNetVerbs
            .Select(verb =>
            {
                var checkBox = new CheckBox { Content = $"dotnet {verb}" };
                checkBox.Click += (_, _) =>
                {
                    if (!_refreshing)
                    {
                        _feature.SetDotNetVerbEnabled(verb, checkBox.IsChecked == true);
                    }
                };
                return checkBox;
            })
            .ToArray();
        foreach (var checkBox in _dotNetVerbChecks)
        {
            _dotNetVerbs.Children.Add(checkBox);
        }
        buildTools.Add(_dotNetVerbs);

        var addProcessRow = new Grid { ColumnSpacing = 8 };
        addProcessRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        addProcessRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _processName = new TextBox
        {
            PlaceholderText = "Additional exact process name (for example, my-game)",
            MinWidth = 240
        };
        _processName.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
            {
                AddProcessName();
            }
        };
        var addButton = new Button { Content = "Add process" };
        addButton.Click += (_, _) => AddProcessName();
        Grid.SetColumn(_processName, 0);
        Grid.SetColumn(addButton, 1);
        addProcessRow.Children.Add(_processName);
        addProcessRow.Children.Add(addButton);
        buildTools.Add(addProcessRow);
        _addFeedback = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        };
        buildTools.Add(_addFeedback);
        _processList = new StackPanel { Spacing = 6 };
        buildTools.Add(_processList);
        Children.Add(buildTools);

        var statusCard = Card("Status");
        _runtimeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        _activityStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#C3C9D4") };
        _ignoredStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#AAB4C5") };
        _errorStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brush("#F0B46A") };
        statusCard.Add(_runtimeStatus);
        statusCard.Add(_activityStatus);
        statusCard.Add(_ignoredStatus);
        statusCard.Add(_errorStatus);
        Children.Add(statusCard);
    }

    public void Refresh(AutomaticPowerPlanSettings settings, PowerPlanViewSnapshot snapshot)
    {
        _refreshing = true;
        _enabled.IsChecked = settings.Enabled;
        _pollingInterval.Value = settings.PollingIntervalSeconds;
        _gracePeriod.Value = settings.GracePeriodSeconds;
        _ignoreMsBuildWorkers.IsChecked = settings.IgnoreReusableMsBuildWorkerNodes;
        for (var index = 0; index < _dotNetVerbChecks.Length; index++)
        {
            _dotNetVerbChecks[index].IsChecked = settings.WatchedDotNetVerbs.Contains(
                WatchedProcessPolicy.DefaultDotNetVerbs[index],
                StringComparer.OrdinalIgnoreCase);
        }

        var profileSignature = string.Join("|", snapshot.Profiles.Select(profile => $"{profile.Id}\u001f{profile.Name}"));
        if (!string.Equals(profileSignature, _renderedProfiles, StringComparison.Ordinal))
        {
            _renderedProfiles = profileSignature;
            _targetProfile.ItemsSource = snapshot.Profiles.ToArray();
        }

        _targetProfile.SelectedItem = snapshot.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, settings.TargetProfileId, StringComparison.OrdinalIgnoreCase));
        _targetProfile.IsEnabled = snapshot.Profiles.Count > 0;

        var processSignature = string.Join("|", settings.WatchedProcessNames);
        if (!string.Equals(processSignature, _renderedProcesses, StringComparison.Ordinal))
        {
            _renderedProcesses = processSignature;
            RenderProcessNames(settings.WatchedProcessNames);
        }
        _refreshing = false;

        _currentProfile.Text = snapshot.CurrentProfileId is null
            ? "Current profile: not queried yet"
            : $"Current or last observed profile: {ProfileName(snapshot.Profiles, snapshot.CurrentProfileId)}";
        _profileStatus.Text = snapshot.Profiles.Count == 0
            ? "No profiles are available from this OS power service."
            : settings.TargetProfileId is null
                ? "No temporary profile is selected."
                : $"Temporary target: {ProfileName(snapshot.Profiles, settings.TargetProfileId)}";
        _runtimeStatus.Text = snapshot.StatusMessage ?? "Checking watched processes…";
        _activityStatus.Text = string.IsNullOrWhiteSpace(snapshot.ActivityDetails)
            ? "No watched activity detected."
            : $"Active: {snapshot.ActivityDetails}";
        _ignoredStatus.Text = snapshot.GraceSecondsRemaining is { } remaining
            ? $"Restore grace: {remaining} second(s) remaining."
            : string.IsNullOrWhiteSpace(snapshot.IgnoredActivityDetails)
                ? string.Empty
                : snapshot.IgnoredActivityDetails;
        _errorStatus.Text = snapshot.ErrorMessage ?? string.Empty;
    }

    private void AddProcessName()
    {
        if (_feature.TryAddWatchedProcessName(_processName.Text, out var message))
        {
            _processName.Text = string.Empty;
        }

        _addFeedback.Text = message;
    }

    private void RenderProcessNames(IReadOnlyList<string> processNames)
    {
        _processList.Children.Clear();
        foreach (var processName in processNames)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.Children.Add(new TextBlock
            {
                Text = processName,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("#D7DCE5")
            });
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => _feature.RemoveWatchedProcessName(processName);
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            _processList.Children.Add(row);
        }
    }

    private NumericUpDown CreateNumber(
        int minimum,
        int maximum,
        int initial,
        int increment,
        Action<int> changed)
    {
        var control = new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = initial,
            Increment = increment,
            FormatString = "0",
            Width = maximum > 9999 ? 105 : 82
        };
        control.ValueChanged += (_, args) =>
        {
            if (!_refreshing && args.NewValue is { } value)
            {
                changed((int)Math.Round(value));
            }
        };
        return control;
    }

    private static StackPanel NumberRow(string label, NumericUpDown value, string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(value);
        row.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static FeatureCard Card(string title) => new(title);

    private static string ProfileName(IReadOnlyList<PowerProfile> profiles, string profileId) =>
        profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? profileId;

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
