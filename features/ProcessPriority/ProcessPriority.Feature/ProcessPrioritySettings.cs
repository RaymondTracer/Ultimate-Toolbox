using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

public sealed class ProcessPrioritySettings
{
    public int PollIntervalSeconds { get; set; } = 2;

    public List<ProcessPriorityRule> Rules { get; set; } = [];

    public ProcessPrioritySettings Copy() => new()
    {
        PollIntervalSeconds = ProcessPriorityPolicy.NormalizePollInterval(PollIntervalSeconds),
        Rules = ProcessPriorityPolicy.NormalizeRules(Rules).Select(rule => rule.Copy()).ToList()
    };
}

internal sealed record ProcessRuleStatus(
    string ProcessName,
    int MatchingProcesses,
    int Applied,
    int AlreadyCorrect,
    int PermissionDenied,
    int Failed);
