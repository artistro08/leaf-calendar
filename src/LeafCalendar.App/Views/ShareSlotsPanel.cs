using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The right panel while you share availability: the picked times, one row each (the day, then start and end time
/// pickers and a remove button), in the zone on screen. A change goes straight to the view model, so the grid's slots
/// and the copied text follow it; a time moved onto another merges with it. An end at or before the start that makes no
/// sense (<see cref="ShareSlotEdit.Apply"/>) puts the pickers back. Rows are kept and refreshed in place, so focus
/// stays on the picker you used. Built in code from its own references.
/// </summary>
public sealed partial class ShareSlotsPanel : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 8 };
    readonly TextBlock _empty = new() { Text = "No times yet.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    readonly List<SlotRow> _shown = [];
    CalendarViewModel? _vm;

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

        // Under The Title Bar Row (a UserControl doesn't apply its own padding)
        Content = new ScrollViewer { Content = stack, Margin = new Thickness(0, 48, 0, 0) };
        AutomationProperties.SetName(this, "Times to share");
        AutomationProperties.SetAutomationId(this, "ShareSlotsPanel");

        // The Day Labels' Color Follows The Theme
        ActualThemeChanged += (_, _) => _shown.ForEach(r => r.Paint(ActualTheme == ElementTheme.Dark));
    }

    /// <summary>Shows the view model's picked times: existing rows are refreshed in place, extra ones added or removed.</summary>
    public void Update(CalendarViewModel vm)
    {
        _vm = vm;
        _empty.Visibility = vm.ShareSlots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        while (_shown.Count < vm.ShareSlots.Count)
        {
            var row = new SlotRow(this, _shown.Count);
            _shown.Add(row);
            _rows.Children.Add(row.Grid);
        }

        while (_shown.Count > vm.ShareSlots.Count)
        {
            _rows.Children.Remove(_shown[^1].Grid);
            _shown.RemoveAt(_shown.Count - 1);
        }

        for (var i = 0; i < _shown.Count; i++)
        {
            _shown[i].Show(vm, vm.ShareSlots[i], ActualTheme == ElementTheme.Dark);
        }
    }

    // A picker changed: the new time, or the pickers put back when it makes no sense
    void Changed(SlotRow row)
    {
        if (_vm is not { } vm || row.Index >= vm.ShareSlots.Count)
        {
            return;
        }

        var slot = vm.ShareSlots[row.Index];
        if (ShareSlotEdit.Apply(slot, row.From.Time, row.To.Time, vm.Zone) is not { } edited)
        {
            row.Show(vm, slot, ActualTheme == ElementTheme.Dark);
            return;
        }

        if (edited != slot)
        {
            vm.UpdateShareSlot(row.Index, edited.Start, edited.End);
        }
    }

    // One time: its day over its start and end pickers and a remove button
    sealed class SlotRow
    {
        readonly TextBlock _date = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        bool _showing;

        public SlotRow(ShareSlotsPanel owner, int index)
        {
            Index = index;
            var dash = new TextBlock { Text = "–", VerticalAlignment = VerticalAlignment.Center };
            var drop = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = (Style)Application.Current.Resources["LeafBareIconButtonStyle"] };

            AutomationProperties.SetName(From, "Start time");
            AutomationProperties.SetName(To, "End time");
            AutomationProperties.SetName(drop, "Remove this time");
            AutomationProperties.SetAutomationId(From, string.Create(CultureInfo.InvariantCulture, $"SharePanelStart_{index}"));
            AutomationProperties.SetAutomationId(To, string.Create(CultureInfo.InvariantCulture, $"SharePanelEnd_{index}"));
            AutomationProperties.SetAutomationId(drop, string.Create(CultureInfo.InvariantCulture, $"SharePanelRemove_{index}"));
            AutomationProperties.SetAutomationId(Grid, string.Create(CultureInfo.InvariantCulture, $"SharePanelSlot_{index}"));
            ToolTipService.SetToolTip(drop, "Remove");

            // Only The User's Picks Count (not the times set while showing a slot)
            From.SelectedTimeChanged += (_, _) => { if (!_showing) { owner.Changed(this); } };
            To.SelectedTimeChanged   += (_, _) => { if (!_showing) { owner.Changed(this); } };
            drop.Click               += (_, _) => owner._vm?.RemoveShareSlot(Index);

            // Layout: the day on its own line, then start – end and the remove button
            Grid.ColumnSpacing = 8;
            Grid.RowSpacing    = 4;
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumnSpan(_date, 4);
            foreach (var (element, column) in new (FrameworkElement, int)[] { (From, 0), (dash, 1), (To, 2), (drop, 3) })
            {
                Grid.SetRow(element, 1);
                Grid.SetColumn(element, column);
                Grid.Children.Add(element);
            }

            Grid.Children.Add(_date);
        }

        public int Index { get; }

        public Grid Grid { get; } = new();

        public LeafTimePicker From { get; } = new() { MinuteIncrement = 5, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        public LeafTimePicker To { get; } = new() { MinuteIncrement = 5, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch };

        // Shows a slot's day and times (in the zone on screen) without counting as a pick
        public void Show(CalendarViewModel vm, BusyRange slot, bool dark)
        {
            var zone  = vm.Zone;
            var start = TimeZoneInfo.ConvertTime(slot.Start, zone);
            var end   = TimeZoneInfo.ConvertTime(slot.End, zone);
            var day   = DateOnly.FromDateTime(start.DateTime);

            _showing             = true;
            From.ClockIdentifier = To.ClockIdentifier = vm.Settings.Use24HourTime ? "24HourClock" : "12HourClock";
            From.Time            = start.TimeOfDay;
            To.Time              = end.TimeOfDay;
            _date.Text           = TimeLabels.LongDate(day);
            _showing             = false;
            Paint(dark);
            AutomationProperties.SetName(Grid, $"{TimeLabels.LongDate(day)}, {TimeLabels.Range(slot.Start, slot.End, zone, vm.Settings.Use24HourTime)}");
        }

        public void Paint(bool dark) => _date.Foreground = LeafBrushes.SecondaryText(dark);
    }
}
