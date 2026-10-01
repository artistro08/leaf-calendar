using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// A <see cref="TimePicker"/> that shrinks to its column (the editor's date and time share a row).
/// </summary>
/// <remarks>
/// The stock template gives its inner button a 242 DIP minimum width as a StaticResource that XamlControlsResources
/// defines itself, so an app-level override never reaches it: in the editor's 132 DIP column the picker was drawn
/// 242 wide and clipped, cutting off the minutes and AM/PM. The minimum is cleared on the template's own button
/// (set through the dependency property, so nothing from the visual tree is cast).
/// </remarks>
public sealed partial class LeafTimePicker : TimePicker
{
    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        GetTemplateChild("FlyoutButton")?.SetValue(MinWidthProperty, 0d);
    }
}
