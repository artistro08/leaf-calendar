using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Tray;

/// <summary>
/// Desktop acrylic that stays see-through while its window isn't focused (design standard 2; Sony Control's and Layers'
/// <c>ActiveAcrylicBackdrop</c>).
/// </summary>
/// <remarks>
/// The stock <see cref="DesktopAcrylicBackdrop"/> turns solid whenever its window is inactive, and the flyout's host
/// never really is, so this one always reports input as active. Theme and high contrast follow the defaults.
/// See https://learn.microsoft.com/windows/apps/windows-app-sdk/system-backdrop-controller
/// </remarks>
[SuppressMessage("Design", "CA1001", Justification = "The controller is disposed in OnTargetDisconnected, the backdrop's own teardown.")]
public sealed partial class ActiveAcrylicBackdrop : SystemBackdrop
{
    private readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true };
    private DesktopAcrylicController? _controller;

    /// <inheritdoc />
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // Live Acrylic (releasing any controller a missed disconnect left behind)
        _controller?.Dispose();
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(connectedTarget);
        OnDefaultSystemBackdropConfigurationChanged(connectedTarget, xamlRoot);
    }

    /// <inheritdoc />
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);

        // Close The Target Here, On The UI Thread: XAML drops a disconnected target without closing it, and its last
        // release from the GC finalizer thread fails fast (Layers found this). The flyout's content disconnects every
        // time it closes, so this runs often; FlyoutTests.OpenAndCloseThreeTimes_KeepsWorking covers it on AOT.
        if (disconnectedTarget is IDisposable closable)
        {
            closable.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // Copy theme changes, never the "inactive" state
        var defaults = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        _configuration.Theme = defaults.Theme;
        _configuration.IsHighContrast = defaults.IsHighContrast;
        _configuration.IsInputActive = true;
    }
}
