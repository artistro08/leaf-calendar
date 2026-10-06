using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using LeafCalendar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LeafCalendar.App.Views;

/// <summary>
/// A person's chip on the overlay bar, or their row in its dropdown (an App type, so WinRT can hold the list). Remove
/// x:Binds to <see cref="Remove"/>.
/// </summary>
public sealed record OverlayChip(string Email, string Name, bool Unknown, SolidColorBrush Dot, Action<string> OnRemove, bool InDropdown = false)
{
    /// <summary>The chip's outline in the bar; a row in the dropdown has none.</summary>
    public Thickness Outline => new(InDropdown ? 0 : 1);

    /// <summary>Automation ID of the chip.</summary>
    public string ChipId => $"OverlayChip_{Email}";

    /// <summary>Automation ID of the remove button.</summary>
    public string RemoveId => $"OverlayRemove_{Email}";

    /// <summary>The remove button's name and tooltip.</summary>
    public string RemoveName => $"Remove {Name}";

    /// <summary>" · No free/busy info" when Google had none for this person.</summary>
    public string UnknownText => Unknown ? " · No free/busy info" : "";

    /// <summary>What a screen reader says for the chip.</summary>
    public string FullText => Name + UnknownText;

    /// <summary>Remove click.</summary>
    public void Remove() => OnRemove(Email);
}

/// <summary>
/// The bar above the calendar while people are overlaid: "Busy times" (or "Meet with"), the person's chip in their color
/// with a remove button (or, for two or more, a dropdown listing a chip for each), and Clear. Hidden when nobody is
/// overlaid.
/// </summary>
public sealed partial class OverlayBar : UserControl
{
    private CalendarViewModel? _vm;

    /// <summary>Creates the bar (hidden until <see cref="Update"/> shows someone).</summary>
    public OverlayBar()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Update(_vm);
    }

    /// <summary>Shows the view model's overlaid people, or hides the bar when there are none.</summary>
    public void Update(CalendarViewModel? vm)
    {
        _vm = vm;
        if (vm is null || vm.OverlayPeople.Count == 0)
        {
            Visibility = Visibility.Collapsed;
            Chips.ItemsSource = null;
            PeopleList.ItemsSource = null;
            PeopleFlyout.Hide();
            return;
        }

        // Label And Hint
        var month = vm.Mode == CalendarViewMode.Month;
        Label.Text = vm.IsMeetWith ? "Meet with" : "Busy times";
        Hint.Text = month ? "Busy times show in the day and week views." : vm.IsMeetWith ? "Drag on the calendar to invite them." : "";
        Hint.Visibility = Hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // One Person's Chip, Or The Dropdown Of Everyone (it stays open while people are removed from it)
        var dark = ActualTheme == ElementTheme.Dark;
        var chips = vm.OverlayPeople
            .Select(p => new OverlayChip(p.Email, p.Name, p.State == PersonBusyState.Unknown, LeafBrushes.Person(p.ColorIndex, dark), vm.RemoveOverlayPerson, vm.OverlayPeople.Count > 1))
            .ToList();
        var one = chips.Count == 1;
        var list = one ? Chips : PeopleList;
        var focusedChip = Math.Max(FocusedChipIndex(Chips), FocusedChipIndex(PeopleList));
        Chips.ItemsSource = one ? chips : null;
        PeopleList.ItemsSource = one ? null : chips;
        PeopleCount.Text = $"{chips.Count} people";
        PeopleButton.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
        if (one)
        {
            PeopleFlyout.Hide();
        }

        Visibility = Visibility.Visible;

        // The Rebuilt Chips Drop A Focused Remove Button, So Focus Goes Back To The One Now In Its Place (or Clear, past the last)
        if (focusedChip >= 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FocusChip(list, focusedChip));
        }
    }

    // The chip in this list holding focus, or -1
    private int FocusedChipIndex(ItemsControl list)
    {
        if (XamlRoot is null)
        {
            return -1;
        }

        for (DependencyObject? current = FocusManager.GetFocusedElement(XamlRoot) as UIElement; current is not null && current != list; current = VisualTreeHelper.GetParent(current))
        {
            if (list.IndexFromContainer(current) is var index and >= 0)
            {
                return index;
            }
        }

        return -1;
    }

    // The remove button now at this place in the list, or the one above it, or Clear once the list is gone
    private void FocusChip(ItemsControl list, int index)
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        list.UpdateLayout();
        if ((list.ContainerFromIndex(index) ?? (index > 0 ? list.ContainerFromIndex(index - 1) : null)) is { } container && FindButton(container) is { } remove)
        {
            remove.Focus(FocusState.Keyboard);
            return;
        }

        ClearButton.Focus(FocusState.Keyboard);
    }

    private static Button? FindButton(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as Button ?? FindButton(child)) is { } button)
            {
                return button;
            }
        }

        return null;
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _vm?.ClearOverlay();
}
