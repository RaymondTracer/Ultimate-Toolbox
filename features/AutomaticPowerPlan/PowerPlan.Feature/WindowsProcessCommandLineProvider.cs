using System.Management;
using System.Runtime.Versioning;

namespace UltimateToolbox.PowerPlan.Feature;

[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessCommandLineProvider : IProcessCommandLineProvider
{
    public ProcessCommandLineLookup GetCommandLines(IReadOnlyList<ObservedProcess> candidates)
    {
        if (candidates.Count == 0)
        {
            return new(true, new Dictionary<int, string?>(), null);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'dotnet.exe' OR Name = 'MSBuild.exe'");
            using var rows = searcher.Get();
            var candidateIds = candidates.Select(candidate => candidate.ProcessId).ToHashSet();
            var commandLines = new Dictionary<int, string?>();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    var processId = Convert.ToInt32(row["ProcessId"]);
                    if (candidateIds.Contains(processId))
                    {
                        commandLines[processId] = row["CommandLine"] as string;
                    }
                }
            }

            return new(true, commandLines, null);
        }
        catch (Exception exception)
        {
            return new(false, new Dictionary<int, string?>(), exception.GetBaseException().Message);
        }
    }
}
