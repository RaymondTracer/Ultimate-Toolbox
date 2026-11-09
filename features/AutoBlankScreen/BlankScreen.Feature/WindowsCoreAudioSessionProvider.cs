using System.Diagnostics;
using System.Runtime.Versioning;
using UltimateToolbox.BlankScreen;

namespace UltimateToolbox.AutoBlankScreen.Feature;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCoreAudioSessionProvider : IAudioSessionProvider
{
    public IReadOnlyList<AudioPlaybackSession> ReadSessions() =>
        CoreAudioSessionEnumerator.EnumerateRenderSessions()
            .Select(session => new AudioPlaybackSession(
                TryGetProcessName(session.ProcessId),
                session.DisplayName,
                session.PeakLevel,
                IsActive: true,
                IsMuted: false,
                HasMeasuredLevel: true))
            .ToArray();

    private static string? TryGetProcessName(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

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
