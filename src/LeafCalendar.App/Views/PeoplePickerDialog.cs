using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.People;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// Picks people to overlay (P) or meet with (F): a "Name or email" box with contact suggestions, and the picked people
/// below it, each with a remove button.
/// </summary>
public static class PeoplePickerDialog
{
    /// <summary>
    /// Shows the picker; the picked people, or null when canceled. A suggestion adds its person, and Enter on text that
    /// is exactly one valid address adds that address. The primary button needs 1 to <see cref="FreeBusyLookup.MaxPeople"/> people.
    /// </summary>
    public static async Task<IReadOnlyList<Contact>?> ShowAsync(FrameworkElement owner, CalendarViewModel vm, string title, string primaryText)
    {
        var picked = new List<Contact>();
        var suggestions = new List<(ContactSuggestion View, Contact Person)>();
        var removes = new List<Button>();
        using var search = new LatestSearch<ContactResults>();

        // Box
        var box = new AutoSuggestBox { PlaceholderText = "Name or email", UpdateTextOnSelect = false, MinWidth = 320 };
        AutomationProperties.SetAutomationId(box, "PeoplePickerBox");
        AutomationProperties.SetName(box, "Name or email");

        // Hint (caption, secondary color that follows the theme), Then The Picked People (collapsed while empty, so no
        // blank strip sits under the hint)
        var hint = new TextBlock { Text = $"Up to {FreeBusyLookup.MaxPeople} people.", Style = (Style)Application.Current.Resources["LeafSecondaryTextStyle"], Margin = new Thickness(0, 4, 0, 0) };
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var layout = new StackPanel();
        layout.Children.Add(box);
        layout.Children.Add(hint);
        layout.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot = owner.XamlRoot,
            RequestedTheme = owner.ActualTheme,
            Title = title,
            Content = layout,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",

            // The primary action is accent-colored, but not the default button: Enter in the box adds the typed person,
            // and never closes the dialog
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            DefaultButton = ContentDialogButton.None,
            IsPrimaryButtonEnabled = false,
        };

        // Suggestions Wait Until Typing Pauses
        var timer = owner.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => vm.Fire(SuggestAsync, "people.suggest.failed");

        // The suggestion Up/Down or a click last picked; typing drops it
        object? chosen = null;
        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            chosen = null;
            timer.Stop();
            timer.Start();
        };
        box.SuggestionChosen += (_, args) => chosen = args.SelectedItem;

        // A Clicked Suggestion Adds Its Person
        box.QuerySubmitted += (_, args) =>
        {
            if (args.ChosenSuggestion is { } clicked)
            {
                Submit(clicked);
            }
        };

        // Enter: the ContentDialog takes Enter before the box can raise QuerySubmitted, so the box handles it first
        box.AddHandler(UIElement.PreviewKeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            // The box's Text catches up with typing only when its TextChanged runs, which can come after a quick Enter
            args.Handled = true;
            var pick = chosen;
            box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Submit(pick));
        }), handledEventsToo: true);

        var result = await dialog.ShowAsync();
        timer.Stop();
        search.Cancel();
        return result == ContentDialogResult.Primary ? picked : null;

        async Task SuggestAsync()
        {
            var text = box.Text.Trim();
            if (text.Length == 0)
            {
                search.Cancel();
                return;
            }

            // A result for text that's no longer in the box (a person was added, or the box changed) is dropped
            if (await search.RunAsync(ct => vm.SearchPeopleAsync(text, ct)) is not { } results || box.Text.Trim() != text)
            {
                return;
            }

            suggestions.Clear();
            suggestions.AddRange(results.Contacts.Select(c => (new ContactSuggestion(c.Name, c.Email), c)));
            box.ItemsSource = suggestions.Select(s => s.View).ToList();
            box.IsSuggestionListOpen = suggestions.Count > 0;
        }

        // A picked suggestion (found by reference in our own list), or else exactly one valid address in the box
        void Submit(object? suggestion)
        {
            timer.Stop();
            if (suggestion is not null && suggestions.FindIndex(s => ReferenceEquals(s.View, suggestion)) is >= 0 and var index)
            {
                Add(suggestions[index].Person);
                return;
            }

            var text = box.Text.Trim();
            if (CalendarViewModel.IsAddress(text))
            {
                Add(new Contact("", text));
            }
        }

        void Add(Contact person)
        {
            chosen = null;
            search.Cancel();
            if (picked.Count >= FreeBusyLookup.MaxPeople || picked.Exists(p => string.Equals(p.Email, person.Email, StringComparison.OrdinalIgnoreCase)))
            {
                box.Text = "";
                return;
            }

            picked.Add(person);
            box.Text = "";
            box.ItemsSource = null;
            Render();
        }

        // One Row Per Picked Person (the name, with the address under it in secondary text; an address alone when
        // there's no name), With A Remove Button
        void Render()
        {
            list.Children.Clear();
            removes.Clear();
            list.Visibility = picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var person in picked)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // The ID goes on the first line: a Grid isn't in the automation tree
                var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var text = new TextBlock { Text = person.Name.Length > 0 ? person.Name : person.Email, TextTrimming = TextTrimming.CharacterEllipsis };
                AutomationProperties.SetAutomationId(text, $"PickedPerson_{person.Email}");
                lines.Children.Add(text);
                if (person.Name.Length > 0)
                {
                    lines.Children.Add(new TextBlock
                    {
                        Text = person.Email,
                        Style = (Style)Application.Current.Resources["LeafSecondaryTextStyle"],
                        TextWrapping = TextWrapping.NoWrap,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                }

                row.Children.Add(lines);

                var remove = new Button
                {
                    Content = new FontIcon { Glyph = "", FontSize = 12 },
                    Style = (Style)Application.Current.Resources["LeafIconButtonStyle"],
                };
                AutomationProperties.SetName(remove, $"Remove {person.Email}");
                ToolTipService.SetToolTip(remove, $"Remove {person.Email}");
                remove.Click += (_, _) =>
                {
                    var index = picked.IndexOf(person);
                    var how = remove.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
                    picked.Remove(person);
                    Render();

                    // Focus stays in the list: the row now in this one's place, the one above it, or the box when none are left
                    Control next = picked.Count > 0 ? removes[Math.Min(index, picked.Count - 1)] : box;
                    next.Focus(how);
                };
                removes.Add(remove);
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Children.Add(row);
            }

            dialog.IsPrimaryButtonEnabled = picked.Count is > 0 and <= FreeBusyLookup.MaxPeople;
        }
    }
}
