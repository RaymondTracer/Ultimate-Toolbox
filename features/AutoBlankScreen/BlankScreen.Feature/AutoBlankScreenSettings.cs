using UltimateToolbox.BlankScreen;

namespace UltimateToolbox.AutoBlankScreen.Feature;

public sealed class AutoBlankScreenSettings
{
    public int TimeoutHours { get; set; }

    public int TimeoutMinutes { get; set; } = 10;

    public List<string> MonitoredAudioProcesses { get; set; } = BlankScreenPolicy.DefaultMonitoredAudioProcesses.ToList();

    public TimeSpan GetTimeout() => BlankScreenPolicy.GetTimeout(TimeoutHours, TimeoutMinutes);

    public AutoBlankScreenSettings Copy() => new()
    {
        TimeoutHours = Math.Clamp(TimeoutHours, 0, 999),
        TimeoutMinutes = Math.Clamp(TimeoutMinutes, 0, 59),
        MonitoredAudioProcesses = BlankScreenPolicy.NormalizeAudioProcessNames(MonitoredAudioProcesses).ToList()
    };

    public bool IsEquivalentTo(AutoBlankScreenSettings normalized) =>
        TimeoutHours == normalized.TimeoutHours &&
        TimeoutMinutes == normalized.TimeoutMinutes &&
        MonitoredAudioProcesses is not null &&
        MonitoredAudioProcesses.SequenceEqual(normalized.MonitoredAudioProcesses, StringComparer.Ordinal);
}
