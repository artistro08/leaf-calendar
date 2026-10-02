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
/// The right panel while you share availability (S): the zone the copied text is written in, the message Copy wraps the
/// times in (yours to change, kept for next time; <c>{times}</c> marks where they go), the picked times, one row
/// each (the day, then start and end time pickers and a remove button, in the zone on screen), then Copy (which copies,
/// stops sharing, and says so in the notice) and Cancel pinned at the bottom. Busy times come from the visible calendars.
/// A change goes straight to the view model, so the grid's slots and the copied text follow it; a time moved onto
/// another merges with it. An end at or before the start that makes no sense (<see cref="ShareSlotEdit.Apply"/>) puts
/// the pickers back. Rows are kept and refreshed in place, so focus stays on the picker you used. The panel keeps the
/// pane's fixed width; long text ends in an ellipsis. Built in code from its own references.
/// </summary>
public sealed partial class ShareSlotsPanel : UserControl
{
    readonly StackPanel _rows = new() { Spacing = 8 };
    readonly TextBlock _empty = new() { Text = "No times yet.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    readonly TimeZoneComboBox _zoneBox = new() { Header = "Time zone", IsEditable = true, IsTextSearchEnabled = true };
    readonly TextBox _message = new()
    {
        Header          = "Message",
        AcceptsReturn   = true,
        TextWrapping    = TextWrapping.Wrap,
        MinHeight       = 88,
        MaxLength       = AvailabilityText.MaxMessageLength,
        PlaceholderText = "Only the times",
    };
    readonly Button _copy = new() { Content = "Copy", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    readonly Border _footer = new() { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
    readonly List<SlotRow> _shown = [];
    CalendarViewModel? _vm;
    bool _sharing;

    /// <summary>Builds the panel (filled by <see cref="Update"/>).</summary>
    public ShareSlotsPanel()
    {
        var title = new TextBlock { Text = "Times to share", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        var hint  = new TextBlock
        {
            Text         = "Drag on the calendar to pick times. Leaf leaves out the busy ones.",
            Style        = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        // Zone (the copied text is written on this zone's clock)
        AutomationProperties.SetName(_zoneBox, "Time zone");
        AutomationProperties.SetAutomationId(_zoneBox, "ShareZoneBox");
        _zoneBox.ZoneChanged += (_, id) => _vm?.ShareZoneId = id;

        // Message (what Copy wraps the free times in, kept for next time; {times} marks where they go)
        var messageHint = new TextBlock
        {
            Text         = "{times} is replaced with your free times.",
            Style        = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var resetMessage = new HyperlinkButton { Content = "Use the default message", Padding = new Thickness(0) };
        AutomationProperties.SetName(_message, "Message");
        AutomationProperties.SetAutomationId(_message, "ShareMessageBox");
        AutomationProperties.SetAutomationId(resetMessage, "ShareMessageReset");
        _message.LostFocus += (_, _) => _vm?.SetShareMessage(_message.Text);
        resetMessage.Click += (_, _) =>
        {
            _message.Text = AvailabilityText.DefaultMessage.Replace("\r\n", "\r", StringComparison.Ordinal);
            _vm?.SetShareMessage(AvailabilityText.DefaultMessage);
        };

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(16, 8, 16, 16) };
        stack.Children.Add(title);
        stack.Children.Add(hint);
        stack.Children.Add(_zoneBox);
        stack.Children.Add(_message);
        stack.Children.Add(messageHint);
        stack.Children.Add(resetMessage);
        stack.Children.Add(_empty);
        stack.Children.Add(_rows);

        // Copy And Cancel (pinned, each half the width, like the editor's footer; Copy is the one accent)
        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(_copy, "ShareCopyButton");
        AutomationProperties.SetAutomationId(cancel, "ShareCancelButton");
        _copy.Click  += (_, _) => Copy();
        cancel.Click += (_, _) => _vm?.StopSharing();

        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(cancel, 1);
        buttons.Children.Add(_copy);
        buttons.Children.Add(cancel);

        _footer.Child = buttons;
        AutomationProperties.SetAutomationId(_footer, "ShareFooter");

        // Layout: the scrolling times under the title bar row (a UserControl doesn't apply its own padding), the footer pinned
        var scroll = new ScrollViewer { Content = stack };
        var layout = new Grid { Margin = new Thickness(0, 48, 0, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_footer, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(_footer);

        Content = layout;
        AutomationProperties.SetName(this, "Times to share");
        AutomationProperties.SetAutomationId(this, "ShareSlotsPanel");

        // The Day Labels' And The Footer Line's Colors Follow The Theme
        ActualThemeChanged += (_, _) =>
        {
            var dark = ActualTheme == ElementTheme.Dark;
            _footer.BorderBrush = LeafBrushes.GridLine(dark);
            _shown.ForEach(r => r.Paint(dark));
        };
    }

    /// <summary>Shows the view model's picked times (existing rows refreshed in place, extra ones added or removed), the zone when sharing starts, and enables Copy once a time is picked.</summary>
    public void Update(CalendarViewModel vm)
    {
        _vm = vm;
        if (vm.IsSharing && !_sharing)
        {
            _zoneBox.Show(vm.ShareZoneId, vm.Now);
            _message.Text = vm.Settings.ShareMessage.Replace("\r\n", "\r", StringComparison.Ordinal);
        }

        _sharing            = vm.IsSharing;
        _copy.IsEnabled     = vm.ShareSlots.Count > 0;
        _footer.BorderBrush = LeafBrushes.GridLine(ActualTheme == ElementTheme.Dark);
        _empty.Visibility   = vm.ShareSlots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    // The message as typed so far counts, even if the box still has focus
    void Copy()
    {
        if (_vm is { } vm)
        {
            vm.SetShareMessage(_message.Text);
            vm.Fire(vm.CopyAvailabilityAsync, "share.copy.failed");
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
        readonly TextBlock _date = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
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

        public TimePicker From { get; } = TimePickerFit.Shrink(new() { MinuteIncrement = 5, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch });

        public TimePicker To { get; } = TimePickerFit.Shrink(new() { MinuteIncrement = 5, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch });

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
