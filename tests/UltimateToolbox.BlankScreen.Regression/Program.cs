using UltimateToolbox.BlankScreen;

Equal("zero timeout keeps the legacy ten-minute fallback", TimeSpan.FromMinutes(10), BlankScreenPolicy.GetTimeout(0, 0));
Equal("legacy default timeout remains ten minutes", TimeSpan.FromMinutes(10), BlankScreenPolicy.GetTimeout(0, 10));
Equal("timeout hours and minutes are bounded", TimeSpan.FromHours(999) + TimeSpan.FromMinutes(59), BlankScreenPolicy.GetTimeout(2000, 75));
Equal("timeout splits into UI hour and minute fields", (12, 34), BlankScreenPolicy.SplitTimeout(TimeSpan.FromMinutes((12 * 60) + 34)));

SequenceEqual(
    "audio process rules normalize paths, extensions, case, and duplicates",
    ["firefox", "chat app"],
    BlankScreenPolicy.NormalizeAudioProcessNames(["C:\\Apps\\Firefox.exe", "FIREFOX", " chat app "]));
True("legacy default audio rules are preserved", BlankScreenPolicy.DefaultMonitoredAudioProcesses.SequenceEqual(["firefox", "discord"]));
True("configured rules match substrings of process names", BlankScreenPolicy.MatchesAudioProcess(["firefox"], "org.mozilla.firefox-bin", null));
True("configured rules match substrings of app labels", BlankScreenPolicy.MatchesAudioProcess(["discord"], null, "Discord Voice"));
True("empty process list monitors every audio process", BlankScreenPolicy.MatchesAudioProcess([], "music-player", null));

var audio = AudioActivityPolicy.Evaluate(
    [
        new AudioPlaybackSession("firefox", "Browser", 0.8f, IsActive: true, IsMuted: false, HasMeasuredLevel: true),
        new AudioPlaybackSession("discord", "Voice", 1f, IsActive: true, IsMuted: true, HasMeasuredLevel: true),
        new AudioPlaybackSession("music", "Player", 1f, IsActive: false, IsMuted: false, HasMeasuredLevel: false)
    ],
    ["firefox", "discord"],
    ["firefox", "discord", "music"]);
True("active monitored audio blocks blanking", audio.HasAudio);
Equal("loudest active monitored session is identified", "firefox", audio.ProcessName);
Equal("Windows level meter is preserved", 0.8f, audio.PeakLevel);
True("recent app candidates remain available for the editor", audio.RecentlyDetectedProcesses.Contains("music"));

var now = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
var automatic = BlankScreenStateMachine.Evaluate(
    null,
    TimeSpan.FromMinutes(10),
    audioAvailable: true,
    audioActive: false,
    manualBlankRequested: false,
    now,
    TimeSpan.FromMinutes(10));
Equal("idle timeout shows the overlay", BlankScreenAction.ShowAutomatic, automatic.Action);
True("automatic overlay state is remembered", automatic.NextState.OverlayVisible);

var hold = BlankScreenStateMachine.Evaluate(
    automatic.NextState,
    TimeSpan.Zero,
    audioAvailable: true,
    audioActive: false,
    manualBlankRequested: false,
    now.AddMilliseconds(100),
    TimeSpan.FromMinutes(10));
Equal("initial automatic overlay hold avoids immediate dismissal", BlankScreenAction.None, hold.Action);

var wakeForInput = BlankScreenStateMachine.Evaluate(
    automatic.NextState,
    TimeSpan.FromMilliseconds(100),
    audioAvailable: true,
    audioActive: false,
    manualBlankRequested: false,
    now.AddMilliseconds(500),
    TimeSpan.FromMinutes(10));
Equal("input after the hold dismisses the overlay", BlankScreenAction.HideForActivity, wakeForInput.Action);
False("input wake clears overlay state", wakeForInput.NextState.OverlayVisible);

var wakeForAudio = BlankScreenStateMachine.Evaluate(
    automatic.NextState,
    TimeSpan.FromMinutes(10),
    audioAvailable: true,
    audioActive: true,
    manualBlankRequested: false,
    now.AddMilliseconds(100),
    TimeSpan.FromMinutes(10));
Equal("audio dismisses immediately even during the hold", BlankScreenAction.HideForAudio, wakeForAudio.Action);

var audioReset = BlankScreenStateMachine.Evaluate(
    null,
    TimeSpan.FromMinutes(20),
    audioAvailable: true,
    audioActive: true,
    manualBlankRequested: false,
    now,
    TimeSpan.FromMinutes(10));
Equal("active audio resets effective idle", TimeSpan.Zero, audioReset.EffectiveIdle);
var afterAudio = BlankScreenStateMachine.Evaluate(
    audioReset.NextState,
    TimeSpan.FromMinutes(5),
    audioAvailable: true,
    audioActive: false,
    manualBlankRequested: false,
    now.AddSeconds(2),
    TimeSpan.FromMinutes(10));
Equal("idle time remains reset until it catches up with audio end", TimeSpan.FromSeconds(2), afterAudio.EffectiveIdle);

var manual = BlankScreenStateMachine.Evaluate(
    null,
    TimeSpan.FromSeconds(2),
    audioAvailable: false,
    audioActive: false,
    manualBlankRequested: true,
    now,
    TimeSpan.FromMinutes(10));
Equal("manual blank is allowed as an explicit user action", BlankScreenAction.ShowManual, manual.Action);

var manualAudioBlocked = BlankScreenStateMachine.Evaluate(
    null,
    TimeSpan.FromSeconds(2),
    audioAvailable: true,
    audioActive: true,
    manualBlankRequested: true,
    now,
    TimeSpan.FromMinutes(10));
Equal("manual blank respects active audio", BlankScreenAction.ManualBlockedByAudio, manualAudioBlocked.Action);

var automaticAudioUnavailable = BlankScreenStateMachine.Evaluate(
    null,
    TimeSpan.FromHours(1),
    audioAvailable: false,
    audioActive: false,
    manualBlankRequested: false,
    now,
    TimeSpan.FromMinutes(10));
Equal("automatic blanking fails open when audio monitoring is unavailable", BlankScreenAction.None, automaticAudioUnavailable.Action);
False("unavailable audio does not create an overlay", automaticAudioUnavailable.NextState.OverlayVisible);

var activityUnavailable = BlankScreenStateMachine.Evaluate(
    null,
    null,
    audioAvailable: true,
    audioActive: false,
    manualBlankRequested: true,
    now,
    TimeSpan.FromMinutes(10));
Equal("manual blank requires an available wake input source", BlankScreenAction.ManualBlockedByUnavailableActivity, activityUnavailable.Action);

Console.WriteLine("Auto Blank Screen regression checks passed.");
return;

static void Equal<T>(string name, T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{name}: expected {expected}, got {actual}.");
    }
}

static void True(string name, bool actual)
{
    if (!actual)
    {
        throw new InvalidOperationException($"{name}: expected true.");
    }
}

static void False(string name, bool actual)
{
    if (actual)
    {
        throw new InvalidOperationException($"{name}: expected false.");
    }
}

static void SequenceEqual<T>(string name, IReadOnlyList<T> expected, IReadOnlyList<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"{name}: sequences did not match.");
    }
}
