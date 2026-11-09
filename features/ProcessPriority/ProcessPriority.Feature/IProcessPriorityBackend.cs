using System.Diagnostics;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

internal interface IProcessPriorityBackend
{
    PriorityEnforcementResult Apply(Process process, ProcessPriorityRule rule);
}
