using System.Reflection;
using System.Text.Json;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateToolbox.Contracts;
using UltimateToolbox.Dimmer;

namespace UltimateToolbox.Dimmer.Feature;

public sealed class DimmerFeature : IToolboxFeature
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SolarCacheLifetime = TimeSpan.FromHours(12);
    private static readonly int[] QuickBrightnessValues = [25, 50, 75, 100];
    private static readonly int?[] QuickTemperatureValues = [null, 4500, 5000, 5500, 6000];

    private readonly Dictionary<string, DisplayRuntime> _displays = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private IFeatureContext? _context;
    private DimmerSettings _settings = new();
    private OpenMeteoClient? _solarClient;
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _saveTimer;
    private DimmerView? _view;
    private TrayIcon? _trayIcon;
    private TrayIcons? _previousTrayIcons;
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _solarRefreshPending;
    private bool _disposed;
    private string? _settingsWarning;
    private Task _solarOperation = Task.CompletedTask;

    public FeatureMetadata Metadata { get; } = new(
        "dimmer",
        "Dimmer",
        "Set per-display brightness and color temperature, with smooth sunrise and sunset schedules.",
        [FeatureOperatingSystem.Windows, FeatureOperatingSystem.Linux]);

    public Task StartAsync(IFeatureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dispatcher.UIThread.VerifyAccess();
        _context = context;
        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(_shutdown.Cancel);
        }

        LoadSettings();
        _solarClient = new OpenMeteoClient();
        context.DisplayOverlays.DisplaysChanged += OnDisplaysChanged;
        RefreshDisplays();
        TryInstallTrayIcon();

        _updateTimer = new DispatcherTimer { Interval = RefreshInterval };
        _updateTimer.Tick += (_, _) => OnUpdateTick();
        _updateTimer.Start();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) => SavePendingSettings();

        ApplyAllDisplays();
        UpdateStatus();
        return Task.CompletedTask;
    }

    public Control CreateView(IFeatureContext context)
    {
        Dispatcher.UIThread.VerifyAccess();
        _view = new DimmerView(this, _settings, context.CurrentOperatingSystem);
        UpdateStatus();
        return _view;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellationRegistration.Dispose();
        _shutdown.Cancel();
        _updateTimer?.Stop();
        _saveTimer?.Stop();
        if (!_solarOperation.IsCompleted)
        {
            try
            {
                await _solarOperation;
            }
            catch (OperationCanceledException)
            {
            }
        }

        SaveSettings();

        if (_context is not null)
        {
            _context.DisplayOverlays.DisplaysChanged -= OnDisplaysChanged;
        }

        foreach (var display in _displays.Values)
        {
            try
            {
                display.Dispose();
            }
            catch (Exception exception)
            {
                _context?.Logger.Error($"Could not restore display '{display.Display.Id}' while stopping Dimmer.", exception);
            }
        }

        _displays.Clear();
        _solarClient?.Dispose();
        _solarClient = null;

        if (_trayIcon is not null && Application.Current is { } application)
        {
            if (_previousTrayIcons is not null)
            {
                TrayIcon.SetIcons(application, _previousTrayIcons);
            }

            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _shutdown.Dispose();
    }

    internal DimmerSettings Settings => _settings;

    internal IReadOnlyList<(FeatureDisplay Display, DisplayDimmerSettings Settings)> ConnectedDisplaySettings =>
        _displays.Values.Select(display => (display.Display, display.Settings)).ToArray();

    internal void SetEnabled(bool enabled)
    {
        _settings.Enabled = enabled;
        SettingsChanged(refreshView: true);
    }

    internal void SetKeepAbovePopups(bool enabled)
    {
        _settings.KeepOverlayAbovePopups = enabled;
        SettingsChanged(refreshView: true);
    }

    internal void SetStartOnSignIn(bool enabled)
    {
        if (UserStartupRegistration.SetEnabled(enabled, _context!.CurrentOperatingSystem, out var error))
        {
            _settings.StartOnSignIn = enabled;
            _settingsWarning = null;
            SettingsChanged();
            return;
        }

        _settings.StartOnSignIn = !enabled;
        _settingsWarning = error ?? "Could not update sign-in startup.";
        _view?.RefreshSettings();
        UpdateStatus();
    }

    internal void SetManualBrightness(string displayId, int brightness)
    {
        if (FindSettings(displayId) is { } settings)
        {
            settings.Brightness = Math.Clamp(brightness, 0, 100);
            SettingsChanged();
        }
    }

    internal void SetManualTemperature(string displayId, int? temperature)
    {
        if (FindSettings(displayId) is { } settings)
        {
            settings.TemperatureKelvin = NormalizeTemperature(temperature);
            SettingsChanged();
        }
    }

    internal void SetDisplayEnabled(string displayId, bool enabled)
    {
        if (FindSettings(displayId) is { } settings)
        {
            settings.Enabled = enabled;
            SettingsChanged();
        }
    }

    internal void SetDisplayScheduleEnabled(string displayId, bool enabled)
    {
        if (FindSettings(displayId) is { } settings)
        {
            settings.ScheduleEnabled = enabled;
            SettingsChanged();
        }
    }

    internal void SetScheduledBrightness(string displayId, bool day, int brightness)
    {
        if (FindSettings(displayId) is not { } settings)
        {
            return;
        }

        if (day)
        {
            settings.DayBrightness = Math.Clamp(brightness, 0, 100);
        }
        else
        {
            settings.NightBrightness = Math.Clamp(brightness, 0, 100);
        }

        SettingsChanged();
    }

    internal void SetScheduledTemperature(string displayId, bool day, int? temperature)
    {
        if (FindSettings(displayId) is not { } settings)
        {
            return;
        }

        if (day)
        {
            settings.DayTemperatureKelvin = NormalizeTemperature(temperature);
        }
        else
        {
            settings.NightTemperatureKelvin = NormalizeTemperature(temperature);
        }

        SettingsChanged();
    }

    internal void SetScheduleEnabled(bool enabled)
    {
        _settings.Schedule.Enabled = enabled;
        SettingsChanged();
        if (enabled)
        {
            CheckSolarRefresh();
        }
    }

    internal void SetCity(string city)
    {
        _settings.Schedule.City = city.Trim();
        QueueSave();
    }

    internal void SetTransitionMinutes(int minutes)
    {
        _settings.Schedule.TransitionMinutes = Math.Clamp(minutes, 1, 240);
        SettingsChanged();
    }

    internal async Task ResolveCityAsync(string city)
    {
        if (_solarClient is null || _shutdown.IsCancellationRequested || !_solarOperation.IsCompleted)
        {
            return;
        }

        SetCity(city);
        _view?.SetLocationBusy(true, "Looking up the location and sunrise/sunset times…");
        var operation = ResolveCityCoreAsync(city);
        _solarOperation = operation;
        await operation;
    }

    private async Task ResolveCityCoreAsync(string city)
    {
        var solarClient = _solarClient;
        if (solarClient is null)
        {
            return;
        }

        try
        {
            var result = await solarClient.ResolveCityAsync(city, _shutdown.Token);
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            _settings.Schedule.City = result.City;
            _settings.Schedule.ResolvedLocation = result.ResolvedLocation;
            _settings.Schedule.TimeZone = result.TimeZone;
            _settings.Schedule.Latitude = result.Latitude;
            _settings.Schedule.Longitude = result.Longitude;
            _settings.Schedule.LocationResolved = true;
            _settings.Schedule.SolarDays = result.SolarDays.ToList();
            _settings.Schedule.FetchedAtUtc = result.FetchedAtUtc;
            _settingsWarning = null;
            SettingsChanged();
            _view?.SetLocationBusy(false, $"Using {result.ResolvedLocation} · {result.TimeZone}");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _context?.Logger.Error("Could not resolve the solar schedule location.", exception);
            _settingsWarning = exception.GetBaseException().Message;
            _view?.SetLocationBusy(false, _settingsWarning);
            UpdateStatus();
        }
    }

    private static int? NormalizeTemperature(int? temperature) => temperature is null
        ? null
        : Math.Clamp(temperature.Value, 4500, 6000);

    private DisplayDimmerSettings? FindSettings(string displayId) =>
        _settings.Displays.FirstOrDefault(display =>
            string.Equals(display.DisplayId, displayId, StringComparison.OrdinalIgnoreCase));

    private void LoadSettings()
    {
        try
        {
            _settings = _context!.Settings.Load<DimmerSettings>() ?? ImportLegacySettings() ?? new DimmerSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _settingsWarning = $"Settings could not be read: {exception.GetBaseException().Message}";
            _context!.Logger.Error(_settingsWarning, exception);
            _settings = ImportLegacySettings() ?? new DimmerSettings();
        }

        _settings.Normalize();
        SaveSettings();
    }

    private DimmerSettings? ImportLegacySettings()
    {
        if (_context?.CurrentOperatingSystem != FeatureOperatingSystem.Windows)
        {
            return null;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming))
        {
            return null;
        }

        var imported = LegacyDimmerConfigImporter.TryImport(
            Path.Combine(roaming, "dimmer", "config.json"),
            _context.DisplayOverlays.GetDisplays());
        if (imported is not null)
        {
            _context.Logger.Info("Imported settings from the existing Dimmer profile.");
        }

        return imported;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Normalize();
            _context?.Settings.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _settingsWarning = $"Settings could not be saved: {exception.GetBaseException().Message}";
            _context?.Logger.Error(_settingsWarning, exception);
            UpdateStatus();
        }
    }

    private void SettingsChanged(bool refreshView = false, bool immediate = false)
    {
        _settings.Normalize();
        if (refreshView)
        {
            _view?.RefreshSettings();
        }

        ApplyAllDisplays();
        UpdateStatus();

        if (immediate || _saveTimer is null)
        {
            SaveSettings();
        }
        else
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        UpdateTrayMenu();
    }

    private void QueueSave()
    {
        if (_saveTimer is null)
        {
            SaveSettings();
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SavePendingSettings()
    {
        _saveTimer?.Stop();
        SaveSettings();
    }

    private void OnUpdateTick()
    {
        if (_disposed)
        {
            return;
        }

        ApplyAllDisplays();
        CheckSolarRefresh();
        UpdateStatus();
    }

    private void CheckSolarRefresh()
    {
        var schedule = _settings.Schedule;
        if (_solarRefreshPending || !schedule.Enabled || !schedule.LocationResolved || _solarClient is null)
        {
            return;
        }

        var fetched = schedule.FetchedAtUtc > 0
            ? DateTimeOffset.FromUnixTimeSeconds(schedule.FetchedAtUtc)
            : DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - fetched < SolarCacheLifetime)
        {
            return;
        }

        if (_solarOperation.IsCompleted)
        {
            var operation = RefreshSolarScheduleAsync();
            _solarOperation = operation;
        }
    }

    private async Task RefreshSolarScheduleAsync()
    {
        if (_solarClient is null || _solarRefreshPending || !_settings.Schedule.LocationResolved)
        {
            return;
        }

        _solarRefreshPending = true;
        _view?.SetLocationBusy(true, "Refreshing sunrise and sunset times…");
        try
        {
            var schedule = _settings.Schedule;
            var latitude = schedule.Latitude;
            var longitude = schedule.Longitude;
            var result = await _solarClient.RefreshAsync(latitude, longitude, _shutdown.Token);
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            if (!schedule.LocationResolved || schedule.Latitude != latitude || schedule.Longitude != longitude)
            {
                return;
            }

            schedule.TimeZone = result.TimeZone;
            schedule.SolarDays = result.SolarDays.ToList();
            schedule.FetchedAtUtc = result.FetchedAtUtc;
            _settingsWarning = null;
            _view?.SetLocationBusy(false, GetLocationStatus());
            SettingsChanged(immediate: true);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _context?.Logger.Error("Could not refresh cached sunrise/sunset times.", exception);
            _settingsWarning = $"Sunrise/sunset refresh failed; keeping the cached schedule. {exception.GetBaseException().Message}";
            _view?.SetLocationBusy(false, GetLocationStatus());
            UpdateStatus();
        }
        finally
        {
            _solarRefreshPending = false;
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs args) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                RefreshDisplays();
                ApplyAllDisplays();
                UpdateStatus();
            }
        });

    private void RefreshDisplays()
    {
        var displays = _context!.DisplayOverlays.GetDisplays();
        var connectedIds = displays.Select(display => display.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in _displays.Keys.Where(key => !connectedIds.Contains(key)).ToArray())
        {
            try
            {
                _displays[removed].Dispose();
            }
            catch (Exception exception)
            {
                _context.Logger.Error($"Could not restore disconnected display '{removed}'.", exception);
            }

            _displays.Remove(removed);
        }

        foreach (var display in displays)
        {
            if (_displays.ContainsKey(display.Id))
            {
                continue;
            }

            var settings = _settings.GetOrAddDisplay(display.Id);
            _displays.Add(display.Id, new DisplayRuntime(display, settings));
        }

        _settings.Normalize();
        _view?.RefreshSettings();
        UpdateTrayMenu();
    }

    private void ApplyAllDisplays()
    {
        foreach (var display in _displays.Values)
        {
            ApplyDisplay(display);
        }
    }

    private void ApplyDisplay(DisplayRuntime display)
    {
        if (!_settings.Enabled || !display.Settings.Enabled)
        {
            display.SetOverlay(null, keepAbovePopups: false, _context!, this);
            ApplyTemperature(display, null);
            return;
        }

        var brightness = display.Settings.Brightness;
        var temperature = display.Settings.TemperatureKelvin;
        if (_settings.Schedule.Enabled && display.Settings.ScheduleEnabled &&
            SolarSchedule.TryGetDayBlend(
                DateTimeOffset.UtcNow,
                _settings.Schedule.SolarDays,
                _settings.Schedule.TransitionMinutes,
                out var dayBlend))
        {
            brightness = SolarSchedule.BlendBrightness(
                display.Settings.NightBrightness,
                display.Settings.DayBrightness,
                dayBlend);
            temperature = SolarSchedule.BlendTemperature(
                display.Settings.NightTemperatureKelvin,
                display.Settings.DayTemperatureKelvin,
                dayBlend);
        }

        display.SetOverlay(
            brightness,
            _settings.KeepOverlayAbovePopups,
            _context!,
            this);
        ApplyTemperature(display, temperature);
    }

    private void ApplyTemperature(DisplayRuntime display, int? temperature)
    {
        if (display.TemperatureBackend is null && temperature is null)
        {
            return;
        }

        if (display.TemperatureBackend is null && display.TemperatureError is not null &&
            DateTimeOffset.UtcNow - display.LastTemperatureFailureUtc < TimeSpan.FromSeconds(30))
        {
            return;
        }

        try
        {
            display.TemperatureBackend ??= DisplayTemperatureController.Create(display.Display, _context!.CurrentOperatingSystem);
            display.TemperatureBackend.Apply(temperature);
            display.TemperatureError = null;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            try
            {
                display.TemperatureBackend?.Dispose();
            }
            catch (Exception restoreException)
            {
                _context?.Logger.Error($"Could not restore the display color ramp for '{display.Display.Id}'.", restoreException);
            }

            display.TemperatureBackend = null;
            display.LastTemperatureFailureUtc = DateTimeOffset.UtcNow;
            ReportDisplayError(display, "temperature", exception);
        }
    }

    private void ReportDisplayError(DisplayRuntime display, string effect, Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (effect == "overlay")
        {
            if (display.OverlayError == message)
            {
                return;
            }

            display.OverlayError = message;
        }
        else
        {
            if (display.TemperatureError == message)
            {
                return;
            }

            display.TemperatureError = message;
        }

        _context?.Logger.Error($"Could not apply {effect} on display '{display.Display.Id}': {message}", exception);
        UpdateStatus();
    }

    private string GetLocationStatus()
    {
        var schedule = _settings.Schedule;
        if (!schedule.LocationResolved)
        {
            return "Choose a city to load sunrise and sunset times.";
        }

        var count = schedule.SolarDays.Count;
        var age = schedule.FetchedAtUtc > 0
            ? DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(schedule.FetchedAtUtc)
            : TimeSpan.MaxValue;
        var refresh = age >= SolarCacheLifetime ? " · cache needs refresh" : string.Empty;
        return $"Using {schedule.ResolvedLocation} · {count} days cached{refresh}";
    }

    private void UpdateStatus()
    {
        if (_view is null)
        {
            return;
        }

        var errors = _displays.Values
            .SelectMany(display => new[] { display.OverlayError, display.TemperatureError })
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var scheduled = _settings.Schedule.Enabled && _settings.Displays.Any(display => display.Enabled && display.ScheduleEnabled);
        var mode = !_settings.Enabled
            ? "Dimmer is paused."
            : scheduled && _settings.Schedule.SolarDays.Count > 0
                ? "Sunrise/sunset schedule is active. Brightness and temperature share the same fade curve."
                : scheduled
                    ? "Schedule is enabled and waiting for a location and solar times."
                    : "Manual display settings are active.";

        if (errors.Length > 0)
        {
            mode += " Some display effects are unavailable: " + string.Join("; ", errors);
        }

        if (!string.IsNullOrWhiteSpace(_settingsWarning))
        {
            mode += " " + _settingsWarning;
        }

        _view.SetStatus(mode, GetLocationStatus());
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = _settings.Enabled ? "Ultimate Toolbox · Dimmer active" : "Ultimate Toolbox · Dimmer paused";
        }
    }

    private void TryInstallTrayIcon()
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        try
        {
            var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                ?? throw new InvalidOperationException("Could not find the Dimmer feature directory.");
            var iconPath = Path.Combine(assemblyDirectory, "dimmer.ico");
            if (!File.Exists(iconPath))
            {
                _context!.Logger.Info("The Dimmer tray icon asset was not found; settings remain available in the host window.");
                return;
            }

            _previousTrayIcons = TrayIcon.GetIcons(application);
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconPath),
                ToolTipText = "Ultimate Toolbox · Dimmer active",
                Menu = CreateTrayMenu()
            };
            _trayIcon.Clicked += (_, _) => ShowHostWindow();
            var icons = new TrayIcons();
            foreach (var existing in _previousTrayIcons ?? [])
            {
                icons.Add(existing);
            }

            icons.Add(_trayIcon);
            TrayIcon.SetIcons(application, icons);
        }
        catch (Exception exception)
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _context!.Logger.Error("Could not install the Dimmer tray icon.", exception);
            _settingsWarning = $"Tray integration is unavailable: {exception.GetBaseException().Message}";
        }
    }

    private NativeMenu CreateTrayMenu()
    {
        var menu = new NativeMenu();
        var open = new NativeMenuItem("Open settings");
        open.Click += (_, _) => ShowHostWindow();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());

        var enabled = new NativeMenuItem("Dimmer enabled")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.Enabled
        };
        enabled.Click += (_, _) => SetEnabled(!_settings.Enabled);
        menu.Items.Add(enabled);

        var keepAbove = new NativeMenuItem("Keep overlay above popups")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.KeepOverlayAbovePopups
        };
        keepAbove.Click += (_, _) => SetKeepAbovePopups(!_settings.KeepOverlayAbovePopups);
        menu.Items.Add(keepAbove);

        menu.Items.Add(new NativeMenuItemSeparator());
        foreach (var display in _displays.Values.OrderBy(display => display.Display.IsPrimary ? 0 : 1))
        {
            var submenu = new NativeMenu();
            foreach (var brightness in QuickBrightnessValues)
            {
                var entry = new NativeMenuItem($"Brightness {brightness}%");
                entry.Click += (_, _) => SetManualBrightness(display.Display.Id, brightness);
                submenu.Items.Add(entry);
            }

            submenu.Items.Add(new NativeMenuItemSeparator());
            foreach (var temperature in QuickTemperatureValues)
            {
                var label = temperature is null ? "No temperature" : $"{temperature} K";
                var entry = new NativeMenuItem(label);
                entry.Click += (_, _) => SetManualTemperature(display.Display.Id, temperature);
                submenu.Items.Add(entry);
            }

            menu.Items.Add(new NativeMenuItem(display.Display.Name ?? display.Display.Id) { Menu = submenu });
        }

        return menu;
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Menu = CreateTrayMenu();
        }
    }

    private static void ShowHostWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    private sealed class DisplayRuntime(FeatureDisplay display, DisplayDimmerSettings settings) : IDisposable
    {
        private SolidColorBrush? _overlayBrush;
        private int? _lastBrightness;
        private DateTimeOffset _lastOverlayFailureUtc;

        public FeatureDisplay Display { get; } = display;
        public DisplayDimmerSettings Settings { get; } = settings;
        public IDisplayOverlayHandle? Overlay { get; private set; }
        public IDisplayTemperatureBackend? TemperatureBackend { get; set; }
        public string? OverlayError { get; set; }
        public string? TemperatureError { get; set; }
        public DateTimeOffset LastTemperatureFailureUtc { get; set; }

        public void SetOverlay(int? brightness, bool keepAbovePopups, IFeatureContext context, DimmerFeature owner)
        {
            if (brightness is null || brightness >= 100)
            {
                Overlay?.Dispose();
                Overlay = null;
                _overlayBrush = null;
                _lastBrightness = brightness;
                OverlayError = null;
                return;
            }

            var normalizedBrightness = Math.Clamp(brightness.Value, 0, 100);
            if (Overlay is null && OverlayError is not null &&
                DateTimeOffset.UtcNow - _lastOverlayFailureUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }

            try
            {
                if (Overlay is null)
                {
                    var brush = new SolidColorBrush(Colors.Transparent);
                    var content = new Border { Background = brush };
                    Overlay = context.DisplayOverlays.CreateFullScreenOverlay(Display, content, topmost: true);
                    _overlayBrush = brush;
                    _lastBrightness = null;
                }

                if (_lastBrightness != normalizedBrightness && _overlayBrush is not null)
                {
                    var alpha = (byte)Math.Clamp(
                        Math.Round((100 - normalizedBrightness) * 2.55, MidpointRounding.AwayFromZero),
                        0,
                        240);
                    _overlayBrush.Color = Color.FromArgb(alpha, 0, 0, 0);
                    _lastBrightness = normalizedBrightness;
                }

                if (keepAbovePopups)
                {
                    Overlay.SetTopmost(topmost: true);
                }

                OverlayError = null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
            {
                Overlay?.Dispose();
                Overlay = null;
                _overlayBrush = null;
                _lastBrightness = null;
                _lastOverlayFailureUtc = DateTimeOffset.UtcNow;
                owner.ReportDisplayError(this, "overlay", exception);
            }
        }

        public void Dispose()
        {
            try
            {
                Overlay?.Dispose();
            }
            finally
            {
                Overlay = null;
                _overlayBrush = null;
                TemperatureBackend?.Dispose();
                TemperatureBackend = null;
            }
        }
    }
}
