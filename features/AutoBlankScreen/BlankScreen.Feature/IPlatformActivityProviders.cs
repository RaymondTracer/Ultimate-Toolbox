using UltimateToolbox.BlankScreen;

namespace UltimateToolbox.AutoBlankScreen.Feature;

internal interface IIdleTimeProvider : IDisposable
{
    string Description { get; }

    string? Error { get; }

    TimeSpan? GetIdleTime();
}

internal interface IAudioSessionProvider
{
    IReadOnlyList<AudioPlaybackSession> ReadSessions();
}
