using LeafCalendar.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>Asks which events of a repeating series a change applies to (spec 7.3), like Google Calendar.</summary>
public static class ScopeDialog
{
    /// <summary>
    /// Shows the question; null when canceled. <paramref name="includeFollowing"/> offers "This and following events"
    /// (replies don't), and <paramref name="includeThis"/> offers "This event" (a repeat change doesn't).
    /// </summary>
    public static async Task<EditScope?> AskAsync(FrameworkElement owner, bool includeFollowing, bool includeThis = true, string title = "Change repeating event")
    {
        var thisOne = new RadioButton { Content = "This event", GroupName = "Scope", IsChecked = includeThis, Visibility = includeThis ? Visibility.Visible : Visibility.Collapsed };
        var following = new RadioButton { Content = "This and following events", GroupName = "Scope", IsChecked = !includeThis && includeFollowing, Visibility = includeFollowing ? Visibility.Visible : Visibility.Collapsed };
        var all = new RadioButton { Content = "All events", GroupName = "Scope", IsChecked = !includeThis && !includeFollowing };
        AutomationProperties.SetAutomationId(thisOne, "ScopeThis");
        AutomationProperties.SetAutomationId(following, "ScopeFollowing");
        AutomationProperties.SetAutomationId(all, "ScopeAll");

        var choices = new StackPanel { Spacing = 4 };
        choices.Children.Add(thisOne);
        choices.Children.Add(following);
        choices.Children.Add(all);

        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = title,
            Content = choices,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        // The radio buttons are this method's own references (never read back through the dialog)
        return following.IsChecked == true ? EditScope.Following : all.IsChecked == true ? EditScope.All : EditScope.This;
    }
}
