using System.Diagnostics;

namespace UltimateToolbox.PowerPlan.Feature;

internal sealed class PowerProfileCommandRunner
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public CommandOutput Run(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {fileName}.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start '{fileName}': {exception.Message}", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"'{fileName}' did not finish within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        Task.WaitAll(standardOutputTask, standardErrorTask);
        return new CommandOutput(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
    }
}

internal sealed record CommandOutput(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedText => string.Join(
        Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(text => text.Length > 0));
}
