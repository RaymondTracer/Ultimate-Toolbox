using System.Diagnostics;
using UltimateToolbox.PowerPlan;

namespace UltimateToolbox.PowerPlan.Feature;

internal sealed class ProcessActivityScanner
{
    private readonly IProcessCommandLineProvider _commandLineProvider;

    public ProcessActivityScanner(IProcessCommandLineProvider commandLineProvider)
    {
        _commandLineProvider = commandLineProvider;
    }

    public ProcessActivitySnapshot Scan(AutomaticPowerPlanSettings settings)
    {
        var observed = new List<ObservedProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    observed.Add(new ObservedProcess(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        foreach (var configuredName in settings.WatchedProcessNames)
        {
            if (settings.IgnoreReusableMsBuildWorkerNodes &&
                WatchedProcessPolicy.Matches(configuredName, "msbuild"))
            {
                continue;
            }

            var matchingCount = observed.Count(process =>
                WatchedProcessPolicy.Matches(configuredName, process.ProcessName));
            if (matchingCount > 0)
            {
                var name = configuredName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? configuredName
                    : $"{configuredName}.exe";
                return new ProcessActivitySnapshot(
                    true,
                    $"{name} ({matchingCount} process(es))",
                    null,
                    []);
            }
        }

        var dotNetProcesses = observed
            .Where(process => WatchedProcessPolicy.Matches("dotnet", process.ProcessName))
            .ToArray();
        var watchesMsBuild = settings.WatchedProcessNames.Any(name =>
            WatchedProcessPolicy.Matches("msbuild", name));
        var msBuildProcesses = watchesMsBuild
            ? observed.Where(process => WatchedProcessPolicy.Matches("msbuild", process.ProcessName)).ToArray()
            : Array.Empty<ObservedProcess>();

        if (dotNetProcesses.Length == 0 && msBuildProcesses.Length == 0)
        {
            return new ProcessActivitySnapshot(false, null, null, []);
        }

        var candidates = dotNetProcesses.Concat(msBuildProcesses).DistinctBy(process => process.ProcessId).ToArray();
        var lookup = _commandLineProvider.GetCommandLines(candidates);
        var warnings = new List<string>();
        var ignoredWorkers = 0;

        if (!lookup.IsAvailable)
        {
            warnings.Add($"Could not inspect process command lines: {lookup.Error ?? "provider unavailable"}.");
        }

        foreach (var process in dotNetProcesses)
        {
            if (!lookup.IsAvailable ||
                !lookup.CommandLines.TryGetValue(process.ProcessId, out var commandLine) ||
                string.IsNullOrWhiteSpace(commandLine))
            {
                warnings.Add("Uninspectable dotnet.exe processes were ignored for this scan.");
                continue;
            }

            var verb = WatchedProcessPolicy.GetWatchedDotNetVerb(commandLine);
            if (verb is not null && settings.WatchedDotNetVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                return new ProcessActivitySnapshot(
                    true,
                    $"dotnet.exe {verb} (PID {process.ProcessId})",
                    null,
                    warnings.Distinct(StringComparer.Ordinal).ToArray());
            }
        }

        if (watchesMsBuild)
        {
            foreach (var process in msBuildProcesses)
            {
                if (!lookup.IsAvailable ||
                    !lookup.CommandLines.TryGetValue(process.ProcessId, out var commandLine) ||
                    string.IsNullOrWhiteSpace(commandLine))
                {
                    warnings.Add("Could not inspect MSBuild.exe command lines; treating the MSBuild process as watched activity.");
                    return new ProcessActivitySnapshot(
                        true,
                        $"msbuild.exe (PID {process.ProcessId})",
                        null,
                        warnings.Distinct(StringComparer.Ordinal).ToArray());
                }

                if (settings.IgnoreReusableMsBuildWorkerNodes &&
                    WatchedProcessPolicy.IsReusableMsBuildWorker(commandLine))
                {
                    ignoredWorkers++;
                    continue;
                }

                return new ProcessActivitySnapshot(
                    true,
                    $"msbuild.exe (PID {process.ProcessId})",
                    null,
                    warnings.Distinct(StringComparer.Ordinal).ToArray());
            }
        }

        var ignoredDetails = ignoredWorkers > 0
            ? $"Ignored {ignoredWorkers} reusable MSBuild worker process(es) (/nodemode)."
            : null;
        return new ProcessActivitySnapshot(
            false,
            null,
            ignoredDetails,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }
}
