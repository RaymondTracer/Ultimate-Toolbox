using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateToolbox.BlankScreen;
using UltimateToolbox.Contracts;

namespace UltimateToolbox.AutoBlankScreen.Feature;

public sealed class AutoBlankScreenFeature : IToolboxFeature
{
    private static readonly TimeSpan AudioScanInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromMinutes(5);

    private readonly object _settingsGate = new();
    private readonly Queue<string> _diagnostics = new();
    private readonly Dictionary<string, DateTimeOffset> _recentProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDisplayOverlayHandle> _overlays = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _cursorVisibilityLease;
    private IFeatureContext? _context;
    private AutoBlankScreenSettings _settings = new();
    private IIdleTimeProvider? _idleProvider;
    private IAudioSessionProvider? _audioProvider;
    private DispatcherTimer? _monitorTimer;
    private AutoBlankScreenView? _view;
    private Task<IReadOnlyList<AudioPlaybackSession>>? _audioScanTask;
    private AudioActivitySnapshot _audioStatus = AudioActivitySnapshot.Unavailable("Checking audio sessions…");
    private BlankScreenRuntimeState _runtimeState = new();
    private TimeSpan? _lastIdleTime;
    private DateTimeOffset _nextAudioScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private string? _lastErrorSignature;
    private bool _manualBlankRequested;
    private bool _disposed;
    private string? _statusMessage;

    public FeatureMetadata Metadata { get; } = new(
        "auto-blank-screen",
        "Auto Blank Screen",
        "Blank every connected display after an idle timeout, wake on input, and pause blanking for selected audio apps.",
        [FeatureOperatingSystem.Windows, FeatureOperatingSystem.Linux]);

    public Task StartAsync(IFeatureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dispatcher.UIThread.VerifyAccess();
        _context = context;

        _idleProvider = context.CurrentOperatingSystem switch
        {
            FeatureOperatingSystem.Windows when OperatingSystem.IsWindows() => new WindowsRawInputIdleProvider(),
            FeatureOperatingSystem.Linux when OperatingSystem.IsLinux() => new X11ScreenSaverIdleProvider(),
            _ => throw new PlatformNotSupportedException("Auto Blank Screen supports Windows and Linux only.")
        };
        _audioProvider = context.CurrentOperatingSystem switch
        {
            FeatureOperatingSystem.Windows when OperatingSystem.IsWindows() => new WindowsCoreAudioSessionProvider(),
            FeatureOperatingSystem.Linux when OperatingSystem.IsLinux() => new PulseAudioSessionProvider(),
            _ => throw new PlatformNotSupportedException("Audio-session inspection supports Windows and Linux only.")
        };

        AutoBlankScreenSettings loaded;
        try
        {
            loaded = context.Settings.Load<AutoBlankScreenSettings>() ?? new AutoBlankScreenSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Error("Auto Blank Screen settings could not be read; using defaults.", exception);
            loaded = new AutoBlankScreenSettings();
        }

        var normalized = loaded.Copy();
        lock (_settingsGate)
        {
            _settings = normalized;
        }

        if (!loaded.IsEquivalentTo(normalized))
        {
            context.Logger.Warning("Auto Blank Screen settings contained invalid values; normalized values were saved.");
            SaveSettings(normalized);
        }

        if (_idleProvider.Error is { } idleError)
        {
            AddDiagnostic(idleError);
        }
        else
        {
            AddDiagnostic($"Input activity source: {_idleProvider.Description}.");
        }

        context.DisplayOverlays.DisplaysChanged += OnDisplaysChanged;
        _monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _monitorTimer.Tick += (_, _) => OnMonitorTick();
        _monitorTimer.Start();
        StartAudioScan();
        OnMonitorTick();
        return Task.CompletedTask;
    }

    public Control CreateView(IFeatureContext context)
    {
        Dispatcher.UIThread.VerifyAccess();
        _view = new AutoBlankScreenView(this);
        RefreshView();
        return _view;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _monitorTimer?.Stop();
        if (_context is not null)
        {
            _context.DisplayOverlays.DisplaysChanged -= OnDisplaysChanged;
        }

        if (_audioScanTask is { } scanTask)
        {
            try
            {
                await scanTask;
            }
            catch
            {
                // A final scan failure should not prevent overlays from closing.
            }
        }

        CloseOverlays();
        _idleProvider?.Dispose();
        SaveSettings(GetSettingsSnapshot());
        _monitorTimer = null;
    }

    internal AutoBlankScreenSettings GetSettingsSnapshot()
    {
        lock (_settingsGate)
        {
            return _settings.Copy();
        }
    }

    internal AutoBlankScreenViewSnapshot GetViewSnapshot()
    {
        var settings = GetSettingsSnapshot();
        var idleProvider = _idleProvider;
        var idleTime = _lastIdleTime;
        var audioStatus = _audioStatus;
        var error = JoinNonEmpty(idleProvider?.Error, audioStatus.IsAvailable ? null : audioStatus.Error);
        var status = _statusMessage ?? (idleProvider is null
            ? "Waiting for the input monitor to start."
            : $"Monitoring with {_idleProvider!.Description}.");

        return new AutoBlankScreenViewSnapshot(
            settings,
            idleTime,
            idleProvider?.Description ?? "Input monitor unavailable",
            audioStatus,
            GetRecentProcessNames(),
            _diagnostics.ToArray(),
            status,
            error,
            _runtimeState.OverlayVisible);
    }

    internal void SetTimeout(int hours, int minutes)
    {
        UpdateSettings(settings =>
        {
            settings.TimeoutHours = Math.Clamp(hours, 0, 999);
            settings.TimeoutMinutes = Math.Clamp(minutes, 0, 59);
        });
        AddDiagnostic($"Idle timeout set to {GetSettingsSnapshot().GetTimeout():g}.");
        RefreshView();
    }

    internal void SetMonitorAllAudio(bool enabled)
    {
        UpdateSettings(settings =>
        {
            if (enabled)
            {
                settings.MonitoredAudioProcesses.Clear();
            }
            else if (settings.MonitoredAudioProcesses.Count == 0)
            {
                settings.MonitoredAudioProcesses = BlankScreenPolicy.DefaultMonitoredAudioProcesses.ToList();
            }
        });
        RefreshAudioStatus();
        AddDiagnostic(enabled ? "Monitoring every output audio process." : "Monitoring the selected audio processes.");
        RefreshView();
    }

    internal bool TryAddAudioProcess(string? processName, out string message)
    {
        var normalized = BlankScreenPolicy.NormalizeAudioProcessName(processName);
        if (normalized is null)
        {
            message = "Enter an audio process name or matching app label.";
            return false;
        }

        lock (_settingsGate)
        {
            if (_settings.MonitoredAudioProcesses.Count == 0)
            {
                message = "All audio processes are already monitored. Turn off ‘Monitor all audio apps’ to add a selected process.";
                return false;
            }

            if (_settings.MonitoredAudioProcesses.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                message = $"{normalized} is already monitored.";
                return false;
            }

            _settings.MonitoredAudioProcesses.Add(normalized);
        }

        message = $"Added {normalized}.";
        PersistAndRefresh();
        return true;
    }

    internal void RemoveAudioProcess(string processName)
    {
        lock (_settingsGate)
        {
            _settings.MonitoredAudioProcesses.RemoveAll(name =>
                string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));
        }

        PersistAndRefresh();
    }

    internal void RequestManualBlank()
    {
        _manualBlankRequested = true;
        _nextAudioScanAt = DateTimeOffset.MinValue;
        OnMonitorTick();
    }

    private void OnMonitorTick()
    {
        if (_disposed || _context is null)
        {
            return;
        }

        ConsumeAudioScan();
        if (_manualBlankRequested && !_audioStatus.IsAvailable && _audioScanTask is null)
        {
            StartAudioScan();
        }

        if (_manualBlankRequested && _audioScanTask is not null)
        {
            _statusMessage = "Checking audio sessions before the manual blank request.";
            RefreshView();
            return;
        }

        if (_audioScanTask is null && DateTimeOffset.UtcNow >= _nextAudioScanAt)
        {
            StartAudioScan();
        }

        var now = DateTimeOffset.UtcNow;
        _lastIdleTime = _idleProvider?.GetIdleTime();
        var settings = GetSettingsSnapshot();
        var decision = BlankScreenStateMachine.Evaluate(
            _runtimeState,
            _lastIdleTime,
            _audioStatus.IsAvailable,
            _audioStatus.HasAudio,
            _manualBlankRequested,
            now,
            settings.GetTimeout());
        _manualBlankRequested = false;
        _runtimeState = decision.NextState;
        ApplyDecision(decision);
        UpdateStatus(decision, settings);
        RefreshView();
    }

    private void StartAudioScan()
    {
        if (_disposed || _audioProvider is null)
        {
            return;
        }

        var provider = _audioProvider;
        _nextAudioScanAt = DateTimeOffset.UtcNow + AudioScanInterval;
        _audioScanTask = Task.Run(provider.ReadSessions);
    }

    private void ConsumeAudioScan()
    {
        if (_audioScanTask is not { IsCompleted: true } completedTask)
        {
            return;
        }

        _audioScanTask = null;
        try
        {
            var sessions = completedTask.GetAwaiter().GetResult();
            UpdateRecentlyDetectedProcesses(sessions);
            _audioStatus = AudioActivityPolicy.Evaluate(
                sessions,
                GetSettingsSnapshot().MonitoredAudioProcesses,
                GetRecentProcessNames());
            _lastErrorSignature = null;
        }
        catch (Exception exception)
        {
            _audioStatus = AudioActivitySnapshot.Unavailable(exception.GetBaseException().Message);
            LogError(exception);
        }
    }

    private void ApplyDecision(BlankScreenDecision decision)
    {
        switch (decision.Action)
        {
            case BlankScreenAction.ShowAutomatic:
            case BlankScreenAction.ShowManual:
                ShowOverlays();
                AddDiagnostic(decision.Action == BlankScreenAction.ShowManual
                    ? "Showing a manual blank-screen overlay."
                    : "Showing blank-screen overlays after the idle timeout.");
                break;

            case BlankScreenAction.HideForAudio:
                CloseOverlays();
                AddDiagnostic($"Waking the displays because audio started in {(_audioStatus.ProcessName ?? "a monitored app")}.");
                break;

            case BlankScreenAction.HideForActivity:
                CloseOverlays();
                AddDiagnostic("Waking the displays because user input resumed.");
                break;

            case BlankScreenAction.HideForUnavailableActivity:
                CloseOverlays();
                AddDiagnostic("Waking the displays because input monitoring became unavailable.");
                break;

            case BlankScreenAction.HideForUnavailableAudio:
                CloseOverlays();
                AddDiagnostic("Waking the displays because audio monitoring became unavailable.");
                break;

            case BlankScreenAction.ManualBlockedByAudio:
                AddDiagnostic($"Manual blanking was blocked because audio is active in {(_audioStatus.ProcessName ?? "a monitored app")}.");
                break;
        }
    }

    private void ShowOverlays()
    {
        if (_context is null || _overlays.Count > 0)
        {
            return;
        }

        var created = new List<IDisplayOverlayHandle>();
        try
        {
            var displays = _context.DisplayOverlays.GetDisplays();
            if (displays.Count == 0)
            {
                throw new InvalidOperationException("No connected displays are available for the blank-screen overlay.");
            }

            foreach (var display in displays)
            {
                var content = new Border { Background = Brushes.Black };
                var overlay = _context.DisplayOverlays.CreateFullScreenOverlay(display, content, topmost: true);
                created.Add(overlay);
            }

            foreach (var overlay in created)
            {
                _overlays[overlay.Display.Id] = overlay;
            }

            try
            {
                _cursorVisibilityLease = _context.DisplayOverlays.HideCursor();
            }
            catch (Exception cursorException)
            {
                AddDiagnostic($"Blank-screen overlays are active, but the pointer could not be hidden: {cursorException.GetBaseException().Message}");
                LogError(cursorException);
            }

            _statusMessage = $"Screen blanked across {created.Count} display(s).";
        }
        catch (Exception exception)
        {
            foreach (var overlay in created)
            {
                overlay.Dispose();
            }

            _runtimeState.OverlayVisible = false;
            _runtimeState.OverlayHoldUntil = null;
            _statusMessage = "The blank-screen overlay could not be created.";
            LogError(exception);
        }
    }

    private void CloseOverlays()
    {
        foreach (var overlay in _overlays.Values.ToArray())
        {
            try
            {
                overlay.Dispose();
            }
            catch (Exception exception)
            {
                LogError(exception);
            }
        }

        _overlays.Clear();
        try
        {
            _cursorVisibilityLease?.Dispose();
        }
        catch (Exception exception)
        {
            LogError(exception);
        }
        finally
        {
            _cursorVisibilityLease = null;
        }

        _runtimeState.OverlayVisible = false;
        _runtimeState.OverlayHoldUntil = null;
    }

    private void OnDisplaysChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!_runtimeState.OverlayVisible)
        {
            return;
        }

        CloseOverlays();
        _runtimeState.OverlayVisible = true;
        ShowOverlays();
    }

    private void UpdateStatus(BlankScreenDecision decision, AutoBlankScreenSettings settings)
    {
        if (_runtimeState.OverlayVisible)
        {
            _statusMessage = $"Screen blanked · wake threshold is {BlankScreenStateMachine.OverlayDismissThreshold.TotalMilliseconds:0} ms of idle after input.";
            return;
        }

        if (_audioStatus.HasAudio)
        {
            _statusMessage = _audioStatus.HasMeasuredLevel
                ? $"Blanking paused while {_audioStatus.ProcessName ?? "audio"} is playing at {_audioStatus.PeakLevel * 100:0}% peak."
                : $"Blanking paused while an output stream from {_audioStatus.ProcessName ?? "audio"} is active; live peak metering is unavailable on Linux.";
            return;
        }

        if (_lastIdleTime is null)
        {
            _statusMessage = _idleProvider?.Error ?? "Input activity is unavailable on this desktop session.";
            return;
        }

        if (!_audioStatus.IsAvailable)
        {
            _statusMessage = "Automatic blanking is paused because audio monitoring is unavailable.";
            return;
        }

        if (decision.Action == BlankScreenAction.ManualBlockedByAudio)
        {
            _statusMessage = $"Manual blanking is blocked while {_audioStatus.ProcessName ?? "a monitored audio app"} is playing.";
            return;
        }

        var remaining = settings.GetTimeout() - decision.EffectiveIdle;
        _statusMessage = remaining <= TimeSpan.Zero
            ? "Waiting for an available audio session scan."
            : $"Monitoring · blanking in {FormatDuration(remaining)}.";
    }

    private void UpdateRecentlyDetectedProcesses(IReadOnlyList<AudioPlaybackSession> sessions)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var session in sessions)
        {
            var name = BlankScreenPolicy.NormalizeAudioProcessName(session.ProcessName)
                ?? BlankScreenPolicy.NormalizeAudioProcessName(session.DisplayName);
            if (name is not null)
            {
                _recentProcesses[name] = now;
            }
        }

        foreach (var staleName in _recentProcesses
                     .OrderByDescending(entry => entry.Value)
                     .Skip(20)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _recentProcesses.Remove(staleName);
        }
    }

    private IReadOnlyList<string> GetRecentProcessNames() =>
        _recentProcesses.OrderByDescending(entry => entry.Value).Select(entry => entry.Key).ToArray();

    private void PersistAndRefresh()
    {
        var snapshot = GetSettingsSnapshot();
        SaveSettings(snapshot);
        RefreshAudioStatus();
        RefreshView();
    }

    private void UpdateSettings(Action<AutoBlankScreenSettings> update)
    {
        lock (_settingsGate)
        {
            update(_settings);
            _settings = _settings.Copy();
        }

        SaveSettings(GetSettingsSnapshot());
    }

    private void SaveSettings(AutoBlankScreenSettings settings)
    {
        try
        {
            _context?.Settings.Save(settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _context?.Logger.Error("Auto Blank Screen settings could not be saved.", exception);
            _statusMessage = "Settings could not be saved.";
        }
    }

    private void RefreshAudioStatus()
    {
        if (_audioProvider is null || _audioScanTask is not null)
        {
            return;
        }

        _audioScanTask = Task.Run(_audioProvider.ReadSessions);
        _nextAudioScanAt = DateTimeOffset.UtcNow + AudioScanInterval;
    }

    private void LogError(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        if (string.Equals(_lastErrorSignature, message, StringComparison.Ordinal))
        {
            return;
        }

        _lastErrorSignature = message;
        AddDiagnostic(message);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastErrorLogAt >= ErrorLogThrottle)
        {
            _lastErrorLogAt = now;
            _context?.Logger.Error("Auto Blank Screen could not complete an operation.", exception);
        }
    }

    private void AddDiagnostic(string message)
    {
        var formatted = $"{DateTime.Now:HH:mm:ss} {message}";
        if (_diagnostics.Count == 3)
        {
            _diagnostics.Dequeue();
        }

        _diagnostics.Enqueue(formatted);
        _context?.Logger.Info(message);
    }

    private void RefreshView() => _view?.Refresh(GetViewSnapshot());

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes} min {duration.Seconds:00} s"
                : $"{Math.Ceiling(duration.TotalSeconds):0} s";
    }

    private static string? JoinNonEmpty(params string?[] values)
    {
        var nonEmpty = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return nonEmpty.Length == 0 ? null : string.Join(" ", nonEmpty);
    }
}

internal sealed record AutoBlankScreenViewSnapshot(
    AutoBlankScreenSettings Settings,
    TimeSpan? IdleTime,
    string IdleSourceDescription,
    AudioActivitySnapshot AudioStatus,
    IReadOnlyList<string> RecentlyDetectedProcesses,
    IReadOnlyList<string> Diagnostics,
    string StatusMessage,
    string? ErrorMessage,
    bool OverlayActive);
