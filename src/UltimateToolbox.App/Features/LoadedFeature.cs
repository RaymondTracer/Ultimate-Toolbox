using System.Runtime.Loader;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class LoadedFeature(
    string assemblyPath,
    IToolboxFeature module,
    IFeatureContext context,
    bool isSupported,
    AssemblyLoadContext loadContext)
{
    public string AssemblyPath { get; } = assemblyPath;

    public IToolboxFeature Module { get; } = module;

    public IFeatureContext Context { get; } = context;

    public FeatureMetadata Metadata => Module.Metadata;

    public bool IsSupported { get; } = isSupported;

    public bool IsStarted { get; set; }

    public string? RuntimeError { get; set; }

    public AssemblyLoadContext LoadContext { get; } = loadContext;
}
