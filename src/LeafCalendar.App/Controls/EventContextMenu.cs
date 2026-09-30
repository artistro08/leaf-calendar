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
            menu.Items.Add(Item("Going", "MenuRsvpYes", 0xE8FB, () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Accepted, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(Item("Maybe", "MenuRsvpMaybe", 0xE9CE, () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Tentative, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(Item("Not going", "MenuRsvpNo", 0xE711, () => vm.Fire(() => vm.RespondAsync(ResponseStatus.Declined, null, emailOrganizer: true), "menu.respond.failed")));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(Item("Copy", "MenuCopy", 0xE8C8, () => vm.CopySelection()));

        // Changes (several events: each one you can't change is skipped)
        if (single is null || single.CanEdit)
        {
            menu.Items.Add(Item("Cut", "MenuCut", 0xE8C6, () => vm.Fire(vm.CutSelectionAsync, "menu.cut.failed")));

            var colors = new MenuFlyoutSubItem { Text = "Color", Icon = Glyph(0xE790) };
            AutomationProperties.SetAutomationId(colors, "MenuColor");
            colors.Items.Add(Item("Calendar color", "MenuColor_Calendar", Swatch(EventColors.ResolveAccent(null, occurrence.CalendarColor)), () => vm.Fire(() => vm.RecolorAsync(null), "menu.recolor.failed")));
            foreach (var (id, name) in EventColors.EventColorNames)
            {
                colors.Items.Add(Item(name, $"MenuColor_{id}", Swatch(EventColors.ResolveAccent(id, occurrence.CalendarColor)), () => vm.Fire(() => vm.RecolorAsync(id), "menu.recolor.failed")));
            }

            menu.Items.Add(colors);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Delete", "MenuDelete", 0xE74D, () => vm.Fire(() => vm.DeleteAsync([.. vm.Selection], sendUpdates: true), "menu.delete.failed")));
        }

        menu.ShowAt(target, position);
    }

    static MenuFlyoutItem Item(string text, string automationId, int glyph, Action click) => Item(text, automationId, Glyph(glyph), click);

    static MenuFlyoutItem Item(string text, string automationId, IconElement icon, Action click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = icon };
        AutomationProperties.SetAutomationId(item, automationId);
        item.Click += (_, _) => click();
        return item;
    }

    // A 16 DIP Segoe Fluent glyph (the design standard's menu icon)
    static FontIcon Glyph(int glyph) => new() { Glyph = char.ConvertFromUtf32(glyph), FontSize = 16 };

    // A filled circle in the color (named by the item's text, so it's never color alone)
    static FontIcon Swatch(string hex)
    {
        var icon = Glyph(0xE91F);
        icon.Foreground = LeafBrushes.FromHex(hex);
        return icon;
    }
}
