using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The right panel while you share availability: the picked times, one row each (the day, then start and end time
/// pickers and a remove button), in the zone on screen. A change goes straight to the view model, so the grid's slots
/// and the copied text follow it; a time moved onto another merges with it. Built in code from its own references.
/// </summary>
public sealed partial class ShareSlotsPanel : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 8 };
    readonly TextBlock _empty = new() { Text = "No times yet.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };

    /// <summary>Builds the panel (filled by <see cref="Update"/>).</summary>
    public ShareSlotsPanel()
    {
        var title = new TextBlock { Text = "Times to share", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
        var hint  = new TextBlock
        {
            Text         = "Drag on the calendar to pick times. Leaf leaves out the busy ones.",
            Style        = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(16, 8, 16, 16) };
        stack.Children.Add(title);
        stack.Children.Add(hint);
        stack.Children.Add(_empty);
        stack.Children.Add(_rows);

        Padding = new Thickness(0, 48, 0, 0);
        Content = new ScrollViewer { Content = stack };
        AutomationProperties.SetName(this, "Times to share");
        AutomationProperties.SetAutomationId(this, "ShareSlotsPanel");
    }

    /// <summary>Lists the view model's picked times again.</summary>
    public void Update(CalendarViewModel vm)
    {
        _rows.Children.Clear();
        _empty.Visibility = vm.ShareSlots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < vm.ShareSlots.Count; i++)
        {
            _rows.Children.Add(Row(vm, i, vm.ShareSlots[i]));
        }
    }

    // One time: its day over its start and end pickers and a remove button
    Grid Row(CalendarViewModel vm, int index, BusyRange slot)
    {
        var zone  = vm.Zone;
        var start = TimeZoneInfo.ConvertTime(slot.Start, zone);
        var end   = TimeZoneInfo.ConvertTime(slot.End, zone);
        var day   = DateOnly.FromDateTime(start.DateTime);
        var clock = vm.Settings.Use24HourTime ? "24HourClock" : "12HourClock";

        var date = new TextBlock { Text = TimeLabels.LongDate(day), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = LeafBrushes.SecondaryText(ActualTheme == ElementTheme.Dark) };
        var from = new LeafTimePicker { Time = start.TimeOfDay, MinuteIncrement = 5, ClockIdentifier = clock, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var to   = new LeafTimePicker { Time = end.TimeOfDay, MinuteIncrement = 5, ClockIdentifier = clock, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dash = new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center };
        var drop = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = (Style)Application.Current.Resources["LeafBareIconButtonStyle"] };

        AutomationProperties.SetName(from, "Start time");
        AutomationProperties.SetName(to, "End time");
        AutomationProperties.SetName(drop, "Remove this time");
        AutomationProperties.SetAutomationId(from, string.Create(CultureInfo.InvariantCulture, $"SharePanelStart_{index}"));
        AutomationProperties.SetAutomationId(to, string.Create(CultureInfo.InvariantCulture, $"SharePanelEnd_{index}"));
        AutomationProperties.SetAutomationId(drop, string.Create(CultureInfo.InvariantCulture, $"SharePanelRemove_{index}"));
        ToolTipService.SetToolTip(drop, "Remove");

        // A Changed Time Goes To The View Model (an end at or before the start is the next day's, like midnight)
        void Changed()
        {
            var startAt = DragMath.Instant(day, from.Time.TotalMinutes, zone);
            var endAt   = DragMath.Instant(to.Time <= from.Time ? day.AddDays(1) : day, to.Time.TotalMinutes, zone);
            if (startAt != slot.Start || endAt != slot.End)
            {
                vm.UpdateShareSlot(index, startAt, endAt);
            }
        }

        from.SelectedTimeChanged += (_, _) => Changed();
        to.SelectedTimeChanged   += (_, _) => Changed();
        drop.Click               += (_, _) => vm.RemoveShareSlot(index);

        // Layout: the day on its own line, then start – end and the remove button
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumnSpan(date, 4);
        foreach (var (element, column) in new (FrameworkElement, int)[] { (from, 0), (dash, 1), (to, 2), (drop, 3) })
        {
            Grid.SetRow(element, 1);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        grid.Children.Add(date);
        AutomationProperties.SetName(grid, $"{TimeLabels.LongDate(day)}, {TimeLabels.Range(slot.Start, slot.End, zone, vm.Settings.Use24HourTime)}");
        AutomationProperties.SetAutomationId(grid, string.Create(CultureInfo.InvariantCulture, $"SharePanelSlot_{index}"));
        return grid;
    }
}
