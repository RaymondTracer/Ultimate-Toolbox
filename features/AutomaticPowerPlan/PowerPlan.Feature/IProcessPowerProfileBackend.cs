using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

internal interface IProcessPowerProfileBackend
{
    IReadOnlyList<PowerProfile> GetAvailableProfiles();

    string? SuggestPerformanceProfileId(IReadOnlyList<PowerProfile> availableProfiles);

    string GetCurrentProfileId();

    void SetActiveProfile(string profileId);
}

internal sealed record ProcessCommandLineLookup(
    bool IsAvailable,
    IReadOnlyDictionary<int, string?> CommandLines,
    string? Error);

internal sealed record ObservedProcess(int ProcessId, string ProcessName);

internal interface IProcessCommandLineProvider
{
    ProcessCommandLineLookup GetCommandLines(IReadOnlyList<ObservedProcess> candidates);
}
