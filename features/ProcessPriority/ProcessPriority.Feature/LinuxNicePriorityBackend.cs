using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UltimateToolbox.ProcessPriority;

namespace UltimateToolbox.ProcessPriority.Feature;

internal sealed class LinuxNicePriorityBackend : IProcessPriorityBackend
{
    private const int PrioProcess = 0;
    private const int ErrorPermission = 1;
    private const int ErrorNoSuchProcess = 3;
    private const int ErrorAccess = 13;

    public PriorityEnforcementResult Apply(Process process, ProcessPriorityRule rule)
    {
        var target = Math.Clamp(rule.LinuxNiceValue, -20, 19);
        int[] threadIds;

        try
        {
            process.Refresh();
            threadIds = process.Threads
                .Cast<ProcessThread>()
                .Select(thread => thread.Id)
                .Where(threadId => threadId > 0)
                .Distinct()
                .ToArray();
        }
        catch (InvalidOperationException exception)
        {
            return new(PriorityResultKind.ProcessExited, exception.Message);
        }
        catch (Exception exception)
        {
            return new(PriorityResultKind.Failed, $"Could not enumerate threads: {exception.GetType().Name}: {exception.Message}");
        }

        if (threadIds.Length == 0)
        {
            return new(PriorityResultKind.ProcessExited, "no live threads were found");
        }

        var changed = 0;
        var denied = 0;
        var failed = 0;
        var exited = 0;
        string? lastError = null;

        foreach (var threadId in threadIds)
        {
            var nativeThreadId = checked((uint)threadId);
            int current;
            try
            {
                current = GetNiceValue(nativeThreadId);
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNoSuchProcess)
            {
                exited++;
                continue;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is ErrorPermission or ErrorAccess)
            {
                denied++;
                lastError = $"TID {threadId}: {exception.Message}";
                continue;
            }
            catch (Exception exception)
            {
                failed++;
                lastError = $"TID {threadId}: {exception.GetType().Name}: {exception.Message}";
                continue;
            }

            if (current == target)
            {
                continue;
            }

            try
            {
                if (SetNiceValue(nativeThreadId, target) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    lastError = $"TID {threadId}: {new Win32Exception(error).Message}";
                    if (error == ErrorNoSuchProcess)
                    {
                        exited++;
                    }
                    else if (error is ErrorPermission or ErrorAccess)
                    {
                        denied++;
                    }
                    else
                    {
                        failed++;
                    }

                    continue;
                }

                var actual = GetNiceValue(nativeThreadId);
                if (actual == target)
                {
                    changed++;
                }
                else
                {
                    failed++;
                    lastError = $"TID {threadId}: requested nice {target}, but read back {actual}";
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorNoSuchProcess)
            {
                exited++;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is ErrorPermission or ErrorAccess)
            {
                denied++;
                lastError = $"TID {threadId}: {exception.Message}";
            }
            catch (Exception exception)
            {
                failed++;
                lastError = $"TID {threadId}: {exception.GetType().Name}: {exception.Message}";
            }
        }

        if (denied > 0)
        {
            return new(PriorityResultKind.PermissionDenied, $"denied for {denied} thread(s); {lastError}");
        }

        if (failed > 0)
        {
            return new(PriorityResultKind.Failed, $"failed for {failed} thread(s); {lastError}");
        }

        if (changed > 0)
        {
            return new(PriorityResultKind.Applied, $"set nice to {target} for {changed} thread(s)");
        }

        return exited == threadIds.Length
            ? new(PriorityResultKind.ProcessExited, "all enumerated threads exited during the update")
            : new(PriorityResultKind.AlreadyCorrect);
    }

    private static int GetNiceValue(uint threadId)
    {
        Marshal.WriteInt32(ErrnoLocation(), 0);
        var value = GetPriority(PrioProcess, threadId);
        var error = Marshal.ReadInt32(ErrnoLocation());
        if (value == -1 && error != 0)
        {
            throw new Win32Exception(error);
        }

        return value;
    }

    private static int SetNiceValue(uint threadId, int value) =>
        SetPriority(PrioProcess, threadId, value);

    [DllImport("libc", EntryPoint = "getpriority", SetLastError = false)]
    private static extern int GetPriority(int which, uint who);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, uint who, int priority);

    [DllImport("libc", EntryPoint = "__errno_location", SetLastError = false)]
    private static extern IntPtr ErrnoLocation();
}
