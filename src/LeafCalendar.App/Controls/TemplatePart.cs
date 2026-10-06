using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Controls;

/// <summary>Finds a named part of a stock control's template (its visual tree is the only way in from outside).</summary>
internal static class TemplatePart
{
    /// <summary>The first element under <paramref name="root"/> named <paramref name="name"/>, depth first, or null.</summary>
    public static T? Find<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && found.Name == name)
            {
                return found;
            }

            if (Find<T>(child, name) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }
}
