using Avalonia.Controls;

namespace UltimateToolbox.Contracts;

/// <summary>Pixel bounds and identity for one connected display.</summary>
public sealed record FeatureDisplay(
    string Id,
    string? Name,
    int X,
    int Y,
    int Width,
    int Height,
    double Scaling,
    bool IsPrimary);

/// <summary>Host-owned display enumeration and input-transparent overlay windows.</summary>
public interface IDisplayOverlayService : IDisposable
{
    event EventHandler? DisplaysChanged;

    /// <remarks>Call on Avalonia's UI thread.</remarks>
    IReadOnlyList<FeatureDisplay> GetDisplays();

    /// <remarks>Call on Avalonia's UI thread. The returned handle closes the overlay when disposed.</remarks>
    IDisplayOverlayHandle CreateFullScreenOverlay(FeatureDisplay display, Control content, bool topmost = true);

    /// <summary>Temporarily hides the pointer on the active display backend.</summary>
    /// <remarks>Call on Avalonia's UI thread. The returned lease restores pointer visibility when disposed.</remarks>
    IDisposable HideCursor();
}

public interface IDisplayOverlayHandle : IDisposable
{
    FeatureDisplay Display { get; }

    /// <remarks>Call on Avalonia's UI thread.</remarks>
    void SetTopmost(bool topmost);
}
