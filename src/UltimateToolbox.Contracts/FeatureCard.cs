using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UltimateToolbox.Contracts;

/// <summary>A shared settings card for feature module views.</summary>
public sealed class FeatureCard : Border
{
    private readonly StackPanel _content;

    public FeatureCard(string? title = null)
    {
        Padding = new Thickness(15);
        CornerRadius = new CornerRadius(8);
        Background = new SolidColorBrush(Color.Parse("#202631"));
        _content = new StackPanel { Spacing = 9 };

        if (!string.IsNullOrWhiteSpace(title))
        {
            _content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 17,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        Child = _content;
    }

    public void Add(Control control) => _content.Children.Add(control);
}
