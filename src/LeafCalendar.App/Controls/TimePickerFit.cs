using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Makes a stock <see cref="TimePicker"/> shrink to its column (the editor's date and time share a row).
/// </summary>
/// <remarks>
/// The stock template gives its inner button a 242 DIP minimum width as a StaticResource that XamlControlsResources
/// defines itself, so an app-level override never reaches it: in the editor's 132 DIP column the picker was drawn
/// 242 wide and clipped, cutting off the minutes and AM/PM. The minimum is cleared on the template's own button once
/// the template is built (found by name and set through the dependency property, so nothing from the visual tree is
/// cast).
///
/// This used to be a <c>TimePicker</c> subclass. That crashed the Native AOT build: WinUI destroys a released picker
/// later, from its own queue, and on the way out the picker detaches its Loaded handler from itself, which for a
/// subclass asks the .NET object for <c>IFrameworkElement</c>. By then .NET had already collected that object, so the
/// process ended (a <c>NullReferenceException</c> in <c>ComWrappers.ManagedObjectWrapper.QueryInterface</c> under
/// <c>DirectUI::TimePicker::~TimePicker</c>). A stock picker answers that question itself, so never subclass it.
/// </remarks>
public static class TimePickerFit
{
    /// <summary>Clears <paramref name="picker"/>'s inner minimum width whenever it's laid out, and returns it.</summary>
    public static TimePicker Shrink(TimePicker picker)
    {
        picker.SizeChanged += (_, _) => ClearButtonMinimum(picker);
        return picker;
    }

    // Finds The Template's Flyout Button And Clears Its Minimum Width (true once found)
    private static bool ClearButtonMinimum(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child.GetValue(FrameworkElement.NameProperty) is "FlyoutButton")
            {
                child.SetValue(FrameworkElement.MinWidthProperty, 0d);
                return true;
            }

            if (ClearButtonMinimum(child))
            {
                return true;
            }
        }

        return false;
    }
}
