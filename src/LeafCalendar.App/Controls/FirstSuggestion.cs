using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Highlights a suggestion box's first suggestion (the first one that can be picked) whenever it lists new ones, so Enter
/// takes it; the arrow keys move on from there.
/// </summary>
/// <remarks>
/// The highlight is the box's own list selection, the one the arrow keys move, so the box raises SuggestionChosen for it
/// and passes it to QuerySubmitted as the chosen suggestion. A box using this keeps UpdateTextOnSelect off, so the
/// typed text stays as typed, and treats SuggestionChosen as a highlight, never as a pick. Typing drops the highlight
/// until the box lists suggestions for the new text, so Enter never takes one listed for what was typed before.
/// </remarks>
internal static class FirstSuggestion
{
    /// <summary>Starts highlighting <paramref name="box"/>'s first suggestion once its template is in place.</summary>
    public static void Highlight(AutoSuggestBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox box)
        {
            return;
        }

        box.Loaded -= OnLoaded;
        if (TemplatePart.Find<Popup>(box, "SuggestionsPopup") is { Child: Border { Child: ListView list } })
        {
            // Once the new rows are laid out, so a row that can't be picked shows as one
            list.Items.VectorChanged += (_, _) => box.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => SelectFirst(list));
            box.TextChanged += (_, args) =>
            {
                if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                {
                    list.SelectedIndex = -1;
                }
            };
        }
    }

    private static void SelectFirst(ListView list)
    {
        if (list.SelectedIndex >= 0)
        {
            return;
        }

        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not Control { IsEnabled: false })
            {
                list.SelectedIndex = i;
                return;
            }
        }
    }
}
