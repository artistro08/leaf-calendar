using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace LeafCalendar.App.Views;

/// <summary>
/// The right panel while you share availability (S): a title (shown on the saved times), the zone the copied text is
/// written in, the message Copy wraps the times in (it starts from your default and changes only this share;
/// <c>{times}</c> marks where they go), the picked times, one row each (the day, then start and end time pickers and a
/// remove button, in the zone on screen), then Copy (which copies, saves the times as a group, stops sharing, and says so
/// in the notice) and Cancel pinned at the bottom. With a saved group open the heading says so, every change saves to the
/// group, a guest email box shows, each time gets an Approve… button (enabled for one valid address), and the footer
/// holds Copy, Close and Delete. Busy times come from the visible calendars.
/// A change goes straight to the view model, so the grid's slots and the copied text follow it; a time moved onto
/// another merges with it. An end at or before the start that makes no sense (<see cref="ShareSlotEdit.Apply"/>) puts
/// the pickers back. Rows are kept and refreshed in place, so focus stays on the picker you used. The panel keeps the
/// pane's fixed width; long text ends in an ellipsis. Built in code from its own references.
/// </summary>
public sealed partial class ShareSlotsPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _empty = new() { Text = "No times yet.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    private readonly TimeZoneComboBox _zoneBox = new() { Header = "Time zone", IsEditable = true, IsTextSearchEnabled = true };
    private readonly TextBox _message = new()
    {
        Header = "Message",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 88,
        MaxLength = AvailabilityText.MaxMessageLength,
        PlaceholderText = "Only the times",
    };
    private readonly Button _copy = new() { Content = "Copy", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Border _footer = new() { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly List<SlotRow> _shown = [];

    // The title is centered in the 28 DIP row right under the title bar, like the details panel's headings and the
    // sidebar's month title
    private readonly TextBlock _title = new() { Text = "Times to share", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _titleBox = new() { Header = "Title", PlaceholderText = CalendarViewModel.GenericShareTitle, MaxLength = ShareGroupStore.MaxTitleLength };
    private readonly TextBox _guest = new() { Header = "Guest email", PlaceholderText = "name@example.com", InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.EmailSmtpAddress) } }, Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _delete = new() { Content = "Delete", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
    private readonly Grid _buttons = new() { ColumnSpacing = 8 };
    private readonly ColumnDefinition _deleteColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private CalendarViewModel? _vm;
    private bool _sharing;
    private long? _group;

    /// <summary>Builds the panel (filled by <see cref="Update"/>).</summary>
    public ShareSlotsPanel()
    {
        AutomationProperties.SetAutomationId(_title, "SharePanelTitle");
        var hint = new TextBlock
        {
            Text = "Drag on the calendar to pick times. Leaf leaves out the busy ones.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        // Zone (the copied text is written on this zone's clock)
        AutomationProperties.SetName(_zoneBox, "Time zone");
        AutomationProperties.SetAutomationId(_zoneBox, "ShareZoneBox");
        _zoneBox.ZoneChanged += (_, id) => _vm?.ShareZoneId = id;

        // Title (shown on the saved times; pre-fills the event title on approve)
        AutomationProperties.SetName(_titleBox, "Title");
        AutomationProperties.SetAutomationId(_titleBox, "ShareTitleBox");
        _titleBox.LostFocus += (_, _) => _vm?.ShareTitle = _titleBox.Text;

        // Guest (a saved group only: Approve needs one address)
        AutomationProperties.SetName(_guest, "Guest email");
        AutomationProperties.SetAutomationId(_guest, "ShareGuestBox");
        _guest.TextChanged += (_, _) => ShowCanApprove();

        // Message (what Copy wraps the free times in, for this share only; {times} marks where they go)
        var messageHint = new TextBlock
        {
            Text = "{times} is replaced with your free times.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        var resetMessage = new HyperlinkButton { Content = "Use the default message", Padding = new Thickness(0) };
        AutomationProperties.SetName(_message, "Message");
        AutomationProperties.SetAutomationId(_message, "ShareMessageBox");
        AutomationProperties.SetAutomationId(resetMessage, "ShareMessageReset");
        _message.LostFocus += (_, _) => _vm?.ShareText = _message.Text;
        resetMessage.Click += (_, _) =>
        {
            if (_vm is { } vm)
            {
                _message.Text = vm.Settings.ShareMessage.Replace("\r\n", "\r", StringComparison.Ordinal);
                vm.ShareText = vm.Settings.ShareMessage;
            }
        };

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        stack.Children.Add(_title);
        stack.Children.Add(hint);
        stack.Children.Add(_titleBox);
        stack.Children.Add(_zoneBox);
        stack.Children.Add(_message);
        stack.Children.Add(messageHint);
        stack.Children.Add(resetMessage);
        stack.Children.Add(_guest);
        stack.Children.Add(_empty);
        stack.Children.Add(_rows);

        // Copy, Cancel (Close for a saved group) And Delete (a saved group only), pinned and sharing the width like the
        // editor's footer; Copy is the one accent. The delete column is only there while Delete shows (a hidden column
        // would still take the grid's column spacing)
        AutomationProperties.SetAutomationId(_copy, "ShareCopyButton");
        AutomationProperties.SetAutomationId(_cancel, "ShareCancelButton");
        AutomationProperties.SetAutomationId(_delete, "ShareDeleteButton");
        _copy.Click += (_, _) => Copy();
        _cancel.Click += (_, _) => _vm?.StopSharing();
        _delete.Click += (_, _) => _vm?.DeleteOpenGroup();

        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_cancel, 1);
        Grid.SetColumn(_delete, 2);
        _buttons.Children.Add(_copy);
        _buttons.Children.Add(_cancel);
        _buttons.Children.Add(_delete);

        _footer.Child = _buttons;
        AutomationProperties.SetAutomationId(_footer, "ShareFooter");

        // Layout: the scrolling times under the title bar row (a UserControl doesn't apply its own padding), the footer pinned
        var scroll = new ScrollViewer { Content = stack };
        ScrollIndicator.ShowOnHover(scroll);
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

    /// <summary>
    /// Shows the view model's picked times (existing rows refreshed in place, extra ones added or removed); the title,
    /// zone and message when sharing starts or another group opens; the saved-group controls while one is open; and
    /// enables Copy once a time is picked.
    /// </summary>
    public void Update(CalendarViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;

        // Sharing Started Or Another Group Opened: the boxes show its title, message and zone
        if (vm.IsSharing && (!_sharing || _group != vm.OpenGroupId))
        {
            _zoneBox.Show(vm.ShareZoneId, vm.Now);
            _message.Text = vm.ShareText.Replace("\r\n", "\r", StringComparison.Ordinal);
            _titleBox.Text = vm.ShareTitle;
            _guest.Text = "";
        }

        _group = vm.OpenGroupId;
        var saved = _group is not null;
        _title.Text = saved ? "Saved times" : "Times to share";
        _cancel.Content = saved ? "Close" : "Cancel";
        _guest.Visibility = _delete.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        if (saved != _buttons.ColumnDefinitions.Contains(_deleteColumn))
        {
            if (saved)
            {
                _buttons.ColumnDefinitions.Add(_deleteColumn);
            }
            else
            {
                _buttons.ColumnDefinitions.Remove(_deleteColumn);
            }
        }

        _sharing = vm.IsSharing;
        _copy.IsEnabled = vm.ShareSlots.Count > 0;
        _footer.BorderBrush = LeafBrushes.GridLine(ActualTheme == ElementTheme.Dark);
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
            _shown[i].Show(vm, vm.ShareSlots[i], ActualTheme == ElementTheme.Dark, saved);
        }

        ShowCanApprove();
    }

    // The title and message as typed so far count, even if a box still has focus
    private void Copy()
    {
        if (_vm is { } vm)
        {
            vm.ShareTitle = _titleBox.Text;
            vm.ShareText = _message.Text;
            vm.Fire(vm.CopyAvailabilityAsync, "share.copy.failed");
        }
    }

    // Approve… is enabled while the guest box holds one valid address
    private void ShowCanApprove()
    {
        var can = CalendarViewModel.IsAddress(_guest.Text.Trim());
        _shown.ForEach(r => r.CanApprove = can);
    }

    // Approve…: the title as typed so far counts (the event editor with this time and the guest comes with ApproveSlot)
    private void Approve(SlotRow row)
    {
        _ = row;
        if (_vm is { } vm && CalendarViewModel.IsAddress(_guest.Text.Trim()))
        {
            vm.ShareTitle = _titleBox.Text;
        }
    }

    // A picker changed: the new time, or the pickers put back when it makes no sense
    private void Changed(SlotRow row)
    {
        if (_vm is not { } vm || row.Index >= vm.ShareSlots.Count)
        {
            return;
        }

        var slot = vm.ShareSlots[row.Index];
        if (ShareSlotEdit.Apply(slot, row.From.Time, row.To.Time, vm.Zone) is not { } edited)
        {
            row.Show(vm, slot, ActualTheme == ElementTheme.Dark, _group is not null);
            return;
        }

        if (edited != slot)
        {
            vm.UpdateShareSlot(row.Index, edited.Start, edited.End);
        }
    }

    // One time: its day over its start and end pickers and a remove button
    private sealed class SlotRow
    {
        private readonly TextBlock _date = new() { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        private bool _showing;

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
            To.SelectedTimeChanged += (_, _) => { if (!_showing) { owner.Changed(this); } };
            drop.Click += (_, _) => owner._vm?.RemoveShareSlot(Index);

            // Approve… (a saved group only): this time as an event with the guest
            Approve = new Button { Content = "Approve…", IsEnabled = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
            ToolTipService.SetToolTip(Approve, "Save this time as an event with the guest");
            AutomationProperties.SetAutomationId(Approve, string.Create(CultureInfo.InvariantCulture, $"SharePanelApprove_{index}"));
            Approve.Click += (_, _) => owner.Approve(this);
            Grid.SetRow(Approve, 2);
            Grid.SetColumnSpan(Approve, 4);
            Grid.Children.Add(Approve);

            // Layout: the day on its own line, then start – end and the remove button, then Approve… under the pickers
            // (the pane is too narrow for a fifth column)
            Grid.ColumnSpacing = 8;
            Grid.RowSpacing = 4;
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
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

        public Button Approve { get; }

        public bool CanApprove { set => Approve.IsEnabled = value; }

        // Shows a slot's day and times (in the zone on screen) without counting as a pick; Approve… only for a saved group
        public void Show(CalendarViewModel vm, BusyRange slot, bool dark, bool saved)
        {
            var zone = vm.Zone;
            var start = TimeZoneInfo.ConvertTime(slot.Start, zone);
            var end = TimeZoneInfo.ConvertTime(slot.End, zone);
            var day = DateOnly.FromDateTime(start.DateTime);

            _showing = true;
            From.ClockIdentifier = To.ClockIdentifier = vm.Settings.Use24HourTime ? "24HourClock" : "12HourClock";
            From.Time = start.TimeOfDay;
            To.Time = end.TimeOfDay;
            _date.Text = TimeLabels.LongDate(day);
            _showing = false;
            Paint(dark);
            var range = TimeLabels.Range(slot.Start, slot.End, zone, vm.Settings.Use24HourTime);
            AutomationProperties.SetName(Grid, $"{TimeLabels.LongDate(day)}, {range}");
            AutomationProperties.SetName(Approve, $"Approve {range}");
            Approve.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        }

        public void Paint(bool dark) => _date.Foreground = LeafBrushes.SecondaryText(dark);
    }
}
