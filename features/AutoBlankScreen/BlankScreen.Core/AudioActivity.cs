namespace UltimateToolbox.BlankScreen;

public sealed record AudioPlaybackSession(
    string? ProcessName,
    string? DisplayName,
    float PeakLevel,
    bool IsActive,
    bool IsMuted,
    bool HasMeasuredLevel);

public sealed record AudioActivitySnapshot(
    bool IsAvailable,
    bool HasAudio,
    string? ProcessName,
    float PeakLevel,
    bool HasMeasuredLevel,
    IReadOnlyList<string> RecentlyDetectedProcesses,
    string? Error)
{
    public static AudioActivitySnapshot Unavailable(string? error) =>
        new(false, false, null, 0, false, Array.Empty<string>(), error);
}

/// <summary>Filters platform audio sessions using the legacy Auto Blank Screen matching rules.</summary>
public static class AudioActivityPolicy
{
    private const float NoiseFloor = 0.002f;

    public static AudioActivitySnapshot Evaluate(
        IEnumerable<AudioPlaybackSession> sessions,
        IEnumerable<string> monitoredProcesses,
        IReadOnlyList<string> recentlyDetectedProcesses)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(monitoredProcesses);
        ArgumentNullException.ThrowIfNull(recentlyDetectedProcesses);

        var rules = monitoredProcesses.ToArray();
        var candidates = sessions
            .Where(session => session.IsActive && !session.IsMuted)
            .Where(session => !session.HasMeasuredLevel || session.PeakLevel > NoiseFloor)
            .Where(session => BlankScreenPolicy.MatchesAudioProcess(rules, session.ProcessName, session.DisplayName))
            .Select(session => new
            {
                Session = session,
                Label = !string.IsNullOrWhiteSpace(session.ProcessName)
                    ? session.ProcessName
                    : !string.IsNullOrWhiteSpace(session.DisplayName)
                        ? session.DisplayName
                        : "audio",
                Level = Math.Clamp(session.PeakLevel, 0f, 1f)
            })
            .ToArray();

        var selected = candidates
            .OrderByDescending(candidate => candidate.Session.HasMeasuredLevel)
            .ThenByDescending(candidate => candidate.Level)
            .FirstOrDefault();

        return selected is null
            ? new(true, false, null, 0, true, recentlyDetectedProcesses, null)
            : new(
                true,
                true,
                selected.Label,
                selected.Level,
                selected.Session.HasMeasuredLevel,
                recentlyDetectedProcesses,
                null);
    }
}
