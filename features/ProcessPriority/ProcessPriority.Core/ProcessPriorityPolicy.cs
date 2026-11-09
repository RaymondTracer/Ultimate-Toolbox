namespace UltimateToolbox.ProcessPriority;

/// <summary>Shared process-name matching and persisted-rule normalization.</summary>
public static class ProcessPriorityPolicy
{
    public const int MinimumPollIntervalSeconds = 1;
    public const int MaximumPollIntervalSeconds = 300;

    public static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var name = processName.Trim().Replace('\\', '/');
        var separator = name.LastIndexOf('/');
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl) || name.IndexOfAny(['*', '?']) >= 0)
        {
            return null;
        }

        return name;
    }

    public static bool Matches(string? ruleProcessName, string? observedProcessName)
    {
        var rule = NormalizeProcessName(ruleProcessName);
        var observed = NormalizeProcessName(observedProcessName);
        return rule is not null && observed is not null &&
            string.Equals(rule, observed, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ProcessPriorityRule> NormalizeRules(IEnumerable<ProcessPriorityRule>? rules)
    {
        if (rules is null)
        {
            return [];
        }

        var normalized = new List<ProcessPriorityRule>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule is null)
            {
                continue;
            }

            var processName = NormalizeProcessName(rule.ProcessName);
            if (processName is null || !names.Add(processName))
            {
                continue;
            }

            normalized.Add(new ProcessPriorityRule
            {
                ProcessName = processName,
                Enabled = rule.Enabled,
                WindowsPriority = Enum.IsDefined(rule.WindowsPriority)
                    ? rule.WindowsPriority
                    : WindowsPriorityLevel.Normal,
                LinuxNiceValue = Math.Clamp(rule.LinuxNiceValue, -20, 19)
            });
        }

        return normalized;
    }

    public static int NormalizePollInterval(int seconds) =>
        Math.Clamp(seconds, MinimumPollIntervalSeconds, MaximumPollIntervalSeconds);
}
