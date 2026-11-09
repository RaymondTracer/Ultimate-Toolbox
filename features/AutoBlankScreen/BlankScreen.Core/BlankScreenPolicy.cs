namespace UltimateToolbox.BlankScreen;

/// <summary>Owns timeout and process-rule normalization for the Auto Blank Screen feature.</summary>
public static class BlankScreenPolicy
{
    private static readonly string[] DefaultAudioProcesses = ["firefox", "discord"];

    public static IReadOnlyList<string> DefaultMonitoredAudioProcesses => Array.AsReadOnly(DefaultAudioProcesses);

    public static TimeSpan GetTimeout(int hours, int minutes)
    {
        var totalMinutes = (Math.Clamp(hours, 0, 999) * 60L) + Math.Clamp(minutes, 0, 59);
        return TimeSpan.FromMinutes(totalMinutes <= 0 ? 10 : totalMinutes);
    }

    public static (int Hours, int Minutes) SplitTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            timeout = TimeSpan.Zero;
        }

        var totalMinutes = (int)Math.Clamp(Math.Floor(timeout.TotalMinutes), 0, (999 * 60) + 59);
        return (totalMinutes / 60, totalMinutes % 60);
    }

    public static IReadOnlyList<string> NormalizeAudioProcessNames(IEnumerable<string>? processNames)
    {
        if (processNames is null)
        {
            return DefaultAudioProcesses.ToArray();
        }

        var normalized = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in processNames)
        {
            var name = NormalizeAudioProcessName(candidate);
            if (name is not null && unique.Add(name))
            {
                normalized.Add(name);
            }
        }

        return normalized;
    }

    public static string? NormalizeAudioProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var name = processName.Trim().Trim('"');
        var separator = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (separator >= 0)
        {
            name = name[(separator + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name.Trim();
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    /// <summary>
    /// Preserves the original audio-rule contract: configured names match as
    /// case-insensitive substrings of the audio session's process or display name.
    /// An empty list means every output stream is monitored.
    /// </summary>
    public static bool MatchesAudioProcess(
        IEnumerable<string> configuredNames,
        string? processName,
        string? displayName)
    {
        ArgumentNullException.ThrowIfNull(configuredNames);
        var names = configuredNames.ToArray();
        if (names.Length == 0)
        {
            return true;
        }

        return names.Any(name =>
            Contains(processName, name) || Contains(displayName, name));
    }

    private static bool Contains(string? value, string search)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Contains(search, StringComparison.OrdinalIgnoreCase);
    }
}
