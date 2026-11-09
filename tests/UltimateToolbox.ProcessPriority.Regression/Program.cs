using UltimateToolbox.ProcessPriority;

Equal(
    "Windows executable paths normalize to process names",
    "Zandronum",
    ProcessPriorityPolicy.NormalizeProcessName(" C:\\Games\\Zandronum.exe "));
Equal(
    "Linux executable paths normalize to process names",
    "renderer",
    ProcessPriorityPolicy.NormalizeProcessName("/usr/local/bin/renderer"));
Equal(
    "a process name without a path remains unchanged",
    "zandronum",
    ProcessPriorityPolicy.NormalizeProcessName("zandronum"));
Equal("blank process names are rejected", null, ProcessPriorityPolicy.NormalizeProcessName("  "));
Equal("wildcard rules are rejected", null, ProcessPriorityPolicy.NormalizeProcessName("game*"));
True(
    "matching ignores case and an optional .exe extension",
    ProcessPriorityPolicy.Matches("ZANDRONUM.EXE", "zandronum"));
True(
    "matching uses the normalized executable basename",
    ProcessPriorityPolicy.Matches("/opt/game/arena.exe", "ARENA"));
False("matching remains exact", ProcessPriorityPolicy.Matches("arena", "arena-server"));

var normalizedRules = ProcessPriorityPolicy.NormalizeRules(
[
    new ProcessPriorityRule
    {
        ProcessName = "Arena.exe",
        LinuxNiceValue = -30,
        WindowsPriority = WindowsPriorityLevel.High
    },
    new ProcessPriorityRule { ProcessName = "arena", LinuxNiceValue = 30 },
    new ProcessPriorityRule { ProcessName = "bad*name" },
    new ProcessPriorityRule { ProcessName = "invalid-enum", WindowsPriority = (WindowsPriorityLevel)99 },
    null!
]);

Equal("duplicate normalized rules collapse to one entry", 2, normalizedRules.Count);
Equal("Linux nice values clamp to the documented range", -20, normalizedRules[0].LinuxNiceValue);
Equal("invalid Windows priority values normalize safely", WindowsPriorityLevel.Normal, normalizedRules[1].WindowsPriority);
Equal("upper nice bound clamps to 19", 19, ProcessPriorityPolicy.NormalizeRules(
    [new ProcessPriorityRule { ProcessName = "worker", LinuxNiceValue = 25 }])[0].LinuxNiceValue);
Equal("short poll intervals clamp to one second", 1, ProcessPriorityPolicy.NormalizePollInterval(-5));
Equal("long poll intervals clamp to five minutes", 300, ProcessPriorityPolicy.NormalizePollInterval(900));

Console.WriteLine("Process Priority regression checks passed.");
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
