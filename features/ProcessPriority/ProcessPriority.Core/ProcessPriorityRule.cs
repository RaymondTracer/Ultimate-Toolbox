namespace UltimateToolbox.ProcessPriority;

/// <summary>The safe Windows process priority classes offered by the feature.</summary>
public enum WindowsPriorityLevel
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High
}

/// <summary>A platform-specific priority target for one exact process name.</summary>
public sealed class ProcessPriorityRule
{
    public string ProcessName { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public WindowsPriorityLevel WindowsPriority { get; set; } = WindowsPriorityLevel.Normal;

    /// <summary>Linux nice value: -20 is highest and 19 is lowest.</summary>
    public int LinuxNiceValue { get; set; }

    public ProcessPriorityRule Copy() => new()
    {
        ProcessName = ProcessName,
        Enabled = Enabled,
        WindowsPriority = WindowsPriority,
        LinuxNiceValue = LinuxNiceValue
    };
}
