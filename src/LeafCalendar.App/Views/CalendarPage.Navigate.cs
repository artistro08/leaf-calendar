// Track A (Milestone 5 Tasks 3-5) owns this file: the command menu, the cheat sheet, time travel, and interface scale.
// Empty hooks until the owning track fills them in; the owner deletes this line once every method uses the page
#pragma warning disable CA1822 // Mark members as static

using System.Globalization;
using LeafCalendar.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LeafCalendar.App.Views;

public sealed partial class CalendarPage
{
    // The command menu, built on first use and kept for the page's life
    Flyout? _commandFlyout;
    CommandMenu? _commandMenu;

    /// <summary>The mini month's "Next month" button (the title bar centers its search icon over it).</summary>
    public FrameworkElement? MiniMonthNextButton => Sidebar.MiniMonthNextButton;

    /// <summary>
    /// The Next month button's center, in window DIPs, once the open sidebar has settled; null before it's laid out.
    /// </summary>
    /// <remarks>Measured against the sidebar itself (which settles at the window's left edge), so a pane still sliding in doesn't move it.</remarks>
    public double? MiniMonthNextCenterX =>
        Sidebar.MiniMonthNextButton is { ActualWidth: > 0 } next
            ? next.TransformToVisual(Sidebar).TransformPoint(new Windows.Foundation.Point(next.ActualWidth / 2, 0)).X
            : null;

    /// <summary>
    /// With the sidebar closed, starts the period title after <paramref name="right"/> (the title bar search icon's right
    /// edge, in window DIPs), so the icon never covers it. With the sidebar open the title keeps its place.
    /// </summary>
    public void KeepTitleClearOf(double right)
    {
        if (IsSidebarOpen)
        {
            return;
        }

        // The search glyph's ink ends 8 in from its button's edge; the title starts the usual inset after it
        var left = Math.Max(PaneToggleClearance + TitleInset, right - 8 + TitleInset);
        PeriodTitle.Margin = new Thickness(left, PeriodTitle.Margin.Top, 0, PeriodTitle.Margin.Bottom);
    }

    // Called once when the page opens
    void AttachNavigate()
    {
    }

    // Called from Detach: the menu holds the long-lived view model, so it goes with the page
    void DetachNavigate()
    {
        _commandFlyout?.Hide();
        _commandFlyout = null;
        _commandMenu   = null;
    }

    // =========================================================================
    // COMMAND MENU
    // =========================================================================

    // Ctrl+K, Ctrl+F, /, and the title bar's search icon: the menu opens empty, under the title bar, with focus in its box
    void OpenCommandMenu()
    {
        if (_commandFlyout is null || _commandMenu is null)
        {
            var menu   = new CommandMenu(ViewModel, RunCommandRow);
            var style  = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600d));

            var flyout = new Flyout { Content = menu, FlyoutPresenterStyle = style };
            flyout.Opened += (_, _) => menu.FocusBox();

            (_commandFlyout, _commandMenu) = (flyout, menu);
        }

        _commandMenu.Reset();
        _commandFlyout.ShowAt(Root, new FlyoutShowOptions
        {
            Position  = new Windows.Foundation.Point(Root.ActualWidth / 2, 56),
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode  = FlyoutShowMode.Standard,
        });
    }

    // Runs the picked row (the menu closes first, so focus is back on the calendar for what the row opens)
    void RunCommandRow(CommandRow row, bool jump)
    {
        _commandFlyout?.Hide();

        switch (row.Kind)
        {
            case CommandRowKind.Event when row.Hit is { } hit:
                ViewModel.OpenSearchHit(hit, jump);
                break;

            case CommandRowKind.Date when row.Date is { } date:
                ViewModel.NavigateTo(date);
                break;

            case CommandRowKind.Action when row.Item is { } item:
                if (item.Command != Core.Views.CalendarCommand.None)
                {
                    RunCommand(item.Command, item.Days);
                }
                else
                {
                    RunAction(item.Id);
                }

                break;
        }
    }

    // Actions with no shortcut, by ID; an unknown ID does nothing
    void RunAction(string id)
    {
        var vm = ViewModel;
        switch (id)
        {
            case "toggle-week-numbers":
                vm.Update(s => s with { ShowWeekNumbers = !s.ShowWeekNumbers });
                return;

            case "toggle-24-hour":
                vm.Update(s => s with { Use24HourTime = !s.Use24HourTime });
                return;

            case "toggle-working-hours":
                vm.Update(s => s with { WorkingHours = s.WorkingHours with { Enabled = !s.WorkingHours.Enabled } });
                return;

            case "sync":
                vm.Fire(vm.RefreshAsync);
                return;
        }

        // Interface Scale
        if (id.StartsWith("scale-", StringComparison.Ordinal) && int.TryParse(id[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var percent))
        {
            vm.Update(s => s with { InterfaceScale = percent / 100.0 });
            return;
        }

        // Settings Pages
        SettingsSection? section = id switch
        {
            "settings-general"       => SettingsSection.General,
            "settings-calendars"     => SettingsSection.Calendars,
            "settings-time-zones"    => SettingsSection.TimeZones,
            "settings-notifications" => SettingsSection.Notifications,
            "settings-tray"          => SettingsSection.Tray,
            "settings-shortcuts"     => SettingsSection.Shortcuts,
            "settings-accounts"      => SettingsSection.Accounts,
            "settings-about"         => SettingsSection.About,
            _                        => null,
        };
        if (section is { } page)
        {
            vm.OpenSettings?.Invoke(page);
        }
    }

    // ? (Task 4)
    void ShowShortcutSheet()
    {
    }

    // Z (Task 5)
    void StartTimeTravel()
    {
    }
}
