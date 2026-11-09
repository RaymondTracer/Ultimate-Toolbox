using System.Text.RegularExpressions;
using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

internal sealed class LinuxPowerProfileBackend : IProcessPowerProfileBackend
{
    private static readonly Regex ProfileLine = new(
        @"^\s*\*?\s*(?<id>[a-z0-9][a-z0-9-]*)\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ProfileIdPattern = new(
        @"^[a-z0-9][a-z0-9-]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly PowerProfileCommandRunner _runner = new();

    public IReadOnlyList<PowerProfile> GetAvailableProfiles()
    {
        var output = RunPowerProfilesCtl("list");
        var profiles = new List<PowerProfile>();
        foreach (var line in output.StandardOutput.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = ProfileLine.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var id = match.Groups["id"].Value.ToLowerInvariant();
            if (profiles.All(profile => !string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                profiles.Add(new PowerProfile(id, ToDisplayName(id), id));
            }
        }

        return profiles.Count > 0
            ? profiles
            : throw new InvalidOperationException("powerprofilesctl list did not return any available profiles.");
    }

    public string? SuggestPerformanceProfileId(IReadOnlyList<PowerProfile> availableProfiles) =>
        availableProfiles.FirstOrDefault(profile =>
            string.Equals(profile.Id, "performance", StringComparison.OrdinalIgnoreCase))?.Id;

    public string GetCurrentProfileId()
    {
        var output = RunPowerProfilesCtl("get");
        var profileId = output.StandardOutput.Trim();
        if (!ProfileIdPattern.IsMatch(profileId))
        {
            throw new InvalidOperationException("powerprofilesctl get did not return a valid profile name.");
        }

        return profileId.ToLowerInvariant();
    }

    public void SetActiveProfile(string profileId)
    {
        if (!ProfileIdPattern.IsMatch(profileId))
        {
            throw new ArgumentException("A Linux power profile must be identified by its profile name.", nameof(profileId));
        }

        RunPowerProfilesCtl("set", profileId);
    }

    private CommandOutput RunPowerProfilesCtl(params string[] arguments)
    {
        CommandOutput output;
        try
        {
            output = _runner.Run("powerprofilesctl", arguments);
        }
        catch (InvalidOperationException exception)
        {
            throw new PlatformNotSupportedException(
                "Linux profile switching requires powerprofilesctl from power-profiles-daemon (or a compatible implementation).",
                exception);
        }

        if (output.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"powerprofilesctl {string.Join(' ', arguments)} failed with exit code {output.ExitCode}. {output.CombinedText}");
        }

        return output;
    }

    private static string ToDisplayName(string id) =>
        string.Join(' ', id.Split('-').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
