using System.Text.RegularExpressions;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.PowerPlan;

/// <summary>Shared exact-name and command-line rules from the original power-plan watcher.</summary>
public static partial class WatchedProcessPolicy
{
    private static readonly Regex DotNetCommandLine = new(
        @"(?i)^\s*(?:""(?:(?:[^""]*[/\\])?dotnet(?:\.exe)?)""|(?:[^""\s]*[/\\])?dotnet(?:\.exe)?)\s+""?(?<verb>build|publish|test|restore|msbuild)""?(?=\s|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MsBuildNodeModeCommandLine = new(
        @"(?i)(?:^|\s)[/-]nodemode(?:\s|:|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] DefaultWatchedNames =
    [
        "cl",
        "link",
        "msbuild",
        "cmake",
        "ninja",
        "vcpkg",
        "csc",
        "clang",
        "clang-cl",
        "clang++",
        "lld-link",
        "gcc",
        "g++"
    ];

    private static readonly string[] DefaultDotNetVerbNames = ["build", "publish", "test", "restore", "msbuild"];

    public static IReadOnlyList<string> DefaultWatchedProcessNames => Array.AsReadOnly(DefaultWatchedNames);

    public static IReadOnlyList<string> DefaultDotNetVerbs => Array.AsReadOnly(DefaultDotNetVerbNames);

    public static IReadOnlyList<string> NormalizeProcessNames(IEnumerable<string>? processNames)
    {
        if (processNames is null)
        {
            return DefaultWatchedNames.ToArray();
        }

        var normalized = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in processNames)
        {
            var name = ProcessPriorityPolicy.NormalizeProcessName(candidate);
            if (name is null || ProcessPriorityPolicy.Matches(name, "dotnet") || !unique.Add(name))
            {
                continue;
            }

            normalized.Add(name);
        }

        return normalized;
    }

    public static IReadOnlyList<string> NormalizeDotNetVerbs(IEnumerable<string>? verbs)
    {
        if (verbs is null)
        {
            return DefaultDotNetVerbNames.ToArray();
        }

        var normalized = new List<string>();
        foreach (var verb in DefaultDotNetVerbNames)
        {
            if (verbs.Any(candidate => string.Equals(candidate?.Trim(), verb, StringComparison.OrdinalIgnoreCase)))
            {
                normalized.Add(verb);
            }
        }

        return normalized;
    }

    public static string? GetWatchedDotNetVerb(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = DotNetCommandLine.Match(commandLine);
        return match.Success ? match.Groups["verb"].Value.ToLowerInvariant() : null;
    }

    public static bool IsReusableMsBuildWorker(string? commandLine) =>
        !string.IsNullOrWhiteSpace(commandLine) && MsBuildNodeModeCommandLine.IsMatch(commandLine);

    public static bool Matches(string? configuredName, string? observedName) =>
        ProcessPriorityPolicy.Matches(configuredName, observedName);
}
