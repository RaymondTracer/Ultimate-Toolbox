namespace UltimateToolbox.ProcessPriority.Feature;

internal enum PriorityResultKind
{
    Applied,
    AlreadyCorrect,
    PermissionDenied,
    ProcessExited,
    Failed
}

internal sealed record PriorityEnforcementResult(PriorityResultKind Kind, string? Detail = null);
