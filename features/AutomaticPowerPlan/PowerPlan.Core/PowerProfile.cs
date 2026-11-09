namespace UltimateToolbox.PowerPlan;

public sealed record PowerProfile(string Id, string Name, string Description)
{
    public override string ToString() => Name;
}
