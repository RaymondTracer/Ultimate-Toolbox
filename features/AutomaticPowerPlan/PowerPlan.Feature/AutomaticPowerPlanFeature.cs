using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using UltimateToolbox.Contracts;
using UltimateToolbox.PowerPlan;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.PowerPlan.Feature;

public sealed class AutomaticPowerPlanFeature : IToolboxFeature
{
    private static readonly TimeSpan ProfileRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorLogThrottle = TimeSpan.FromMinutes(5);

    private readonly object _settingsGate = new();
    private readonly object _statusGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private IFeatureContext? _context;
    private AutomaticPowerPlanSettings _settings = new();
    private IProcessPowerProfileBackend? _backend;
    private ProcessActivityScanner? _activityScanner;
    private PowerPlanView? _view;
    private CancellationTokenRegistration _cancellationRegistration;
    private Task _monitorTask = Task.CompletedTask;
    private IReadOnlyList<PowerProfile> _profiles = [];
    private string? _currentProfileId;
    private string? _statusMessage;
    private string? _errorMessage;
    private string? _profileError;
    private string? _activityDetails;
    private string? _ignoredActivityDetails;
    private int? _graceSecondsRemaining;
    private DateTimeOffset _nextProfileRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastErrorLogAt = DateTimeOffset.MinValue;
    private string? _lastWarningSignature;
    private string? _lastIgnoredSignature;
    private bool _disposed;

    public FeatureMetadata Metadata { get; } = new(
        "automatic-power-plan",
        "Automatic Power Plan",
        "Temporarily switch power profiles while selected apps or build tools are active, then restore the profile that was active before.",
        [FeatureOperatingSystem.Windows, FeatureOperatingSystem.Linux]);

    public Task StartAsync(IFeatureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _backend = context.CurrentOperatingSystem switch
        {
            FeatureOperatingSystem.Windows when OperatingSystem.IsWindows() => new WindowsPowerProfileBackend(),
            FeatureOperatingSystem.Linux when OperatingSystem.IsLinux() => new LinuxPowerProfileBackend(),
            _ => throw new PlatformNotSupportedException("Automatic Power Plan supports Windows and Linux only.")
        };
        IProcessCommandLineProvider commandLineProvider = context.CurrentOperatingSystem switch
        {
            FeatureOperatingSystem.Windows when OperatingSystem.IsWindows() => new WindowsProcessCommandLineProvider(),
            FeatureOperatingSystem.Linux when OperatingSystem.IsLinux() => new LinuxProcessCommandLineProvider(),
            _ => throw new PlatformNotSupportedException("Process command-line inspection supports Windows and Linux only.")
        };
        _activityScanner = new ProcessActivityScanner(commandLineProvider);

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(_shutdown.Cancel);
        }

        AutomaticPowerPlanSettings loaded;
        try
        {
            loaded = context.Settings.Load<AutomaticPowerPlanSettings>() ?? new AutomaticPowerPlanSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Error("Automatic Power Plan settings could not be read; using the defaults.", exception);
            loaded = new AutomaticPowerPlanSettings();
        }

        var normalized = loaded.Copy();
        lock (_settingsGate)
        {
            _settings = normalized;
        }

        if (!loaded.IsEquivalentTo(normalized))
        {
            context.Logger.Warning("Automatic Power Plan settings contained invalid values; normalized values were saved.");
            SaveSettings(normalized);
        }

        _monitorTask = Task.Run(() => MonitorAsync(_shutdown.Token));
        return Task.CompletedTask;
    }

    public Control CreateView(IFeatureContext context)
    {
        _view = new PowerPlanView(this);
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
        _cancellationRegistration.Dispose();
        _shutdown.Cancel();
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            RestoreOwnedProfileNow();
        }
        catch (Exception exception)
        {
            _context?.Logger.Error("Could not restore the saved power profile while stopping Automatic Power Plan.", exception);
        }

        SaveSettings(GetSettingsSnapshot());
        _shutdown.Dispose();
    }

    internal AutomaticPowerPlanSettings GetSettingsSnapshot()
    {
        lock (_settingsGate)
        {
            return _settings.Copy();
        }
    }

    internal PowerPlanViewSnapshot GetViewSnapshot()
    {
        lock (_statusGate)
        {
            return new PowerPlanViewSnapshot(
                _profiles.ToArray(),
                _currentProfileId,
                _statusMessage,
                _errorMessage ?? _profileError,
                _activityDetails,
                _ignoredActivityDetails,
                _graceSecondsRemaining);
        }
    }

    internal void SetEnabled(bool enabled) => UpdateSettings(settings => settings.Enabled = enabled);

    internal void SetPollingInterval(int seconds) =>
        UpdateSettings(settings => settings.PollingIntervalSeconds = Math.Clamp(seconds, 1, 3600));

    internal void SetGracePeriod(int seconds) =>
        UpdateSettings(settings => settings.GracePeriodSeconds = Math.Clamp(seconds, 0, 86400));

    internal void SetTargetProfile(string? profileId)
    {
        SetProfileError(null);
        UpdateSettings(settings => settings.TargetProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim());
    }

    internal void SetIgnoreReusableMsBuildWorkers(bool enabled) =>
        UpdateSettings(settings => settings.IgnoreReusableMsBuildWorkerNodes = enabled);

    internal bool TryAddWatchedProcessName(string? processName, out string message)
    {
        var normalized = ProcessPriorityPolicy.NormalizeProcessName(processName);
        if (normalized is null)
        {
            message = "Enter an exact process name without wildcards.";
            return false;
        }

        if (ProcessPriorityPolicy.Matches(normalized, "dotnet"))
        {
            message = "Generic dotnet.exe is filtered automatically. Enable one of its build verbs instead.";
            return false;
        }

        lock (_settingsGate)
        {
            if (_settings.WatchedProcessNames.Any(name => ProcessPriorityPolicy.Matches(name, normalized)))
            {
                message = $"A rule for {normalized} already exists.";
                return false;
            }

            _settings.WatchedProcessNames.Add(normalized);
        }

        message = $"Added {normalized}.";
        PersistAndRefresh();
        return true;
    }

    internal void RemoveWatchedProcessName(string processName) =>
        UpdateSettings(settings => settings.WatchedProcessNames.RemoveAll(name =>
            ProcessPriorityPolicy.Matches(name, processName)));

    internal void SetDotNetVerbEnabled(string verb, bool enabled)
    {
        if (!WatchedProcessPolicy.DefaultDotNetVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        UpdateSettings(settings =>
        {
            settings.WatchedDotNetVerbs = enabled
                ? settings.WatchedDotNetVerbs.Append(verb).ToList()
                : settings.WatchedDotNetVerbs.Where(candidate =>
                    !string.Equals(candidate, verb, StringComparison.OrdinalIgnoreCase)).ToList();
        });
    }

    private void UpdateSettings(Action<AutomaticPowerPlanSettings> update)
    {
        lock (_settingsGate)
        {
            update(_settings);
            _settings = _settings.Copy();
        }

        PersistAndRefresh();
    }

    private void PersistAndRefresh()
    {
        var snapshot = GetSettingsSnapshot();
        SaveSettings(snapshot);
        RefreshView(snapshot);
    }

    private void SaveSettings(AutomaticPowerPlanSettings settings)
    {
        try
        {
            _context?.Settings.Save(settings);
        }
        catch (Exception exception)
        {
            _context?.Logger.Error("Could not save Automatic Power Plan settings.", exception);
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = GetSettingsSnapshot();
            try
            {
                RunMonitoringCycle(settings);
            }
            catch (Exception exception)
            {
                SetError(exception.GetBaseException().Message);
                var now = DateTimeOffset.UtcNow;
                if (now - _lastErrorLogAt >= ErrorLogThrottle)
                {
                    _lastErrorLogAt = now;
                    _context?.Logger.Error("Automatic Power Plan could not complete its monitoring cycle.", exception);
                }
            }

            RefreshView(settings);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollingIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void RunMonitoringCycle(AutomaticPowerPlanSettings settings)
    {
        var backend = _backend ?? throw new InvalidOperationException("The power-profile backend is not initialized.");
        RefreshAvailableProfiles(settings);
        settings = GetSettingsSnapshot();

        if (!settings.Enabled)
        {
            RestoreOwnedProfileNow();
            SetStatus("Automatic switching is paused.", null, null, null);
            return;
        }

        var scanner = _activityScanner ?? throw new InvalidOperationException("The process activity scanner is not initialized.");
        var activity = scanner.Scan(settings);
        LogActivityDiagnostics(activity);
        SetActivityDetails(activity.Details, activity.IgnoredDetails);

        var targetProfileId = settings.TargetProfileId;
        var profileAvailable = targetProfileId is not null && AvailableProfiles().Any(profile =>
            string.Equals(profile.Id, targetProfileId, StringComparison.OrdinalIgnoreCase));

        if (activity.IsActive && !profileAvailable)
        {
            if (settings.RuntimeState.SwitchInProgress)
            {
                var nextState = settings.RuntimeState.Copy();
                nextState.LastActivityAt = DateTimeOffset.UtcNow;
                nextState.GracePeriodActive = false;
                SetRuntimeState(nextState, persist: false);
            }

            SetError(targetProfileId is null
                ? "Choose an available target profile to enable automatic switching."
                : $"The configured target profile '{targetProfileId}' is unavailable.");
            SetStatus(
                $"Watched activity detected: {activity.Details}.",
                targetProfileId is null
                    ? "Choose an available target profile to enable automatic switching."
                    : $"The configured target profile '{targetProfileId}' is unavailable.",
                null,
                null);
            return;
        }

        if (activity.IsActive || settings.RuntimeState.SwitchInProgress)
        {
            var currentProfileId = backend.GetCurrentProfileId();
            SetCurrentProfile(currentProfileId);
            var decision = PowerPlanStateMachine.Evaluate(
                settings.RuntimeState,
                activity.IsActive,
                currentProfileId,
                targetProfileId,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(settings.GracePeriodSeconds));
            ApplyDecision(decision);
            UpdateDecisionStatus(decision, settings.GracePeriodSeconds, activity);
        }
        else
        {
            var decision = PowerPlanStateMachine.Evaluate(
                settings.RuntimeState,
                false,
                null,
                targetProfileId,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(settings.GracePeriodSeconds));
            ApplyDecision(decision);
            SetStatus(
                "Idle · no watched processes are active.",
                activity.Warnings.Count == 0 ? null : string.Join(" ", activity.Warnings),
                null,
                null);
        }
    }

    private void RefreshAvailableProfiles(AutomaticPowerPlanSettings settings)
    {
        if (DateTimeOffset.UtcNow < _nextProfileRefreshAt)
        {
            return;
        }

        var backend = _backend ?? throw new InvalidOperationException("The power-profile backend is not initialized.");
        try
        {
            var profiles = backend.GetAvailableProfiles();
            SetProfiles(profiles);
            _nextProfileRefreshAt = DateTimeOffset.UtcNow + ProfileRefreshInterval;
            SetProfileError(null);

            if (settings.TargetProfileId is null)
            {
                try
                {
                    var suggested = backend.SuggestPerformanceProfileId(profiles);
                    if (suggested is not null && profiles.Any(profile =>
                        string.Equals(profile.Id, suggested, StringComparison.OrdinalIgnoreCase)))
                    {
                        UpdateSettings(current => current.TargetProfileId = suggested);
                    }
                    else
                    {
                        SetProfileError("No performance profile is available. Choose an available profile as the temporary target.");
                    }
                }
                catch (Exception exception)
                {
                    SetProfileError(exception.GetBaseException().Message);
                }
            }
        }
        catch (Exception exception)
        {
            _nextProfileRefreshAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            SetProfileError(exception.GetBaseException().Message);
        }
    }

    private void ApplyDecision(PowerPlanDecision decision)
    {
        var backend = _backend ?? throw new InvalidOperationException("The power-profile backend is not initialized.");
        switch (decision.Action)
        {
            case PowerPlanAction.Activated:
                SetRuntimeState(decision.NextState, persist: true);
                backend.SetActiveProfile(decision.RequestedProfileId!);
                SetCurrentProfile(decision.RequestedProfileId);
                _context?.Logger.Info($"Watched activity detected; switched to profile {decision.RequestedProfileId}. Saved {decision.NextState.RestoreProfileId} for restoration.");
                break;

            case PowerPlanAction.Reapplied:
                SetRuntimeState(decision.NextState, persist: false);
                backend.SetActiveProfile(decision.RequestedProfileId!);
                SetCurrentProfile(decision.RequestedProfileId);
                _context?.Logger.Warning($"Watched activity remains active; reapplied profile {decision.RequestedProfileId}.");
                break;

            case PowerPlanAction.Restored:
                backend.SetActiveProfile(decision.RequestedProfileId!);
                SetRuntimeState(decision.NextState, persist: true);
                SetCurrentProfile(decision.RequestedProfileId);
                _context?.Logger.Info($"Restored saved power profile {decision.RequestedProfileId}.");
                break;

            case PowerPlanAction.GraceStarted:
                SetRuntimeState(decision.NextState, persist: false);
                _context?.Logger.Info($"Watched activity stopped. Keeping the temporary profile for the configured grace period.");
                break;

            case PowerPlanAction.ActivityResumed:
                SetRuntimeState(decision.NextState, persist: false);
                _context?.Logger.Info("Watched activity returned during the grace period; cancelled restoration.");
                break;

            case PowerPlanAction.Active:
            case PowerPlanAction.WaitingForGrace:
                SetRuntimeState(decision.NextState, persist: false);
                break;

            case PowerPlanAction.Idle:
            case PowerPlanAction.BaselineUnavailable:
                SetRuntimeState(decision.NextState, persist: false);
                break;
        }
    }

    private void UpdateDecisionStatus(
        PowerPlanDecision decision,
        int gracePeriodSeconds,
        ProcessActivitySnapshot activity)
    {
        var error = activity.Warnings.Count == 0 ? null : string.Join(" ", activity.Warnings);
        switch (decision.Action)
        {
            case PowerPlanAction.Activated:
                SetStatus($"Switched to the selected performance profile · {activity.Details}.", error, null, null);
                break;
            case PowerPlanAction.Reapplied:
                SetStatus($"Reapplied the selected profile · {activity.Details}.", error, null, null);
                break;
            case PowerPlanAction.GraceStarted:
            case PowerPlanAction.WaitingForGrace:
                var state = decision.NextState;
                var elapsed = state.LastActivityAt is { } last ? (DateTimeOffset.UtcNow - last).TotalSeconds : 0;
                var remaining = Math.Max(0, (int)Math.Ceiling(gracePeriodSeconds - elapsed));
                SetStatus($"No watched activity · restoring the saved profile in {remaining} second(s).", error, remaining, null);
                break;
            case PowerPlanAction.ActivityResumed:
            case PowerPlanAction.Active:
                SetStatus($"Watched activity is active · {activity.Details}.", error, null, null);
                break;
            case PowerPlanAction.Restored:
                SetStatus("Restored the profile that was active before watched activity.", error, null, null);
                break;
            case PowerPlanAction.BaselineUnavailable:
                SetStatus("Could not read the current profile, so no change was made.", decision.Detail, null, null);
                break;
            default:
                SetStatus("Idle · no watched processes are active.", error, null, null);
                break;
        }
    }

    private void RestoreOwnedProfileNow()
    {
        var settings = GetSettingsSnapshot();
        var decision = PowerPlanStateMachine.RestoreNow(settings.RuntimeState);
        if (decision.Action != PowerPlanAction.Restored)
        {
            return;
        }

        var backend = _backend ?? throw new InvalidOperationException("The power-profile backend is not initialized.");
        backend.SetActiveProfile(decision.RequestedProfileId!);
        SetRuntimeState(decision.NextState, persist: true);
        SetCurrentProfile(decision.RequestedProfileId);
        _context?.Logger.Info($"Restored saved power profile {decision.RequestedProfileId} after switching was paused or stopped.");
    }

    private void SetRuntimeState(PowerPlanRuntimeState state, bool persist)
    {
        AutomaticPowerPlanSettings snapshot;
        lock (_settingsGate)
        {
            _settings.RuntimeState = state.Copy();
            snapshot = _settings.Copy();
        }

        if (persist)
        {
            SaveSettings(snapshot);
        }
    }

    private IReadOnlyList<PowerProfile> AvailableProfiles()
    {
        lock (_statusGate)
        {
            return _profiles.ToArray();
        }
    }

    private void LogActivityDiagnostics(ProcessActivitySnapshot activity)
    {
        var warningSignature = string.Join("\n", activity.Warnings);
        if (!string.Equals(warningSignature, _lastWarningSignature, StringComparison.Ordinal))
        {
            foreach (var warning in activity.Warnings)
            {
                _context?.Logger.Warning(warning);
            }

            _lastWarningSignature = warningSignature;
        }

        if (!string.Equals(activity.IgnoredDetails, _lastIgnoredSignature, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(activity.IgnoredDetails))
            {
                _context?.Logger.Info(activity.IgnoredDetails);
            }

            _lastIgnoredSignature = activity.IgnoredDetails;
        }
    }

    private void SetProfiles(IReadOnlyList<PowerProfile> profiles)
    {
        lock (_statusGate)
        {
            _profiles = profiles.ToArray();
        }
    }

    private void SetCurrentProfile(string? profileId)
    {
        lock (_statusGate)
        {
            _currentProfileId = profileId;
        }
    }

    private void SetActivityDetails(string? activity, string? ignored)
    {
        lock (_statusGate)
        {
            _activityDetails = activity;
            _ignoredActivityDetails = ignored;
        }
    }

    private void SetStatus(string? status, string? error, int? graceRemaining, string? activity)
    {
        lock (_statusGate)
        {
            _statusMessage = status;
            _errorMessage = error;
            _graceSecondsRemaining = graceRemaining;
            if (activity is not null)
            {
                _activityDetails = activity;
            }
        }
    }

    private void SetError(string? error)
    {
        lock (_statusGate)
        {
            _errorMessage = error;
        }
    }

    private void SetProfileError(string? error)
    {
        lock (_statusGate)
        {
            _profileError = error;
        }
    }

    private void RefreshView(AutomaticPowerPlanSettings? settings = null)
    {
        var settingsSnapshot = settings ?? GetSettingsSnapshot();
        var viewSnapshot = GetViewSnapshot();
        Dispatcher.UIThread.Post(() => _view?.Refresh(settingsSnapshot, viewSnapshot));
    }
}
