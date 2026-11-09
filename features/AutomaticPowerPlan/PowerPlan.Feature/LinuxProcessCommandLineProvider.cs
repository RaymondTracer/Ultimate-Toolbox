using System.Text;

namespace UltimateToolbox.PowerPlan.Feature;

internal sealed class LinuxProcessCommandLineProvider : IProcessCommandLineProvider
{
    public ProcessCommandLineLookup GetCommandLines(IReadOnlyList<ObservedProcess> candidates)
    {
        var commandLines = new Dictionary<int, string?>();
        foreach (var candidate in candidates)
        {
            try
            {
                var bytes = File.ReadAllBytes($"/proc/{candidate.ProcessId}/cmdline");
                var arguments = Encoding.UTF8.GetString(bytes)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                commandLines[candidate.ProcessId] = string.Join(
                    " ",
                    arguments.Select(argument => $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Missing or unreadable command lines are handled according to the
                // original dotnet-ignore and MSBuild-conservative-fallback rules.
            }
        }

        return new(true, commandLines, null);
    }
}
