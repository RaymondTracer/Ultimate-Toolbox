using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using UltimateToolbox.Contracts;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

public sealed class ProcessPriorityFeature : IToolboxFeature
{
    private static readonly TimeSpan LogThrottle = TimeSpan.FromMinutes(5);

    private readonly object _settingsGate = new();
    private readonly object _statusGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<(string Rule, int ProcessId, PriorityResultKind Kind), DateTimeOffset> _lastLogs = [];
    private readonly Dictionary<string, ProcessRuleStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private IFeatureContext? _context;
    private ProcessPrioritySettings _settings = new();
    private IProcessPriorityBackend? _backend;
    private ProcessPriorityView? _view;
    private CancellationTokenRegistration _cancellationRegistration;
    private Task _monitorTask = Task.CompletedTask;
    private DateTimeOffset _lastScanFailureLog = DateTimeOffset.MinValue;
    private string? _scanError;
    private bool _disposed;

    public FeatureMetadata Metadata { get; } = new(
        "process-priority",
        "Process Priority",
        "Apply configurable Windows priority classes or Linux nice values to selected processes.",
        [FeatureOperatingSystem.Windows, FeatureOperatingSystem.Linux]);

    public Task StartAsync(IFeatureContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _backend = context.CurrentOperatingSystem switch
        {
            FeatureOperatingSystem.Windows when OperatingSystem.IsWindows() => new WindowsProcessPriorityBackend(),
            FeatureOperatingSystem.Linux when OperatingSystem.IsLinux() => new LinuxNicePriorityBackend(),
            _ => throw new PlatformNotSupportedException("Process Priority supports Windows and Linux only.")
        };

        if (cancellationToken.CanBeCanceled)
        {
            _cancellationRegistration = cancellationToken.Register(_shutdown.Cancel);
        }

        ProcessPrioritySettings loaded;
        try
        {
            loaded = context.Settings.Load<ProcessPrioritySettings>() ?? new ProcessPrioritySettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            context.Logger.Error("Process Priority settings could not be read; using empty defaults.", exception);
            loaded = new ProcessPrioritySettings();
        }
        ProcessPrioritySettings normalized;
        lock (_settingsGate)
        {
            _settings = loaded.Copy();
            normalized = _settings.Copy();
        }

        if (!SettingsMatch(loaded, normalized))
        {
            context.Logger.Warning("Process Priority settings contained invalid or duplicate rules; normalized values were saved.");
            SaveSettings(normalized);
        }

        _monitorTask = Task.Run(() => MonitorAsync(_shutdown.Token));
        return Task.CompletedTask;
    }

    public Control CreateView(IFeatureContext context)
    {
        _view = new ProcessPriorityView(this);
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

        SaveSettings(GetSettingsSnapshot());
        _shutdown.Dispose();
    }

    internal ProcessPrioritySettings GetSettingsSnapshot()
    {
        lock (_settingsGate)
        {
            return _settings.Copy();
        }
    }

    internal IReadOnlyDictionary<string, ProcessRuleStatus> GetStatusesSnapshot()
    {
        lock (_statusGate)
        {
            return new Dictionary<string, ProcessRuleStatus>(_statuses, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal bool TryAddRule(string? processName, out string message)
    {
        var normalized = ProcessPriorityPolicy.NormalizeProcessName(processName);
        if (normalized is null)
        {
            message = "Enter a process name without wildcards.";
            return false;
        }

        lock (_settingsGate)
        {
            if (_settings.Rules.Any(rule => ProcessPriorityPolicy.Matches(rule.ProcessName, normalized)))
            {
                message = $"A rule for {normalized} already exists.";
                return false;
            }

            _settings.Rules.Add(new ProcessPriorityRule { ProcessName = normalized });
        }

        message = $"Added {normalized}.";
        PersistAndRefresh();
        return true;
    }

    internal void RemoveRule(string processName)
    {
        lock (_settingsGate)
        {
            var rule = _settings.Rules.FirstOrDefault(candidate =>
                ProcessPriorityPolicy.Matches(candidate.ProcessName, processName));
            if (rule is null)
            {
                return;
            }

            _settings.Rules.Remove(rule);
        }

        PersistAndRefresh();
    }

    internal void SetRuleEnabled(string processName, bool enabled) =>
        ChangeRule(processName, rule => rule.Enabled = enabled);

    internal void SetWindowsPriority(string processName, WindowsPriorityLevel priority) =>
        ChangeRule(processName, rule => rule.WindowsPriority = Enum.IsDefined(priority)
            ? priority
            : WindowsPriorityLevel.Normal);

    internal void SetLinuxNiceValue(string processName, int value) =>
        ChangeRule(processName, rule => rule.LinuxNiceValue = Math.Clamp(value, -20, 19));

    internal void SetPollInterval(int seconds)
    {
        lock (_settingsGate)
        {
            _settings.PollIntervalSeconds = ProcessPriorityPolicy.NormalizePollInterval(seconds);
        }

        PersistAndRefresh();
    }

    private void ChangeRule(string processName, Action<ProcessPriorityRule> change)
    {
        lock (_settingsGate)
        {
            var rule = _settings.Rules.FirstOrDefault(candidate =>
                ProcessPriorityPolicy.Matches(candidate.ProcessName, processName));
            if (rule is null)
            {
                return;
            }

            change(rule);
        }

        PersistAndRefresh();
    }

    private void PersistAndRefresh()
    {
        var snapshot = GetSettingsSnapshot();
        SaveSettings(snapshot);
        RefreshView(snapshot);
    }

    private void SaveSettings(ProcessPrioritySettings settings)
    {
        try
        {
            _context?.Settings.Save(settings);
        }
        catch (Exception exception)
        {
            _context?.Logger.Error("Could not save Process Priority settings.", exception);
        }
    }

    private static bool SettingsMatch(ProcessPrioritySettings original, ProcessPrioritySettings normalized)
    {
        if (original.PollIntervalSeconds != normalized.PollIntervalSeconds || original.Rules is null ||
            original.Rules.Count != normalized.Rules.Count)
        {
            return false;
        }

        for (var index = 0; index < original.Rules.Count; index++)
        {
            var source = original.Rules[index];
            var target = normalized.Rules[index];
            if (source is null ||
                !string.Equals(source.ProcessName, target.ProcessName, StringComparison.Ordinal) ||
                source.Enabled != target.Enabled ||
                source.WindowsPriority != target.WindowsPriority ||
                source.LinuxNiceValue != target.LinuxNiceValue)
            {
                return false;
            }
        }

        return true;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = GetSettingsSnapshot();
            try
            {
                if (settings.Rules.Any(rule => rule.Enabled))
                {
                    ScanProcesses(settings);
                }
                else
                {
                    SetStatuses([]);
                    Volatile.Write(ref _scanError, null);
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _scanError, exception.GetBaseException().Message);
                var now = DateTimeOffset.UtcNow;
                if (now - _lastScanFailureLog >= LogThrottle)
                {
                    _lastScanFailureLog = now;
                    _context?.Logger.Error("Process Priority could not complete a scan.", exception);
                }
            }

            RefreshView(settings);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ScanProcesses(ProcessPrioritySettings settings)
    {
        var backend = _backend ?? throw new InvalidOperationException("The priority backend is not initialized.");
        var rules = settings.Rules.Where(rule => rule.Enabled).ToArray();
        var results = rules.ToDictionary(
            rule => rule.ProcessName,
            _ => new MutableRuleStatus(),
            StringComparer.OrdinalIgnoreCase);
        var activeProcessIds = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string processName;
                int processId;
                try
                {
                    processName = process.ProcessName;
                    processId = process.Id;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (!ProcessPriorityPolicy.Matches(rule.ProcessName, processName))
                    {
                        continue;
                    }

                    activeProcessIds.Add(processId);
                    var status = results[rule.ProcessName];
                    status.MatchingProcesses++;
                    var result = backend.Apply(process, rule);
                    status.Add(result.Kind);
                    LogResult(rule.ProcessName, processId, result);
                }
            }
        }

        var snapshots = rules.Select(rule => results[rule.ProcessName].ToSnapshot(rule.ProcessName)).ToArray();
        SetStatuses(snapshots);
        Volatile.Write(ref _scanError, null);
        foreach (var key in _lastLogs.Keys.Where(key => !activeProcessIds.Contains(key.ProcessId)).ToArray())
        {
            _lastLogs.Remove(key);
        }
    }

    private void LogResult(string rule, int processId, PriorityEnforcementResult result)
    {
        if (result.Kind is not (PriorityResultKind.Applied or PriorityResultKind.PermissionDenied or PriorityResultKind.Failed))
        {
            return;
        }

        var key = (rule, processId, result.Kind);
        var now = DateTimeOffset.UtcNow;
        if (_lastLogs.TryGetValue(key, out var previous) && now - previous < LogThrottle)
        {
            return;
        }

        _lastLogs[key] = now;
        var message = $"Process '{rule}' PID {processId}: {result.Detail ?? result.Kind.ToString()}.";
        if (result.Kind == PriorityResultKind.Applied)
        {
            _context?.Logger.Info(message);
        }
        else if (result.Kind == PriorityResultKind.PermissionDenied)
        {
            _context?.Logger.Warning(message);
        }
        else
        {
            _context?.Logger.Error(message);
        }
    }

    private void SetStatuses(IEnumerable<ProcessRuleStatus> statuses)
    {
        lock (_statusGate)
        {
            _statuses.Clear();
            foreach (var status in statuses)
            {
                _statuses[status.ProcessName] = status;
            }
        }
    }

    private void RefreshView(ProcessPrioritySettings? settings = null)
    {
        var snapshot = settings ?? GetSettingsSnapshot();
        var statuses = GetStatusesSnapshot();
        var scanError = Volatile.Read(ref _scanError);
        Dispatcher.UIThread.Post(() => _view?.Refresh(snapshot, statuses, scanError));
    }

    private sealed class MutableRuleStatus
    {
        public int MatchingProcesses { get; set; }
        public int Applied { get; set; }
        public int AlreadyCorrect { get; set; }
        public int PermissionDenied { get; set; }
        public int Failed { get; set; }

        public void Add(PriorityResultKind kind)
        {
            switch (kind)
            {
                case PriorityResultKind.Applied:
                    Applied++;
                    break;
                case PriorityResultKind.AlreadyCorrect:
                    AlreadyCorrect++;
                    break;
                case PriorityResultKind.PermissionDenied:
                    PermissionDenied++;
                    break;
                case PriorityResultKind.Failed:
                    Failed++;
                    break;
            }
        }

        public ProcessRuleStatus ToSnapshot(string processName) =>
            new(processName, MatchingProcesses, Applied, AlreadyCorrect, PermissionDenied, Failed);
    }
}
