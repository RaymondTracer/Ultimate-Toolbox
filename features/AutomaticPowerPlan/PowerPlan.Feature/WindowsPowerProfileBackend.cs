using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

[SupportedOSPlatform("windows")]
internal sealed class WindowsPowerProfileBackend : IProcessPowerProfileBackend
{
    private static readonly Regex GuidPattern = new(
        @"\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NamePattern = new(
        @"\((?<name>[^)]*)\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly PowerProfileCommandRunner _runner = new();
    private readonly string _powerCfgPath = Path.Combine(Environment.SystemDirectory, "powercfg.exe");

    public IReadOnlyList<PowerProfile> GetAvailableProfiles()
    {
        var output = RunPowerCfg("/list");
        var profiles = new List<PowerProfile>();
        foreach (var line in output.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var guid = GuidPattern.Match(line);
            if (!guid.Success)
            {
                continue;
            }

            var nameMatch = NamePattern.Match(line);
            var name = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : guid.Value;
            profiles.Add(new PowerProfile(guid.Value.ToLowerInvariant(), name, line.Trim()));
        }

        return profiles.Count > 0
            ? profiles
            : throw new InvalidOperationException("powercfg /list did not return any power-scheme GUIDs.");
    }

    public string? SuggestPerformanceProfileId(IReadOnlyList<PowerProfile> availableProfiles)
    {
        var availableIds = availableProfiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var aliases = RunPowerCfg("/aliases").StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        for (var aliasIndex = 0; aliasIndex < aliases.Length; aliasIndex++)
        {
            if (!aliases[aliasIndex].Contains("SCHEME_MIN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var firstLine = Math.Max(0, aliasIndex - 3);
            for (var lineIndex = aliasIndex; lineIndex >= firstLine; lineIndex--)
            {
                var guid = GuidPattern.Match(aliases[lineIndex]);
                if (guid.Success && availableIds.Contains(guid.Value))
                {
                    return guid.Value.ToLowerInvariant();
                }
            }
        }

        throw new InvalidOperationException(
            "Could not resolve the SCHEME_MIN performance plan. Select an available power plan manually.");
    }

    public string GetCurrentProfileId()
    {
        var output = RunPowerCfg("/getactivescheme");
        foreach (var line in output.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var guid = GuidPattern.Match(line);
            if (guid.Success)
            {
                return guid.Value.ToLowerInvariant();
            }
        }

        throw new InvalidOperationException("powercfg /getactivescheme did not return a power-scheme GUID.");
    }

    public void SetActiveProfile(string profileId)
    {
        if (!Guid.TryParse(profileId, out var guid))
        {
            throw new ArgumentException("A Windows power scheme must be identified by its GUID.", nameof(profileId));
        }

        RunPowerCfg("/setactive", guid.ToString("D"));
    }

    private CommandOutput RunPowerCfg(params string[] arguments)
    {
        if (!File.Exists(_powerCfgPath))
        {
            throw new PlatformNotSupportedException($"Could not locate powercfg.exe at '{_powerCfgPath}'.");
        }

        var output = _runner.Run(_powerCfgPath, arguments);
        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"powercfg {string.Join(' ', arguments)} failed with exit code {output.ExitCode}. {output.CombinedText}");
        }

        return output;
    }
}
