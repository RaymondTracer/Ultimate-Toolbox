using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class FeatureCatalog : IDisposable
{
    private static readonly Regex FeatureIdPattern = new("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.Compiled);
    private readonly IDisplayOverlayService _displayOverlays;

    private FeatureCatalog(
        IReadOnlyList<LoadedFeature> features,
        IReadOnlyList<FeatureLoadIssue> issues,
        IDisplayOverlayService displayOverlays)
    {
        Features = features;
        Issues = issues;
        _displayOverlays = displayOverlays;
    }

    public IReadOnlyList<LoadedFeature> Features { get; }

    public IReadOnlyList<FeatureLoadIssue> Issues { get; }

    public static FeatureCatalog Load(string featureDirectory, IDisplayOverlayService displayOverlays)
    {
        ArgumentNullException.ThrowIfNull(displayOverlays);
        Directory.CreateDirectory(featureDirectory);
        var features = new List<LoadedFeature>();
        var issues = new List<FeatureLoadIssue>();
        var featureIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentOperatingSystem = GetCurrentOperatingSystem();

        foreach (var assemblyPath in Directory.EnumerateFiles(
                     featureDirectory,
                     "*.tbfeature.dll",
                     SearchOption.AllDirectories)
                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var loadContext = new FeaturePluginLoadContext(Path.GetFullPath(assemblyPath));

            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
                var moduleTypes = assembly.GetTypes()
                    .Where(type =>
                        type is { IsAbstract: false, IsInterface: false, ContainsGenericParameters: false } &&
                        typeof(IToolboxFeature).IsAssignableFrom(type))
                    .ToArray();

                if (moduleTypes.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"A feature assembly must export exactly one concrete {nameof(IToolboxFeature)} implementation; found {moduleTypes.Length}.");
                }

                if (Activator.CreateInstance(moduleTypes[0]) is not IToolboxFeature module)
                {
                    throw new InvalidOperationException(
                        $"The feature type '{moduleTypes[0].FullName}' needs a public parameterless constructor.");
                }

                ValidateMetadata(module.Metadata);
                if (!featureIds.Add(module.Metadata.Id))
                {
                    throw new InvalidOperationException($"Feature ID '{module.Metadata.Id}' is already in use.");
                }

                var dataDirectory = Path.Combine(FeaturePaths.DataDirectory, "feature-data", module.Metadata.Id);
                var context = new FeatureContext(module.Metadata, dataDirectory, currentOperatingSystem, displayOverlays);
                var isSupported = module.Metadata.SupportedOperatingSystems.Contains(currentOperatingSystem);
                features.Add(new LoadedFeature(assemblyPath, module, context, isSupported, loadContext));
            }
            catch (Exception exception)
            {
                loadContext.Unload();
                issues.Add(new FeatureLoadIssue(assemblyPath, GetUsefulMessage(exception)));
            }
        }

        return new FeatureCatalog(features, issues, displayOverlays);
    }

    public void Dispose()
    {
        foreach (var feature in Features)
        {
            feature.LoadContext.Unload();
        }

        _displayOverlays.Dispose();
    }

    private static void ValidateMetadata(FeatureMetadata? metadata)
    {
        if (metadata is null)
        {
            throw new InvalidOperationException("The feature returned no metadata.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Id) || !FeatureIdPattern.IsMatch(metadata.Id))
        {
            throw new InvalidOperationException("Feature IDs must start with a lowercase letter or digit and contain only lowercase letters, digits, dots, underscores, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Name))
        {
            throw new InvalidOperationException("The feature name is empty.");
        }

        if (metadata.SupportedOperatingSystems is null || metadata.SupportedOperatingSystems.Count == 0)
        {
            throw new InvalidOperationException("The feature must declare at least one supported operating system.");
        }

        if (metadata.SupportedOperatingSystems.Any(platform => platform == FeatureOperatingSystem.Unsupported))
        {
            throw new InvalidOperationException("Unsupported is a host status and cannot be declared as a feature target.");
        }
    }

    private static FeatureOperatingSystem GetCurrentOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return FeatureOperatingSystem.Windows;
        }

        if (OperatingSystem.IsLinux())
        {
            return FeatureOperatingSystem.Linux;
        }

        return FeatureOperatingSystem.Unsupported;
    }

    private static string GetUsefulMessage(Exception exception)
    {
        if (exception is ReflectionTypeLoadException typeLoadException)
        {
            var loaderMessages = typeLoadException.LoaderExceptions
                .Where(loaderException => loaderException is not null)
                .Select(loaderException => loaderException!.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (loaderMessages.Length > 0)
            {
                return string.Join(Environment.NewLine, loaderMessages);
            }
        }

        return exception.GetBaseException().Message;
    }
}
