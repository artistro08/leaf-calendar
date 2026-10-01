using LeafCalendar.App.Controls;
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
    public static async Task<IReadOnlyList<Contact>?> ShowAsync(XamlRoot root, CalendarViewModel vm, string title, string primaryText)
    {
        var picked      = new List<Contact>();
        var suggestions = new List<(ContactSuggestion View, Contact Person)>();
        using var search = new LatestSearch<ContactResults>();

        // Box
        var box = new AutoSuggestBox { PlaceholderText = "Name or email", UpdateTextOnSelect = false, MinWidth = 320 };
        AutomationProperties.SetAutomationId(box, "PeoplePickerBox");
        AutomationProperties.SetName(box, "Name or email");

        var hint   = new TextBlock { Text = $"Up to {FreeBusyLookup.MaxPeople} people.", FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        var list   = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        var layout = new StackPanel();
        layout.Children.Add(box);
        layout.Children.Add(hint);
        layout.Children.Add(list);

        var dialog = new ContentDialog
        {
            XamlRoot                 = root,
            Title                    = title,
            Content                  = layout,
            PrimaryButtonText        = primaryText,
            CloseButtonText          = "Cancel",
            DefaultButton            = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled   = false,
        };
        dialog.Opened += (_, _) => hint.Foreground = LeafBrushes.SecondaryText(dialog.ActualTheme == ElementTheme.Dark);

        // Suggestions Wait Until Typing Pauses
        var timer = root.Content.DispatcherQueue.CreateTimer();
        timer.Interval    = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick       += (_, _) => vm.Fire(SuggestAsync, "people.suggest.failed");

        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            timer.Stop();
            timer.Start();
        };

        // A Picked Suggestion (found by reference in our own list) Or Exactly One Valid Address
        box.QuerySubmitted += (_, args) =>
        {
            timer.Stop();
            if (args.ChosenSuggestion is { } chosen && suggestions.FindIndex(s => ReferenceEquals(s.View, chosen)) is >= 0 and var index)
            {
                Add(suggestions[index].Person);
                return;
            }

            var text = box.Text.Trim();
            if (CalendarViewModel.IsAddress(text))
            {
                Add(new Contact("", text));
            }
        };

        var result = await dialog.ShowAsync();
        timer.Stop();
        search.Cancel();
        return result == ContentDialogResult.Primary ? picked : null;

        async Task SuggestAsync()
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || await search.RunAsync(ct => vm.SearchPeopleAsync(text, ct)) is not { } results)
            {
                return;
            }

            suggestions.Clear();
            suggestions.AddRange(results.Contacts.Select(c => (new ContactSuggestion(c.Name, c.Email), c)));
            box.ItemsSource = suggestions.Select(s => s.View).ToList();
            box.IsSuggestionListOpen = suggestions.Count > 0;
        }

        void Add(Contact person)
        {
            if (picked.Count >= FreeBusyLookup.MaxPeople || picked.Exists(p => string.Equals(p.Email, person.Email, StringComparison.OrdinalIgnoreCase)))
            {
                box.Text = "";
                return;
            }

            picked.Add(person);
            box.Text        = "";
            box.ItemsSource = null;
            Render();
        }

        // One Row Per Picked Person, With A Remove Button
        void Render()
        {
            list.Children.Clear();
            foreach (var person in picked)
            {
                var label = person.Name.Length > 0 ? $"{person.Name} <{person.Email}>" : person.Email;
                var row   = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                AutomationProperties.SetAutomationId(row, $"PickedPerson_{person.Email}");
                AutomationProperties.SetName(row, label);

                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

                var remove = new Button
                {
                    Content         = new FontIcon { Glyph = "", FontSize = 10 },
                    Width           = 32,
                    Height          = 32,
                    Padding         = new Thickness(0),
                    Background      = LeafBrushes.Transparent,
                    BorderThickness = new Thickness(0),
                };
                AutomationProperties.SetName(remove, $"Remove {person.Email}");
                ToolTipService.SetToolTip(remove, $"Remove {person.Email}");
                remove.Click += (_, _) =>
                {
                    picked.Remove(person);
                    Render();
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                list.Children.Add(row);
            }

            dialog.IsPrimaryButtonEnabled = picked.Count is > 0 and <= FreeBusyLookup.MaxPeople;
        }
    }
}
