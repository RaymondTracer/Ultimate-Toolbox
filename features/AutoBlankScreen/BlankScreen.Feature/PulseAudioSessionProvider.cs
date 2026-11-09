using System.Diagnostics;
using System.Text.Json;
using UltimateToolbox.BlankScreen;

namespace UltimateToolbox.AutoBlankScreen.Feature;

/// <summary>Reads PulseAudio-compatible output streams through pactl's JSON interface.</summary>
internal sealed class PulseAudioSessionProvider : IAudioSessionProvider
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public IReadOnlyList<AudioPlaybackSession> ReadSessions()
    {
        var output = RunPactl();
        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("pactl JSON output for sink-inputs was not an array.");
        }

        var sessions = new List<AudioPlaybackSession>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var properties = TryProperty(element, "properties");
            var processName = GetString(properties, "application.process.binary");
            var processId = GetInt32(properties, "application.process.id");
            if (string.IsNullOrWhiteSpace(processName) && processId is > 0)
            {
                processName = TryGetProcessName(processId.Value);
            }

            var displayName = GetString(properties, "application.name")
                ?? GetString(properties, "media.name");
            var isMuted = GetBoolean(element, "mute") ?? false;
            var isCorked = GetBoolean(element, "corked") ?? false;

            sessions.Add(new AudioPlaybackSession(
                processName,
                displayName,
                1f,
                IsActive: !isCorked,
                IsMuted: isMuted,
                HasMeasuredLevel: false));
        }

        return sessions;
    }

    private static string RunPactl()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pactl",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--format=json");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("sink-inputs");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("pactl did not start.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PlatformNotSupportedException(
                "Linux audio blocking requires pactl and a PulseAudio-compatible server (including PipeWire's PulseAudio compatibility service).",
                exception);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)CommandTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"pactl did not finish within {CommandTimeout.TotalSeconds:0} seconds.");
        }

        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pactl list sink-inputs failed with exit code {process.ExitCode}: {stderr.Result.Trim()}");
        }

        return stdout.Result;
    }

    private static JsonElement TryProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : default;

    private static string? GetString(JsonElement element, string name)
    {
        var value = TryProperty(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt32(JsonElement element, string name)
    {
        var value = TryProperty(element, name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number)
            ? number
            : null;
    }

    private static bool? GetBoolean(JsonElement element, string name)
    {
        var value = TryProperty(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? TryGetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
