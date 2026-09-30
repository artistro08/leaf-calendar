using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Events;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Right-click menu for events (spec 7.3 and 7.4): reply, copy, cut, color, delete. It acts on the whole selection
/// when the clicked event is part of it; otherwise it selects just that event first. Items are built in code and
/// their clicks capture the view model, so nothing is read back from a menu item.
/// </summary>
public static class EventContextMenu
{
    /// <summary>Opens the menu at <paramref name="position"/> within <paramref name="target"/>.</summary>
    public static void Show(UIElement target, Point position, CalendarViewModel vm, CalendarOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (!vm.IsSelected(occurrence))
        {
            vm.Select(occurrence);
        }

        var single = vm.Selection.Count == 1 ? vm.SelectedInfo : null;
        var menu   = new MenuFlyout();

        // Reply (one invite)
        if (single is { CanRespond: true })
        {
            menu.Items.Add(Item("Going", "MenuRsvpYes", () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Accepted, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(Item("Maybe", "MenuRsvpMaybe", () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Tentative, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(Item("Not going", "MenuRsvpNo", () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Declined, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Item("Copy", "MenuCopy", vm.CopySelection));

        // Changes (several events: each one you can't change is skipped)
        if (single is null || single.CanEdit)
        {
            menu.Items.Add(Item("Cut", "MenuCut", () => vm.Fire(vm.CutSelectionAsync, "menu.cut.failed")));

            var colors = new MenuFlyoutSubItem { Text = "Color" };
            AutomationProperties.SetAutomationId(colors, "MenuColor");
            colors.Items.Add(Item("Calendar color", "MenuColor_Calendar", () => vm.Fire(() => vm.RecolorAsync(null), "menu.recolor.failed")));
            foreach (var (id, name) in EventColors.EventColorNames)
            {
                colors.Items.Add(Item(name, $"MenuColor_{id}", () => vm.Fire(() => vm.RecolorAsync(id), "menu.recolor.failed")));
            }

            menu.Items.Add(colors);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Delete", "MenuDelete", () => vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "menu.delete.failed")));
        }

        menu.ShowAt(target, position);
    }

    static MenuFlyoutItem Item(string text, string automationId, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => click();
        return item;
    }
}
