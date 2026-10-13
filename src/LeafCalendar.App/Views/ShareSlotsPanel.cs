using System.Globalization;
using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Scheduling;
using LeafCalendar.Core.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The right panel while you share availability (S): a title (shown on the saved times), the zone the copied text is
/// written in, the message Copy &amp; Save wraps the times in (it starts from your default and changes only this share;
/// <c>{times}</c> marks where they go), then under a line the Guests &amp; proposed times section: a guest box with contact
/// suggestions (Enter or a pick adds the guest to the list under it, each with a remove button), then the picked times, one
/// row each (the day, then start and end time pickers and a remove button, in the zone on screen). Pinned at the bottom:
/// for new picks Copy &amp; Save (copies the times exactly as picked, saves them with the guests as a group, stops sharing,
/// and says so in the notice) and Cancel (throws away the picks); with a saved group open, Save, Copy &amp; Save (both
/// update the group; nothing changes it until then) and Close, the heading says so, and each time gets an Approve… button
/// (enabled once the group has a guest); the group's Delete is in the title bar row (MainWindow). A valid address typed
/// but not yet added counts as a guest for Save, Copy &amp; Save and Approve….
/// A change goes straight to the view model, so the grid's slots and the copied text follow it; a time moved onto
/// another merges with it. An end at or before the start that makes no sense (<see cref="ShareSlotEdit.Apply"/>) puts
/// the pickers back. Rows are kept and refreshed in place, so focus stays on the picker you used. The panel keeps the
/// pane's fixed width; long text ends in an ellipsis. Built in code from its own references.
/// </summary>
public sealed partial class ShareSlotsPanel : UserControl
{
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _empty = new() { Text = "No times yet.", Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
    private readonly TimeZoneBox _zoneBox = new() { Header = "Time zone" };
    private readonly TextBox _message = new()
    {
        Header = "Message",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 136,
        MaxLength = AvailabilityText.MaxMessageLength,
        PlaceholderText = "Only the times",
    };
    private readonly Button _save = new() { Content = "Save", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _copy = new() { Content = "Copy & Save", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Border _footer = new() { Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly List<SlotRow> _shown = [];

    // The title is centered in the 28 DIP row right under the title bar, like the details panel's headings and the
    // sidebar's month title
    private readonly TextBlock _title = new() { Text = "Times to share", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis, Padding = new Thickness(0, 4, 0, 4) };
    private readonly TextBox _titleBox = new() { Header = "Title", PlaceholderText = CalendarViewModel.GenericShareTitle, MaxLength = ShareGroupStore.MaxTitleLength };
    private readonly AutoSuggestBox _guest = new() { PlaceholderText = "name@example.com", UpdateTextOnSelect = false };
    private readonly StackPanel _guestRows = new() { Spacing = 4 };
    private readonly Border _line = new() { Height = 1, Margin = new Thickness(0, 8, 0, 8) };

    // The divider between the message's text and its "Reset to default" strip, inside the box's border
    private readonly Border _messageLine = new() { Height = 1 };
    private readonly Button _close = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _buttons = new() { ColumnSpacing = 8 };
    private List<Contact> _guestsShown = [];
    private ContactSuggestions? _guestSuggestions;
    private CalendarViewModel? _vm;
    private bool _sharing;
    private long? _group;
    private bool? _savedLayout;

    // Sharing with a booking link (shown while a scheduling provider is connected): the switch, then the link box while
    // it's on. The box's items are kept here with the links they show, so the WinRT list is never read back
    private readonly ToggleSwitch _useProvider = new() { OnContent = "", OffContent = "" };
    private readonly ComboBox _linkBox = new() { Header = "Link", HorizontalAlignment = HorizontalAlignment.Stretch };
    private IReadOnlyList<SchedulingLink> _linksShown = [];
    private bool _showingLink;

    /// <summary>Builds the panel (filled by <see cref="Update"/>).</summary>
    public ShareSlotsPanel()
    {
        AutomationProperties.SetAutomationId(_title, "SharePanelTitle");
        var hint = new TextBlock
        {
            Text = "Drag on the calendar to pick times.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };

        // Zone (the copied text is written on this zone's clock)
        AutomationProperties.SetName(_zoneBox, "Time zone");
        AutomationProperties.SetAutomationId(_zoneBox, "ShareZoneBox");
        _zoneBox.ZoneChanged += (_, id) => _vm?.ShareZoneId = id;

        // Booking Link Switch And Link Box (the user's own changes go to the view model, not the ones Update makes)
        AutomationProperties.SetAutomationId(_useProvider, "ShareProviderSwitch");
        AutomationProperties.SetAutomationId(_linkBox, "ShareLinkBox");
        AutomationProperties.SetName(_linkBox, "Link");
        _useProvider.Toggled += (_, _) =>
        {
            if (!_showingLink)
            {
                _vm?.SetUseProvider(_useProvider.IsOn);
            }
        };
        _linkBox.SelectionChanged += (_, _) =>
        {
            if (!_showingLink && _linkBox.SelectedIndex is var i && i >= 0 && i < _linksShown.Count)
            {
                _vm?.SetShareLink(_linksShown[i]);
            }
        };

        // Title (shown on the saved times; pre-fills the event title on approve)
        AutomationProperties.SetName(_titleBox, "Title");
        AutomationProperties.SetAutomationId(_titleBox, "ShareTitleBox");
        _titleBox.LostFocus += (_, _) => _vm?.ShareTitle = _titleBox.Text;

        // Guest Box (new picks and saved groups). Contacts are suggested as you type (set up with the view model in
        // Update); a pick, or Enter on a valid address, adds the guest to the list under the box and empties the box
        AutomationProperties.SetName(_guest, "Guest email");
        AutomationProperties.SetAutomationId(_guest, "ShareGuestBox");
        FirstSuggestion.Highlight(_guest);
        _guest.TextChanged += (_, _) => ShowCanApprove();
        _guest.QuerySubmitted += (_, args) =>
        {
            if (_vm is not { } vm)
            {
                return;
            }

            var added = _guestSuggestions?.Find(args.ChosenSuggestion) is { } contact
                ? vm.AddShareGuest(contact.Email, contact.Name)
                : vm.AddShareGuest(args.QueryText);
            if (added)
            {
                _guestSuggestions?.Cancel();
                _guest.Text = "";
                _guest.IsSuggestionListOpen = false;
            }
        };

        // Section Heading (the guests and the picked times, under a line like the footer's)
        AutomationProperties.SetName(_line, "Divider");
        AutomationProperties.SetAutomationId(_line, "ShareGuestsLine");
        var guestsHeading = new TextBlock { Text = "Guests & proposed times", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(guestsHeading, "ShareGuestsHeading");
        AutomationProperties.SetAutomationId(_guestRows, "ShareGuestList");

        // Message (what Copy wraps the proposed times in, for this share only; {times} marks where they go)
        var messageHint = new TextBlock
        {
            Text = "{times} is replaced with your proposed times.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
        };
        // (the link sits at the right, the box's text padding in from its border, like the typed text)
        var textPadding = (Thickness)Application.Current.Resources["TextControlThemePadding"];
        var resetMessage = new HyperlinkButton { Content = "Reset to default", Padding = new Thickness(0), Margin = new Thickness(textPadding.Left, 8, textPadding.Right, 8), HorizontalAlignment = HorizontalAlignment.Right };

        // A plain link: no button fill behind it on hover or press (the text's own link colors still change)
        resetMessage.Resources["HyperlinkButtonBackgroundPointerOver"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        resetMessage.Resources["HyperlinkButtonBackgroundPressed"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
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

        // Message Box: the link sits in a strip along the inside of the box's bottom edge, under a 1 px divider, like the
        // event editor's description toolbar along its top; the box's bottom padding keeps typed text above the strip
        // (the 1 DIP margins keep the strip inside the box's border)
        var messageStrip = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(1, 0, 1, 1) };
        messageStrip.Children.Add(_messageLine);
        messageStrip.Children.Add(resetMessage);
        messageStrip.SizeChanged += (_, e) => _message.Padding = new Thickness(textPadding.Left, textPadding.Top, textPadding.Right, textPadding.Bottom + e.NewSize.Height);
        var messageBox = new Grid();
        messageBox.Children.Add(_message);
        messageBox.Children.Add(messageStrip);

        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 16) };
        stack.Children.Add(_title);
        stack.Children.Add(hint);
        stack.Children.Add(_useProvider);
        stack.Children.Add(_linkBox);
        stack.Children.Add(_titleBox);
        stack.Children.Add(_zoneBox);
        stack.Children.Add(messageBox);
        stack.Children.Add(messageHint);
        stack.Children.Add(_line);
        stack.Children.Add(guestsHeading);
        stack.Children.Add(_guest);
        stack.Children.Add(_guestRows);
        stack.Children.Add(_empty);
        stack.Children.Add(_rows);

        // Footer Buttons, pinned and sharing the width like the editor's footer (which ones show is set in Update). Cancel
        // and Close throw away anything unsaved, as Esc and S do
        AutomationProperties.SetAutomationId(_save, "ShareSaveButton");
        AutomationProperties.SetAutomationId(_copy, "ShareCopyButton");
        AutomationProperties.SetAutomationId(_close, "ShareCancelButton");
        _save.Click += (_, _) => Save();
        _copy.Click += (_, _) => Copy();
        _close.Click += (_, _) => _vm?.StopSharing();
        ShowButtons(saved: false);

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

        // The Day Labels' And The Two Lines' Colors Follow The Theme
        ActualThemeChanged += (_, _) =>
        {
            var dark = ActualTheme == ElementTheme.Dark;
            _footer.BorderBrush = LeafBrushes.GridLine(dark);
            _line.Background = _messageLine.Background = LeafBrushes.GridLine(dark);
            _shown.ForEach(r => r.Paint(dark));
        };
    }

    /// <summary>
    /// Shows the view model's picked times (existing rows refreshed in place, extra ones added or removed); the title,
    /// zone and message when sharing starts or another group opens; the guests; the saved-group controls while one is
    /// open; and enables Save and Copy &amp; Save once a time is picked.
    /// </summary>
    public void Update(CalendarViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        _guestSuggestions ??= new ContactSuggestions(_guest, vm);

        // Sharing Started Or Another Group Opened: the boxes show its title, message and zone, and the guest box starts
        // empty. An open group whose times all passed turns back into new picks with the boxes as typed
        if (vm.IsSharing && (!_sharing || (vm.OpenGroupId is not null && _group != vm.OpenGroupId)))
        {
            _zoneBox.Show(vm.ShareZoneId, vm.Now);
            _message.Text = vm.ShareText.Replace("\r\n", "\r", StringComparison.Ordinal);
            _titleBox.Text = vm.ShareTitle;
            _guestSuggestions.Cancel();
            _guest.Text = "";
        }

        // Sharing Stopped (Close, Esc, S, Approve…, Save, Copy or Delete): a search still waiting or running is dropped and
        // the list goes, so stale suggestions never land on the hidden box or come back next time
        if (!vm.IsSharing)
        {
            _guestSuggestions.Cancel();
            _guest.IsSuggestionListOpen = false;
            _guest.ItemsSource = null;
        }

        _group = vm.OpenGroupId;
        var saved = _group is not null;
        _title.Text = saved ? "Saved times" : "Times to share";
        ShowButtons(saved);
        ShowGuests(vm);
        ShowLink(vm);

        _sharing = vm.IsSharing;
        _save.IsEnabled = vm.ShareSlots.Count > 0;
        _copy.IsEnabled = vm.ShareSlots.Count > 0;
        _footer.BorderBrush = LeafBrushes.GridLine(ActualTheme == ElementTheme.Dark);
        _line.Background = _messageLine.Background = LeafBrushes.GridLine(ActualTheme == ElementTheme.Dark);
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

    /// <summary>
    /// Hands the Title and Message boxes as typed so far to the view model, even while a box still has focus, and adds a
    /// valid address typed in the guest box but not yet added (so no typed guest is lost). Save, Copy &amp; Save and
    /// Approve… call it first; nothing is saved until Save or Copy &amp; Save.
    /// </summary>
    public void Commit()
    {
        if (_vm is { IsSharing: true } vm)
        {
            vm.ShareTitle = _titleBox.Text;
            vm.ShareText = _message.Text;
            if (vm.AddShareGuest(_guest.Text))
            {
                _guestSuggestions?.Cancel();
                _guest.Text = "";
            }
        }
    }

    // The boxes as typed so far count, even if one still has focus
    private void Save()
    {
        if (_vm is { } vm)
        {
            Commit();
            // Through Fire, so anything unexpected is logged rather than thrown into the dispatcher
            vm.Fire(() => { vm.SaveShare(); return Task.CompletedTask; }, "share.save.failed");
        }
    }

    // The boxes as typed so far count, even if one still has focus
    private void Copy()
    {
        if (_vm is { } vm)
        {
            Commit();
            vm.Fire(() => { vm.CopyAndSaveShare(); return Task.CompletedTask; }, "share.copy.failed");
        }
    }

    // New picks: Copy & Save (the accent) and Cancel. A saved group: Save (the accent), Copy & Save and Close
    private void ShowButtons(bool saved)
    {
        if (_savedLayout == saved)
        {
            return;
        }

        _savedLayout = saved;
        _save.Style = (Style)Application.Current.Resources[saved ? "AccentButtonStyle" : "DefaultButtonStyle"];
        _copy.Style = (Style)Application.Current.Resources[saved ? "DefaultButtonStyle" : "AccentButtonStyle"];
        _close.Content = saved ? "Close" : "Cancel";
        _buttons.Children.Clear();
        _buttons.ColumnDefinitions.Clear();
        Button[] buttons = saved ? [_save, _copy, _close] : [_copy, _close];
        for (var i = 0; i < buttons.Length; i++)
        {
            _buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(buttons[i], i);
            _buttons.Children.Add(buttons[i]);
        }
    }

    // The booking link switch and the link box while a provider is connected (the box only while the switch is on). The
    // box's items are rebuilt only when the links change, so it keeps focus and its open list otherwise
    private void ShowLink(CalendarViewModel vm)
    {
        var shown = vm.IsSharing && vm.HasScheduling;
        _useProvider.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _linkBox.Visibility = shown && vm.UseProvider ? Visibility.Visible : Visibility.Collapsed;
        if (!shown)
        {
            return;
        }

        _showingLink = true;
        var label = vm.ShareSwitchLabel;
        _useProvider.Header = label;
        AutomationProperties.SetName(_useProvider, label);
        _useProvider.IsOn = vm.UseProvider;

        // ponytail: one list, not grouped by provider; group it once a second provider exists (spec 5)
        if (!ReferenceEquals(_linksShown, vm.ShareLinks))
        {
            _linksShown = vm.ShareLinks;
            _linkBox.ItemsSource = _linksShown.Select(l => string.Create(CultureInfo.CurrentCulture, $"{l.Name} · {l.Minutes} min")).ToList();
        }

        var selected = vm.ShareLink is { } link ? _linksShown.ToList().IndexOf(link) : -1;
        if (_linkBox.SelectedIndex != selected)
        {
            _linkBox.SelectedIndex = selected;
        }

        _showingLink = false;
    }

    // The guests added, one row each (rebuilt only when the list changed, so a remove button keeps focus otherwise)
    private void ShowGuests(CalendarViewModel vm)
    {
        if (_guestsShown.SequenceEqual(vm.ShareGuests))
        {
            return;
        }

        _guestsShown = [.. vm.ShareGuests];
        _guestRows.Children.Clear();
        for (var i = 0; i < _guestsShown.Count; i++)
        {
            _guestRows.Children.Add(GuestChip(_guestsShown[i], i));
        }
    }

    // One guest: the name (else the address), then a named person's address on its own line, the remove button pinned top
    // right (the event editor's guest chip)
    private Border GuestChip(Contact guest, int index)
    {
        var named = guest.Name.Length > 0;
        var name = new TextBlock { Text = named ? guest.Name : guest.Email, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetAutomationId(name, string.Create(CultureInfo.InvariantCulture, $"ShareGuest_{index}"));
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(name);
        if (named)
        {
            lines.Children.Add(new TextBlock { Text = guest.Email, Style = (Style)Application.Current.Resources["LeafSecondaryTextStyle"], TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Style = (Style)Application.Current.Resources["LeafIconButtonStyle"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(remove, "Remove guest");
        AutomationProperties.SetName(remove, "Remove guest");
        AutomationProperties.SetAutomationId(remove, string.Create(CultureInfo.InvariantCulture, $"ShareGuestRemove_{index}"));
        remove.Click += (_, _) => _vm?.RemoveShareGuest(index);
        Grid.SetColumn(remove, 1);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(lines);
        grid.Children.Add(remove);
        var chip = new Border { Style = (Style)Application.Current.Resources["LeafGuestChipStyle"], Child = grid };
        AutomationProperties.SetName(chip, named ? $"{guest.Name}, {guest.Email}" : guest.Email);
        return chip;
    }

    // Approve… is enabled once there's a guest (added, or a valid address typed and not yet added)
    private void ShowCanApprove()
    {
        var can = _vm is { ShareGuests.Count: > 0 } || CalendarViewModel.IsAddress(_guest.Text.Trim());
        _shown.ForEach(r => r.CanApprove = can);
    }

    // Approve…: the boxes as typed so far count, then the event editor opens on this time with every guest, or, on a link
    // whose provider can book, the time is booked there once you confirm (spec 5.1)
    private void Approve(SlotRow row)
    {
        if (_vm is { } vm)
        {
            Commit();
            if (vm.ApprovesThroughProvider)
            {
                Book(vm, row);
            }
            else
            {
                vm.ApproveSlot(row.Index);
            }
        }
    }

    // Booking on the provider: the bookable times inside this time (several: you pick one in a flyout under the button),
    // then a confirmation, since booking sends the provider's emails. The panel is off with a ring in the row while the
    // provider is asked (once booked, sharing has stopped and the panel is hidden). Anything unexpected is logged and says
    // nothing was booked
    private async void Book(CalendarViewModel vm, SlotRow row)
    {
        var name = vm.ShareProviderName;
        try
        {
            SetBusy(row, true);
            var times = await vm.BookableTimesAsync(row.Index, CancellationToken.None);
            SetBusy(row, false);
            var time = times.Count == 1 ? times[0] : times.Count > 1 ? await PickTimeAsync(vm, row.Approve, times) : null;
            if (time is null || !await ConfirmAsync(vm, time) || !vm.ApprovesThroughProvider)
            {
                return;
            }

            SetBusy(row, true);
            await vm.BookAsync(time, CancellationToken.None);
        }
        catch (Exception ex)
        {
            vm.LogBookFailure(ex);
            vm.SayShare($"Couldn't reach {name}. Nothing was booked.");
        }
        finally
        {
            SetBusy(row, false);
        }
    }

    // While the provider is asked, every control in the panel is off and the row's Approve… shows a ring
    private void SetBusy(SlotRow row, bool busy)
    {
        IsEnabled = !busy;
        row.Busy = busy;
    }

    // A flyout under Approve… listing the bookable times (in the share's zone); null when it closes without a pick
    private static Task<BusyRange?> PickTimeAsync(CalendarViewModel vm, Button under, IReadOnlyList<BusyRange> times)
    {
        var picked = new TaskCompletionSource<BusyRange?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var flyout = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        var labels = vm.BookableTimeLabels(times);
        for (var i = 0; i < times.Count; i++)
        {
            var time = times[i];
            var item = new MenuFlyoutItem { Text = labels[i] };
            AutomationProperties.SetAutomationId(item, string.Create(CultureInfo.InvariantCulture, $"ApproveTime_{i}"));
            item.Click += (_, _) => picked.TrySetResult(time);
            flyout.Items.Add(item);
        }

        // Handled a turn later, so a pick's click always lands first
        flyout.Closed += (_, _) => under.DispatcherQueue.TryEnqueue(() => picked.TrySetResult(null));
        flyout.ShowAt(under);
        return picked.Task;
    }

    // "Book {time} on {provider}?" with the guests the booking invites listed (names, else addresses); Book is the accent
    private async Task<bool> ConfirmAsync(CalendarViewModel vm, BusyRange time)
    {
        var guests = new StackPanel { Spacing = 4 };
        foreach (var guest in vm.BookingGuests)
        {
            guests.Children.Add(new TextBlock { Text = guest.Name.Length > 0 ? guest.Name : guest.Email, TextWrapping = TextWrapping.Wrap });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Book {vm.BookableTimeLabels([time])[0]} on {vm.ShareProviderName}?",
            Content = guests,
            PrimaryButtonText = "Book",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "BookDialog");
        CtrlEnter.Submits(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
        private readonly FontIcon _check = new() { Glyph = "\uE73E", FontSize = 16 };
        private readonly ProgressRing _ring = new() { IsActive = false, Width = 16, Height = 16, Visibility = Visibility.Collapsed };
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

            // Approve… (a saved group only): this time as an event with the guests; a check mark, then the text, centered in
            // a button as wide as the row
            var approveContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            // (while booking on a provider, a ring stands in for the check mark)
            AutomationProperties.SetAutomationId(_ring, string.Create(CultureInfo.InvariantCulture, $"SharePanelApproveRing_{index}"));
            approveContent.Children.Add(_check);
            approveContent.Children.Add(_ring);
            approveContent.Children.Add(new TextBlock { Text = "Approve…" });
            Approve = new Button { Content = approveContent, IsEnabled = false, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTipService.SetToolTip(Approve, "Save this time as an event with the guests");
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

        // Booking on a provider: the ring spins in place of the check mark
        public bool Busy
        {
            set
            {
                _ring.IsActive = value;
                _ring.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                _check.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            }
        }

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
