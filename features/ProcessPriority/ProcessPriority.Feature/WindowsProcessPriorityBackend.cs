using System.ComponentModel;
using System.Diagnostics;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

internal sealed class WindowsProcessPriorityBackend : IProcessPriorityBackend
{
    public PriorityEnforcementResult Apply(Process process, ProcessPriorityRule rule)
    {
        try
        {
            var target = ToProcessPriorityClass(rule.WindowsPriority);
            var current = process.PriorityClass;
            if (current == target)
            {
                return new(PriorityResultKind.AlreadyCorrect);
            }

            process.PriorityClass = target;
            process.Refresh();
            var actual = process.PriorityClass;
            return actual == target
                ? new(PriorityResultKind.Applied, $"changed from {current} to {target}")
                : new(PriorityResultKind.Failed, $"requested {target}, but the process remained {actual}");
        }
        catch (Exception exception)
        {
            return Classify(exception);
        }
    }

    private static ProcessPriorityClass ToProcessPriorityClass(WindowsPriorityLevel priority) => priority switch
    {
        WindowsPriorityLevel.Idle => ProcessPriorityClass.Idle,
        WindowsPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
        WindowsPriorityLevel.Normal => ProcessPriorityClass.Normal,
        WindowsPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
        WindowsPriorityLevel.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal
    };

    private static PriorityEnforcementResult Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException => new(PriorityResultKind.PermissionDenied, exception.Message),
        Win32Exception { NativeErrorCode: 5 } => new(PriorityResultKind.PermissionDenied, exception.Message),
        InvalidOperationException => new(PriorityResultKind.ProcessExited, exception.Message),
        _ => new(PriorityResultKind.Failed, $"{exception.GetType().Name}: {exception.Message}")
    };
}
