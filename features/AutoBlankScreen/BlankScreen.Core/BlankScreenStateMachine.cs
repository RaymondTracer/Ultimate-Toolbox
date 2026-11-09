namespace UltimateToolbox.BlankScreen;

public enum BlankScreenAction
{
    None,
    ShowAutomatic,
    ShowManual,
    HideForAudio,
    HideForActivity,
    HideForUnavailableActivity,
    HideForUnavailableAudio,
    ManualBlockedByAudio,
    ManualBlockedByUnavailableActivity
}

public sealed class BlankScreenRuntimeState
{
    public bool OverlayVisible { get; set; }

    public DateTimeOffset? OverlayHoldUntil { get; set; }

    public DateTimeOffset? LastAudioActivityAt { get; set; }

    public BlankScreenRuntimeState Copy() => new()
    {
        OverlayVisible = OverlayVisible,
        OverlayHoldUntil = OverlayHoldUntil,
        LastAudioActivityAt = LastAudioActivityAt
    };
}

public sealed record BlankScreenDecision(
    BlankScreenAction Action,
    BlankScreenRuntimeState NextState,
    TimeSpan EffectiveIdle,
    string? Reason);

/// <summary>Owns automatic/manual blanking transitions and the audio idle reset.</summary>
public static class BlankScreenStateMachine
{
    public static readonly TimeSpan OverlayDismissThreshold = TimeSpan.FromMilliseconds(200);
    public static readonly TimeSpan AutomaticOverlayHold = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan ManualOverlayHold = TimeSpan.FromMilliseconds(1200);

    public static BlankScreenDecision Evaluate(
        BlankScreenRuntimeState? currentState,
        TimeSpan? observedIdle,
        bool audioAvailable,
        bool audioActive,
        bool manualBlankRequested,
        DateTimeOffset now,
        TimeSpan inactivityThreshold)
    {
        var state = currentState?.Copy() ?? new BlankScreenRuntimeState();
        if (inactivityThreshold <= TimeSpan.Zero)
        {
            inactivityThreshold = TimeSpan.FromMinutes(10);
        }

        var idleAvailable = observedIdle is { } idle && idle >= TimeSpan.Zero;
        var currentIdle = idleAvailable ? observedIdle!.Value : TimeSpan.Zero;

        if (audioActive)
        {
            state.LastAudioActivityAt = now;
            currentIdle = TimeSpan.Zero;
        }
        else if (idleAvailable && state.LastAudioActivityAt is { } lastAudioActivity)
        {
            var sinceAudio = now - lastAudioActivity;
            if (sinceAudio < TimeSpan.Zero)
            {
                sinceAudio = TimeSpan.Zero;
            }

            if (currentIdle <= sinceAudio)
            {
                state.LastAudioActivityAt = null;
            }
            else
            {
                currentIdle = sinceAudio;
            }
        }

        if (manualBlankRequested && !state.OverlayVisible)
        {
            if (audioActive)
            {
                return new(BlankScreenAction.ManualBlockedByAudio, state, currentIdle, "Audio is active.");
            }

            if (!idleAvailable)
            {
                return new(
                    BlankScreenAction.ManualBlockedByUnavailableActivity,
                    state,
                    currentIdle,
                    "Input activity cannot be monitored on this desktop session.");
            }

            if (!state.OverlayVisible)
            {
                state.OverlayVisible = true;
                state.OverlayHoldUntil = now + ManualOverlayHold;
                return new(BlankScreenAction.ShowManual, state, currentIdle, "Manual blank requested.");
            }
        }

        if (state.OverlayVisible)
        {
            if (audioActive)
            {
                state.OverlayVisible = false;
                state.OverlayHoldUntil = null;
                return new(BlankScreenAction.HideForAudio, state, currentIdle, "Audio started while the screen was blanked.");
            }

            if (!idleAvailable)
            {
                state.OverlayVisible = false;
                state.OverlayHoldUntil = null;
                return new(
                    BlankScreenAction.HideForUnavailableActivity,
                    state,
                    currentIdle,
                    "Input activity monitoring became unavailable.");
            }

            if (!audioAvailable)
            {
                state.OverlayVisible = false;
                state.OverlayHoldUntil = null;
                return new(
                    BlankScreenAction.HideForUnavailableAudio,
                    state,
                    currentIdle,
                    "Audio monitoring became unavailable.");
            }

            if (state.OverlayHoldUntil is { } holdUntil && now < holdUntil)
            {
                return new(BlankScreenAction.None, state, currentIdle, "The overlay wake hold is active.");
            }

            state.OverlayHoldUntil = null;
            if (currentIdle <= OverlayDismissThreshold)
            {
                state.OverlayVisible = false;
                return new(BlankScreenAction.HideForActivity, state, currentIdle, "User input resumed.");
            }

            return new(BlankScreenAction.None, state, currentIdle, null);
        }

        if (!idleAvailable)
        {
            return new(
                manualBlankRequested
                    ? BlankScreenAction.ManualBlockedByUnavailableActivity
                    : BlankScreenAction.None,
                state,
                currentIdle,
                "Input activity cannot be monitored on this desktop session.");
        }

        if (audioAvailable && !audioActive && currentIdle >= inactivityThreshold)
        {
            state.OverlayVisible = true;
            state.OverlayHoldUntil = now + AutomaticOverlayHold;
            return new(BlankScreenAction.ShowAutomatic, state, currentIdle, "The inactivity timeout elapsed.");
        }

        return new(
            BlankScreenAction.None,
            state,
            currentIdle,
            audioAvailable ? null : "Audio monitoring is unavailable; automatic blanking is paused.");
    }
}
