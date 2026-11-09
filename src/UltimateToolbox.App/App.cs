using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace UltimateToolbox;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var featureDirectory = FeaturePaths.FeatureDirectory;
            var displayOverlays = new AvaloniaDisplayOverlayService(() => desktop.MainWindow);
            var catalog = FeatureCatalog.Load(featureDirectory, displayOverlays);
            desktop.MainWindow = new MainWindow(catalog, featureDirectory);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
