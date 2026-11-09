using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class FeaturePluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _contractsAssemblyName = typeof(IToolboxFeature).Assembly.GetName().Name!;
    private readonly string _avaloniaAssemblyName = typeof(Control).Assembly.GetName().Name!;

    public FeaturePluginLoadContext(string assemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(assemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(assemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, _contractsAssemblyName, StringComparison.Ordinal) ||
            string.Equals(assemblyName.Name, _avaloniaAssemblyName, StringComparison.Ordinal) ||
            assemblyName.Name?.StartsWith("Avalonia.", StringComparison.Ordinal) == true)
        {
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
