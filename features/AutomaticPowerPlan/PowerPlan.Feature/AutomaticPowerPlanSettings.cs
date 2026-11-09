using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

public sealed class AutomaticPowerPlanSettings
{
    public bool Enabled { get; set; } = true;

    public int PollingIntervalSeconds { get; set; } = 2;

    public int GracePeriodSeconds { get; set; } = 30;

    public string? TargetProfileId { get; set; }

    public List<string> WatchedProcessNames { get; set; } = WatchedProcessPolicy.DefaultWatchedProcessNames.ToList();

    public List<string> WatchedDotNetVerbs { get; set; } = WatchedProcessPolicy.DefaultDotNetVerbs.ToList();

    public bool IgnoreReusableMsBuildWorkerNodes { get; set; } = true;

    public PowerPlanRuntimeState RuntimeState { get; set; } = new();

    public AutomaticPowerPlanSettings Copy()
    {
        var copy = new AutomaticPowerPlanSettings
        {
            Enabled = Enabled,
            PollingIntervalSeconds = Math.Clamp(PollingIntervalSeconds, 1, 3600),
            GracePeriodSeconds = Math.Clamp(GracePeriodSeconds, 0, 86400),
            TargetProfileId = string.IsNullOrWhiteSpace(TargetProfileId) ? null : TargetProfileId.Trim(),
            WatchedProcessNames = WatchedProcessPolicy.NormalizeProcessNames(WatchedProcessNames).ToList(),
            WatchedDotNetVerbs = WatchedProcessPolicy.NormalizeDotNetVerbs(WatchedDotNetVerbs).ToList(),
            IgnoreReusableMsBuildWorkerNodes = IgnoreReusableMsBuildWorkerNodes,
            RuntimeState = RuntimeState?.Copy() ?? new PowerPlanRuntimeState()
        };

        if (!copy.RuntimeState.SwitchInProgress || string.IsNullOrWhiteSpace(copy.RuntimeState.RestoreProfileId))
        {
            copy.RuntimeState = new PowerPlanRuntimeState();
        }

        return copy;
    }

    public bool IsEquivalentTo(AutomaticPowerPlanSettings normalized)
    {
        if (Enabled != normalized.Enabled ||
            PollingIntervalSeconds != normalized.PollingIntervalSeconds ||
            GracePeriodSeconds != normalized.GracePeriodSeconds ||
            !string.Equals(TargetProfileId, normalized.TargetProfileId, StringComparison.Ordinal) ||
            IgnoreReusableMsBuildWorkerNodes != normalized.IgnoreReusableMsBuildWorkerNodes ||
            WatchedProcessNames is null || WatchedDotNetVerbs is null ||
            !WatchedProcessNames.SequenceEqual(normalized.WatchedProcessNames, StringComparer.Ordinal) ||
            !WatchedDotNetVerbs.SequenceEqual(normalized.WatchedDotNetVerbs, StringComparer.Ordinal))
        {
            return false;
        }

        var originalState = RuntimeState ?? new PowerPlanRuntimeState();
        var normalizedState = normalized.RuntimeState;
        return originalState.SwitchInProgress == normalizedState.SwitchInProgress &&
            string.Equals(originalState.RestoreProfileId, normalizedState.RestoreProfileId, StringComparison.Ordinal) &&
            originalState.LastActivityAt == normalizedState.LastActivityAt &&
            originalState.GracePeriodActive == normalizedState.GracePeriodActive;
    }
}

internal sealed record ProcessActivitySnapshot(
    bool IsActive,
    string? Details,
    string? IgnoredDetails,
    IReadOnlyList<string> Warnings);

internal sealed record PowerPlanViewSnapshot(
    IReadOnlyList<PowerProfile> Profiles,
    string? CurrentProfileId,
    string? StatusMessage,
    string? ErrorMessage,
    string? ActivityDetails,
    string? IgnoredActivityDetails,
    int? GraceSecondsRemaining);
