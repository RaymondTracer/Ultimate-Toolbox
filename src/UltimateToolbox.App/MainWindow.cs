using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateToolbox.Contracts;

namespace UltimateToolbox;

internal sealed class MainWindow : Window
{
    private readonly FeatureCatalog _catalog;
    private readonly string _featureDirectory;
    private readonly Border _contentBorder;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isShuttingDown;
    private bool _allowClose;
    private Task _startupTask = Task.CompletedTask;

    public MainWindow(FeatureCatalog catalog, string featureDirectory)
    {
        _catalog = catalog;
        _featureDirectory = featureDirectory;

        Title = "Ultimate Toolbox";
        Width = 1040;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(250)));
        root.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));

        var sidebar = CreateSidebar();
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        _contentBorder = new Border
        {
            Padding = new Thickness(28),
            Background = new SolidColorBrush(Color.Parse("#171A21")),
            Child = CreateHomeView()
        };
        Grid.SetColumn(_contentBorder, 1);
        root.Children.Add(_contentBorder);

        Content = root;
        Opened += (_, _) => _startupTask = StartFeaturesAsync();
        Closing += OnClosing;
    }

    private Control CreateSidebar()
    {
        var sidebar = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(18, 22),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        sidebar.Children.Add(new TextBlock
        {
            Text = "Ultimate Toolbox",
            FontSize = 21,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        sidebar.Children.Add(new TextBlock
        {
            Text = "FEATURES",
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.Parse("#9AA2B1")),
            Margin = new Thickness(0, 18, 0, 0)
        });

        var homeButton = new Button
        {
            Content = "Overview",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        homeButton.Click += (_, _) => _contentBorder.Child = CreateHomeView();
        sidebar.Children.Add(homeButton);

        if (_catalog.Features.Count == 0 && _catalog.Issues.Count == 0)
        {
            sidebar.Children.Add(new TextBlock
            {
                Text = "No feature modules installed",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9AA2B1")),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }

        foreach (var feature in _catalog.Features)
        {
            if (!feature.IsSupported)
            {
                sidebar.Children.Add(new TextBlock
                {
                    Text = $"{feature.Metadata.Name} · unavailable on this OS",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.Parse("#9AA2B1")),
                    Margin = new Thickness(8, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            var button = new Button
            {
                Content = feature.Metadata.Name,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            button.Click += (_, _) => ShowFeature(feature);
            sidebar.Children.Add(button);
        }

        return sidebar;
    }

    private Control CreateHomeView()
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 760 };
        panel.Children.Add(new TextBlock
        {
            Text = _catalog.Features.Count == 0 ? "No features installed" : "Your features",
            FontSize = 30,
            FontWeight = FontWeight.SemiBold
        });

        panel.Children.Add(new TextBlock
        {
            Text = "The host is ready. Features are loaded from separate .NET modules, so this base installation contains no utilities.",
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.Parse("#C3C9D4")),
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Copy a built feature module ending in .tbfeature.dll into this folder, then restart the app:\n{_featureDirectory}",
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = $"Host platform: {GetPlatformName()}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.Parse("#9AA2B1")),
            Margin = new Thickness(0, 4, 0, 0)
        });

        foreach (var issue in _catalog.Issues)
        {
            panel.Children.Add(new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse("#3B2828")),
                Child = new TextBlock
                {
                    Text = $"Could not load {Path.GetFileName(issue.AssemblyPath)}: {issue.Message}",
                    TextWrapping = TextWrapping.Wrap
                }
            });
        }

        return new ScrollViewer { Content = panel };
    }

    private void ShowFeature(LoadedFeature feature)
    {
        if (!feature.IsStarted)
        {
            _contentBorder.Child = new TextBlock
            {
                Text = feature.RuntimeError is null
                    ? $"{feature.Metadata.Name} is starting."
                    : $"{feature.Metadata.Name} failed to start: {feature.RuntimeError}",
                TextWrapping = TextWrapping.Wrap
            };
            return;
        }

        try
        {
            var panel = new StackPanel { Spacing = 16 };
            panel.Children.Add(new TextBlock
            {
                Text = feature.Metadata.Name,
                FontSize = 28,
                FontWeight = FontWeight.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = feature.Metadata.Description,
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.Parse("#C3C9D4")),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(feature.Module.CreateView(feature.Context));
            _contentBorder.Child = new ScrollViewer { Content = panel };
        }
        catch (Exception exception)
        {
            feature.RuntimeError = exception.GetBaseException().Message;
            feature.Context.Logger.Error("Could not create the feature view.", exception);
            _contentBorder.Child = new TextBlock
            {
                Text = $"Could not open {feature.Metadata.Name}: {feature.RuntimeError}",
                TextWrapping = TextWrapping.Wrap
            };
        }
    }

    private async Task StartFeaturesAsync()
    {
        foreach (var feature in _catalog.Features.Where(feature => feature.IsSupported))
        {
            try
            {
                await feature.Module.StartAsync(feature.Context, _lifetime.Token);
                feature.IsStarted = true;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                feature.RuntimeError = exception.GetBaseException().Message;
                feature.Context.Logger.Error("Could not start the feature.", exception);
            }
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _lifetime.Cancel();

        await _startupTask;

        foreach (var feature in _catalog.Features.Reverse().Where(feature => feature.IsStarted))
        {
            try
            {
                await feature.Module.StopAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                feature.Context.Logger.Error("Could not stop the feature cleanly.", exception);
            }
        }

        _catalog.Dispose();
        _lifetime.Dispose();
        _allowClose = true;
        Close();
    }

    private static string GetPlatformName() => OperatingSystem.IsWindows()
        ? "Windows"
        : OperatingSystem.IsLinux()
            ? "Linux"
            : "Unsupported platform";
}
