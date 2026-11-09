using Avalonia.Controls;

namespace UltimateToolbox.Contracts;

/// <summary>
/// Implement this contract in one independently loadable feature assembly.
/// The host creates one instance and keeps it running for the application lifetime.
/// </summary>
public interface IToolboxFeature
{
    FeatureMetadata Metadata { get; }

    Task StartAsync(IFeatureContext context, CancellationToken cancellationToken);

    Control CreateView(IFeatureContext context);

    Task StopAsync(CancellationToken cancellationToken);
}
