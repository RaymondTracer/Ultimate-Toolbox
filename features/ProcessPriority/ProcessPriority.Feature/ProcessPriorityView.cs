using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateToolbox.Contracts;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

internal sealed class ProcessPriorityView : StackPanel
{
    private readonly ProcessPriorityFeature _feature;
    private readonly NumericUpDown _pollInterval;
    private readonly TextBox _processName;
    private readonly TextBlock _feedback;
    private readonly TextBlock _summary;
    private readonly StackPanel _rulesPanel;
    private readonly Dictionary<string, TextBlock> _ruleStatuses = new(StringComparer.OrdinalIgnoreCase);
    private string _renderedRules = string.Empty;
    private bool _refreshing;

    public ProcessPriorityView(ProcessPriorityFeature feature)
    {
        _feature = feature;
        Spacing = 14;
        MaxWidth = 900;

        Children.Add(new TextBlock
        {
            Text = "Process Priority",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold
        });
        Children.Add(new TextBlock
        {
            Text = "Keep selected processes at the priority you choose. Windows uses priority classes; Linux uses per-thread nice values.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });

        var settingsCard = Card("Monitoring");
        var intervalRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        intervalRow.Children.Add(new TextBlock
        {
            Text = "Check every",
            VerticalAlignment = VerticalAlignment.Center
        });
        _pollInterval = new NumericUpDown
        {
            Minimum = ProcessPriorityPolicy.MinimumPollIntervalSeconds,
            Maximum = ProcessPriorityPolicy.MaximumPollIntervalSeconds,
            Increment = 1,
            FormatString = "0",
            Width = 82
        };
        _pollInterval.ValueChanged += (_, args) =>
        {
            if (!_refreshing && args.NewValue is { } value)
            {
                _feature.SetPollInterval((int)Math.Round(value));
            }
        };
        intervalRow.Children.Add(_pollInterval);
        intervalRow.Children.Add(new TextBlock
        {
            Text = "seconds",
            VerticalAlignment = VerticalAlignment.Center
        });
        settingsCard.Add(intervalRow);
        settingsCard.Add(new TextBlock
        {
            Text = "Linux nice ranges from -20 (highest) to 19 (lowest). Raising priority may be denied by your account's limits; the feature never requests administrator access.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        });
        settingsCard.Add(new TextBlock
        {
            Text = "Rules start empty. Another watcher or a Windows IFEO priority rule can compete for the same process.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        });
        Children.Add(settingsCard);

        var addCard = Card("Add a process rule");
        var addRow = new Grid { ColumnSpacing = 8 };
        addRow.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        addRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        _processName = new TextBox
        {
            PlaceholderText = "Process name (for example, zandronum)",
            MinWidth = 220
        };
        var addButton = new Button { Content = "Add rule" };
        addButton.Click += (_, _) => AddRule();
        _processName.KeyDown += (_, args) =>
        {
            if (args.Key == Avalonia.Input.Key.Enter)
            {
                AddRule();
            }
        };
        Grid.SetColumn(_processName, 0);
        Grid.SetColumn(addButton, 1);
        addRow.Children.Add(_processName);
        addRow.Children.Add(addButton);
        addCard.Add(addRow);
        _feedback = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        };
        addCard.Add(_feedback);
        Children.Add(addCard);

        var rulesCard = Card("Rules");
        _summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        };
        rulesCard.Add(_summary);
        _rulesPanel = new StackPanel { Spacing = 10 };
        rulesCard.Add(_rulesPanel);
        Children.Add(rulesCard);
    }

    public void Refresh(
        ProcessPrioritySettings settings,
        IReadOnlyDictionary<string, ProcessRuleStatus> statuses,
        string? scanError)
    {
        _refreshing = true;
        _pollInterval.Value = settings.PollIntervalSeconds;
        _refreshing = false;

        var signature = RuleSignature(settings.Rules);
        if (!string.Equals(signature, _renderedRules, StringComparison.Ordinal))
        {
            _renderedRules = signature;
            RenderRules(settings.Rules, statuses);
        }
        else
        {
            UpdateRuleStatuses(settings.Rules, statuses);
        }

        var enabledRuleCount = settings.Rules.Count(rule => rule.Enabled);
        var matchingProcessCount = statuses.Values.Sum(status => status.MatchingProcesses);
        var statusText = settings.Rules.Count == 0
            ? "No process rules yet. Add a rule above to begin monitoring."
            : enabledRuleCount == 0
                ? $"All {settings.Rules.Count} rule(s) are disabled."
                : $"Checking {enabledRuleCount} enabled rule(s) every {settings.PollIntervalSeconds} second(s); {matchingProcessCount} matching process(es) found in the last scan.";
        _summary.Text = string.IsNullOrWhiteSpace(scanError)
            ? statusText
            : $"Last scan failed: {scanError}\n{statusText}";
    }

    private void AddRule()
    {
        if (_feature.TryAddRule(_processName.Text, out var message))
        {
            _processName.Text = string.Empty;
        }

        _feedback.Text = message;
    }

    private void RenderRules(
        IReadOnlyList<ProcessPriorityRule> rules,
        IReadOnlyDictionary<string, ProcessRuleStatus> statuses)
    {
        _rulesPanel.Children.Clear();
        _ruleStatuses.Clear();

        if (rules.Count == 0)
        {
            _rulesPanel.Children.Add(new TextBlock
            {
                Text = "No rules configured.",
                Foreground = Brush("#AAB4C5")
            });
            return;
        }

        foreach (var rule in rules)
        {
            var card = new FeatureCard();

            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            heading.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            var name = new TextBlock
            {
                Text = rule.ProcessName,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            var enabled = new CheckBox
            {
                Content = "Enabled",
                IsChecked = rule.Enabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            enabled.Click += (_, _) => _feature.SetRuleEnabled(rule.ProcessName, enabled.IsChecked == true);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => _feature.RemoveRule(rule.ProcessName);
            Grid.SetColumn(name, 0);
            Grid.SetColumn(enabled, 1);
            Grid.SetColumn(remove, 2);
            heading.Children.Add(name);
            heading.Children.Add(enabled);
            heading.Children.Add(remove);
            card.Add(heading);

            var targets = new Grid { ColumnSpacing = 12 };
            targets.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            targets.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            targets.Children.Add(CreateWindowsTarget(rule));
            var linuxTarget = CreateLinuxTarget(rule);
            Grid.SetColumn(linuxTarget, 1);
            targets.Children.Add(linuxTarget);
            card.Add(targets);

            var status = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = Brush("#AAB4C5")
            };
            _ruleStatuses[rule.ProcessName] = status;
            card.Add(status);
            _rulesPanel.Children.Add(card);
        }

        UpdateRuleStatuses(rules, statuses);
    }

    private Control CreateWindowsTarget(ProcessPriorityRule rule)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Windows priority class",
            FontSize = 12,
            Foreground = Brush("#C3C9D4")
        });
        var picker = new ComboBox
        {
            ItemsSource = Enum.GetValues<WindowsPriorityLevel>(),
            SelectedItem = rule.WindowsPriority,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && picker.SelectedItem is WindowsPriorityLevel priority)
            {
                _feature.SetWindowsPriority(rule.ProcessName, priority);
            }
        };
        panel.Children.Add(picker);
        return panel;
    }

    private Control CreateLinuxTarget(ProcessPriorityRule rule)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock
        {
            Text = "Linux nice value",
            FontSize = 12,
            Foreground = Brush("#C3C9D4")
        });
        var value = new NumericUpDown
        {
            Minimum = -20,
            Maximum = 19,
            Increment = 1,
            FormatString = "0",
            Value = rule.LinuxNiceValue,
            Width = 100
        };
        value.ValueChanged += (_, args) =>
        {
            if (!_refreshing && args.NewValue is { } numericValue)
            {
                _feature.SetLinuxNiceValue(rule.ProcessName, (int)Math.Round(numericValue));
            }
        };
        panel.Children.Add(value);
        return panel;
    }

    private void UpdateRuleStatuses(
        IReadOnlyList<ProcessPriorityRule> rules,
        IReadOnlyDictionary<string, ProcessRuleStatus> statuses)
    {
        foreach (var rule in rules)
        {
            if (!_ruleStatuses.TryGetValue(rule.ProcessName, out var text))
            {
                continue;
            }

            if (!rule.Enabled)
            {
                text.Text = "Disabled";
            }
            else if (!statuses.TryGetValue(rule.ProcessName, out var status) || status.MatchingProcesses == 0)
            {
                text.Text = "Watching · no matching processes found";
            }
            else
            {
                text.Text = $"{status.MatchingProcesses} process(es): {status.Applied} corrected, " +
                    $"{status.AlreadyCorrect} already correct, {status.PermissionDenied} denied, {status.Failed} failed";
            }
        }
    }

    private static string RuleSignature(IEnumerable<ProcessPriorityRule> rules) => string.Join(
        "|",
        rules.Select(rule => $"{rule.ProcessName}\u001f{rule.Enabled}\u001f{rule.WindowsPriority}\u001f{rule.LinuxNiceValue}"));

    private static FeatureCard Card(string title) => new(title);

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
