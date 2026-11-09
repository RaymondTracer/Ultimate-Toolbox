using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateToolbox.Contracts;

namespace UltimateToolbox.Dimmer.Feature;

internal sealed class DimmerView : StackPanel
{
    private static readonly TemperatureChoice[] TemperatureChoices =
    [
        new(null, "No temperature"),
        new(4500, "4500 K · warm"),
        new(5000, "5000 K"),
        new(5500, "5500 K"),
        new(6000, "6000 K · cool")
    ];

    private readonly DimmerFeature _feature;
    private readonly FeatureOperatingSystem _operatingSystem;
    private readonly CheckBox _enabledCheckBox;
    private readonly CheckBox _abovePopupsCheckBox;
    private readonly CheckBox _startupCheckBox;
    private readonly CheckBox _scheduleCheckBox;
    private readonly TextBox _cityTextBox;
    private readonly Button _resolveButton;
    private readonly TextBlock _locationStatus;
    private readonly TextBlock _status;
    private readonly TextBlock _transitionLabel;
    private readonly Slider _transitionSlider;
    private readonly StackPanel _displayCards;
    private bool _refreshing;
    private bool _locationBusy;

    public DimmerView(DimmerFeature feature, DimmerSettings settings, FeatureOperatingSystem operatingSystem)
    {
        _feature = feature;
        _operatingSystem = operatingSystem;
        Spacing = 14;
        MaxWidth = 900;

        var heading = new StackPanel { Spacing = 5 };
        heading.Children.Add(new TextBlock
        {
            Text = "Dimmer",
            FontSize = 28,
            FontWeight = FontWeight.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "Control brightness and color temperature per display. Each display keeps its own manual values and optional solar schedule.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });
        Children.Add(heading);

        var general = Card("General");
        _enabledCheckBox = new CheckBox { Content = "Enable dimming and temperature controls" };
        _enabledCheckBox.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetEnabled(_enabledCheckBox.IsChecked == true);
            }
        };
        general.Add(_enabledCheckBox);

        _abovePopupsCheckBox = new CheckBox { Content = "Keep the overlay above popup windows" };
        _abovePopupsCheckBox.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetKeepAbovePopups(_abovePopupsCheckBox.IsChecked == true);
            }
        };
        general.Add(_abovePopupsCheckBox);

        _startupCheckBox = new CheckBox { Content = "Start Dimmer when I sign in" };
        _startupCheckBox.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetStartOnSignIn(_startupCheckBox.IsChecked == true);
            }
        };
        general.Add(_startupCheckBox);
        Children.Add(general);

        var scheduleCard = Card("Sunrise and sunset schedule");
        scheduleCard.Add(new TextBlock
        {
            Text = "One smooth fade controls brightness and temperature. Choose “No temperature” at either endpoint to fade to or from neutral white.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4")
        });
        scheduleCard.Add(new TextBlock
        {
            Text = "Finding a location sends its name to Open-Meteo. Sunrise and sunset are cached; location is never requested automatically.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        });

        _scheduleCheckBox = new CheckBox { Content = "Enable the solar schedule" };
        _scheduleCheckBox.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetScheduleEnabled(_scheduleCheckBox.IsChecked == true);
            }
        };
        scheduleCard.Add(_scheduleCheckBox);

        var cityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        _cityTextBox = new TextBox
        {
            PlaceholderText = "City, region, country",
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _cityTextBox.TextChanged += (_, _) =>
        {
            if (!_refreshing && _cityTextBox.Text is { } text)
            {
                _feature.SetCity(text);
            }
        };
        _resolveButton = new Button { Content = "Find location", MinWidth = 112 };
        _resolveButton.Click += async (_, _) => await _feature.ResolveCityAsync(_cityTextBox.Text ?? string.Empty);
        cityRow.Children.Add(_cityTextBox);
        cityRow.Children.Add(_resolveButton);
        scheduleCard.Add(cityRow);

        _locationStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#AAB4C5")
        };
        scheduleCard.Add(_locationStatus);

        _transitionLabel = new TextBlock();
        scheduleCard.Add(_transitionLabel);
        _transitionSlider = new Slider
        {
            Minimum = 1,
            Maximum = 240,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _transitionSlider.ValueChanged += (_, args) =>
        {
            var minutes = Math.Clamp((int)Math.Round(args.NewValue), 1, 240);
            _transitionLabel.Text = $"Fade duration · {minutes} minutes";
            if (!_refreshing)
            {
                _feature.SetTransitionMinutes(minutes);
            }
        };
        scheduleCard.Add(_transitionSlider);
        Children.Add(scheduleCard);

        var displaySection = new StackPanel { Spacing = 10 };
        displaySection.Children.Add(new TextBlock
        {
            Text = "Displays",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        });
        _displayCards = new StackPanel { Spacing = 10 };
        displaySection.Children.Add(_displayCards);
        Children.Add(displaySection);

        _status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("#C3C9D4"),
            Margin = new Thickness(0, 3, 0, 0)
        };
        Children.Add(_status);

        RefreshSettings();
    }

    public void RefreshSettings()
    {
        _refreshing = true;
        try
        {
            var settings = _feature.Settings;
            _enabledCheckBox.IsChecked = settings.Enabled;
            _abovePopupsCheckBox.IsChecked = settings.KeepOverlayAbovePopups;
            _startupCheckBox.IsChecked = settings.StartOnSignIn;
            _startupCheckBox.IsEnabled = _operatingSystem is FeatureOperatingSystem.Windows or FeatureOperatingSystem.Linux;
            _scheduleCheckBox.IsChecked = settings.Schedule.Enabled;
            _cityTextBox.Text = settings.Schedule.City;
            _transitionSlider.Value = settings.Schedule.TransitionMinutes;
            _transitionLabel.Text = $"Fade duration · {settings.Schedule.TransitionMinutes} minutes";

            _displayCards.Children.Clear();
            var displays = _feature.ConnectedDisplaySettings;
            if (displays.Count == 0)
            {
                _displayCards.Children.Add(new TextBlock
                {
                    Text = "No connected displays were detected.",
                    Foreground = Brush("#AAB4C5")
                });
            }
            else
            {
                foreach (var display in displays)
                {
                    _displayCards.Children.Add(CreateDisplayCard(display.Display, display.Settings));
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    public void SetLocationBusy(bool busy, string message)
    {
        _locationBusy = busy;
        _locationStatus.Text = message;
        _resolveButton.IsEnabled = !busy;
    }

    public void SetStatus(string status, string locationStatus)
    {
        _status.Text = status;
        if (!_locationBusy)
        {
            _locationStatus.Text = locationStatus;
        }
    }

    private Control CreateDisplayCard(FeatureDisplay display, DisplayDimmerSettings settings)
    {
        var card = Card(display.Name ?? display.Id);
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };
        var enabled = new CheckBox { Content = "Apply effects", IsChecked = settings.Enabled };
        enabled.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetDisplayEnabled(display.Id, enabled.IsChecked == true);
            }
        };
        header.Children.Add(enabled);
        if (display.IsPrimary)
        {
            header.Children.Add(new TextBlock
            {
                Text = "Primary display",
                FontSize = 12,
                Foreground = Brush("#AAB4C5"),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        card.Add(header);
        card.Add(new TextBlock
        {
            Text = $"{display.Width} × {display.Height} · {display.X}, {display.Y}",
            FontSize = 12,
            Foreground = Brush("#AAB4C5")
        });

        var manual = new StackPanel { Spacing = 6, Margin = new Thickness(0, 7, 0, 4) };
        manual.Children.Add(SectionLabel("Manual settings"));
        manual.Children.Add(CreateBrightnessControl(
            "Brightness",
            settings.Brightness,
            brightness => _feature.SetManualBrightness(display.Id, brightness)));
        manual.Children.Add(CreateTemperatureControl(
            "Color temperature",
            settings.TemperatureKelvin,
            temperature => _feature.SetManualTemperature(display.Id, temperature)));
        card.Add(manual);

        var schedule = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var scheduleToggle = new CheckBox { Content = "Use the solar schedule for this display", IsChecked = settings.ScheduleEnabled };
        scheduleToggle.Click += (_, _) =>
        {
            if (!_refreshing)
            {
                _feature.SetDisplayScheduleEnabled(display.Id, scheduleToggle.IsChecked == true);
            }
        };
        schedule.Children.Add(scheduleToggle);
        schedule.Children.Add(CreateScheduleEndpoint(
            "Day",
            settings.DayBrightness,
            settings.DayTemperatureKelvin,
            isDay: true,
            display.Id));
        schedule.Children.Add(CreateScheduleEndpoint(
            "Night",
            settings.NightBrightness,
            settings.NightTemperatureKelvin,
            isDay: false,
            display.Id));
        card.Add(schedule);
        return card;
    }

    private Control CreateScheduleEndpoint(
        string label,
        int brightness,
        int? temperature,
        bool isDay,
        string displayId)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(12, 3, 0, 3) };
        panel.Children.Add(SectionLabel($"{label} values"));
        panel.Children.Add(CreateBrightnessControl(
            "Brightness",
            brightness,
            value => _feature.SetScheduledBrightness(displayId, isDay, value)));
        panel.Children.Add(CreateTemperatureControl(
            "Color temperature",
            temperature,
            value => _feature.SetScheduledTemperature(displayId, isDay, value)));
        return panel;
    }

    private Control CreateBrightnessControl(string label, int brightness, Action<int> update)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(132)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(78)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(16)));
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = brightness,
            VerticalAlignment = VerticalAlignment.Center
        };
        var value = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 100,
            Value = brightness,
            Increment = 1,
            FormatString = "0",
            Width = 78,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        var percent = new TextBlock { Text = "%", VerticalAlignment = VerticalAlignment.Center };
        var updatingPair = false;
        slider.ValueChanged += (_, args) =>
        {
            var selected = Math.Clamp((int)Math.Round(args.NewValue), 0, 100);
            if (!_refreshing && !updatingPair)
            {
                updatingPair = true;
                value.Value = selected;
                updatingPair = false;
                update(selected);
            }
        };
        value.ValueChanged += (_, args) =>
        {
            if (!_refreshing && !updatingPair && args.NewValue is { } numericValue)
            {
                var selected = Math.Clamp((int)Math.Round(numericValue), 0, 100);
                updatingPair = true;
                value.Value = selected;
                slider.Value = selected;
                updatingPair = false;
                update(selected);
            }
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        Grid.SetColumn(percent, 3);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        grid.Children.Add(percent);
        return grid;
    }

    private Control CreateTemperatureControl(string label, int? temperature, Action<int?> update)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(132)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        var picker = new ComboBox
        {
            ItemsSource = TemperatureChoices,
            SelectedItem = TemperatureChoices.First(choice => choice.Value == temperature),
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        picker.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && picker.SelectedItem is TemperatureChoice choice)
            {
                update(choice.Value);
            }
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(picker, 1);
        grid.Children.Add(name);
        grid.Children.Add(picker);
        return grid;
    }

    private static FeatureCard Card(string title) => new(title);

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brush("#D7DCE5")
    };

    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

    private sealed record TemperatureChoice(int? Value, string Label)
    {
        public override string ToString() => Label;
    }

}
