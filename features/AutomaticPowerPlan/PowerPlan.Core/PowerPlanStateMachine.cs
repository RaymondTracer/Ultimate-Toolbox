namespace UltimateToolbox.PowerPlan;

public enum PowerPlanAction
{
    Idle,
    Activated,
    Active,
    Reapplied,
    GraceStarted,
    WaitingForGrace,
    ActivityResumed,
    Restored,
    BaselineUnavailable
}

public sealed class PowerPlanRuntimeState
{
    public bool SwitchInProgress { get; set; }

    public string? RestoreProfileId { get; set; }

    public DateTimeOffset? LastActivityAt { get; set; }

    public bool GracePeriodActive { get; set; }

    public PowerPlanRuntimeState Copy() => new()
    {
        SwitchInProgress = SwitchInProgress,
        RestoreProfileId = RestoreProfileId,
        LastActivityAt = LastActivityAt,
        GracePeriodActive = GracePeriodActive
    };
}

public sealed record PowerPlanDecision(
    PowerPlanAction Action,
    PowerPlanRuntimeState NextState,
    string? RequestedProfileId,
    string? Detail);

/// <summary>Owns capture, grace, reapply, and restoration transitions for profile switching.</summary>
public static class PowerPlanStateMachine
{
    public static PowerPlanDecision Evaluate(
        PowerPlanRuntimeState? currentState,
        bool watchedActivityActive,
        string? currentProfileId,
        string? targetProfileId,
        DateTimeOffset now,
        TimeSpan gracePeriod)
    {
        var state = currentState?.Copy() ?? new PowerPlanRuntimeState();
        if (watchedActivityActive && string.IsNullOrWhiteSpace(targetProfileId))
        {
            return new(PowerPlanAction.Idle, state, null, "No target profile is configured.");
        }

        if (watchedActivityActive)
        {
            if (!state.SwitchInProgress || string.IsNullOrWhiteSpace(state.RestoreProfileId))
            {
                if (string.IsNullOrWhiteSpace(currentProfileId))
                {
                    return new(PowerPlanAction.BaselineUnavailable, state, null, "The current profile could not be read.");
                }

                return new(
                    PowerPlanAction.Activated,
                    new PowerPlanRuntimeState
                    {
                        SwitchInProgress = true,
                        RestoreProfileId = currentProfileId,
                        LastActivityAt = now,
                        GracePeriodActive = false
                    },
                    targetProfileId,
                    $"Saved {currentProfileId} for restoration.");
            }

            var resumed = state.GracePeriodActive;
            state.LastActivityAt = now;
            state.GracePeriodActive = false;
            var isTargetActive = string.Equals(currentProfileId, targetProfileId, StringComparison.OrdinalIgnoreCase);
            if (!isTargetActive)
            {
                return new(PowerPlanAction.Reapplied, state, targetProfileId, "Watched activity remains active.");
            }

            return resumed
                ? new(PowerPlanAction.ActivityResumed, state, null, "Activity returned during the grace period.")
                : new(PowerPlanAction.Active, state, null, "Watched activity remains active.");
        }

        if (!state.SwitchInProgress || string.IsNullOrWhiteSpace(state.RestoreProfileId))
        {
            return new(PowerPlanAction.Idle, new PowerPlanRuntimeState(), null, "No profile change is owned by the feature.");
        }

        var lastActivity = state.LastActivityAt ?? now;
        var elapsed = now - lastActivity;
        if (elapsed >= (gracePeriod < TimeSpan.Zero ? TimeSpan.Zero : gracePeriod))
        {
            var restoreProfileId = state.RestoreProfileId;
            return new(
                PowerPlanAction.Restored,
                new PowerPlanRuntimeState(),
                restoreProfileId,
                "The grace period elapsed.");
        }

        var firstGracePoll = !state.GracePeriodActive;
        state.GracePeriodActive = true;
        return new(
            firstGracePoll ? PowerPlanAction.GraceStarted : PowerPlanAction.WaitingForGrace,
            state,
            null,
            "Waiting for the configured grace period to elapse.");
    }

    public static PowerPlanDecision RestoreNow(PowerPlanRuntimeState? currentState)
    {
        var state = currentState?.Copy() ?? new PowerPlanRuntimeState();
        if (!state.SwitchInProgress || string.IsNullOrWhiteSpace(state.RestoreProfileId))
        {
            return new(PowerPlanAction.Idle, new PowerPlanRuntimeState(), null, "No profile change is owned by the feature.");
        }

        return new(
            PowerPlanAction.Restored,
            new PowerPlanRuntimeState(),
            state.RestoreProfileId,
            "The feature was disabled or stopped.");
    }
}
