namespace UltimateToolbox.Contracts;

public sealed record FeatureMetadata(
    string Id,
    string Name,
    string Description,
    IReadOnlyCollection<FeatureOperatingSystem> SupportedOperatingSystems);
