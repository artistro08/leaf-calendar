using LeafCalendar.App.Controls;
using LeafCalendar.App.ViewModels;
using LeafCalendar.Core.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Views;

/// <summary>
/// The side-by-side conflict review (spec 5.5): yours and Google's, with the differing fields highlighted. "Keep mine"
/// or "Keep Google's" decides one conflict and moves to the next; "Decide later" leaves the rest waiting.
/// </summary>
/// <remarks>
/// Event text is untrusted, so every value is a plain <see cref="TextBlock"/>. When Google's copy isn't available
/// (deleted there, or unreadable), the wording stays neutral: keeping yours saves it on Google as a new event, and
/// the second button discards it.
/// </remarks>
public static class ConflictDialog
{
    /// <summary>Goes through every open conflict, one dialog each. <paramref name="dark"/> comes from the caller's own element (never read back through the XamlRoot).</summary>
    public static async Task ReviewAsync(XamlRoot root, CalendarViewModel vm, bool dark)
    {
        ArgumentNullException.ThrowIfNull(vm);

        foreach (var seq in vm.Conflicts().Select(c => c.Entry.Seq).ToList())
        {
            // Re-Read Each One (a sync or an earlier answer may have changed or settled it)
            if (vm.Conflicts().FirstOrDefault(c => c.Entry.Seq == seq) is not { } conflict)
            {
                continue;
            }

            var result = await Build(root, vm, conflict, dark).ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                vm.KeepMine(conflict);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                vm.KeepGoogles(conflict);
            }
            else
            {
                return;
            }
        }
    }

    static ContentDialog Build(XamlRoot root, CalendarViewModel vm, ConflictInfo conflict, bool dark)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Header
        AddRow(grid, [Cell("", dark, secondary: true), Cell("Yours", dark, bold: true), Cell("Google's", dark, bold: true)]);

        // Fields (rows empty on both sides are skipped; differing rows get a bold label and a highlight, so it's never color alone)
        foreach (var field in vm.Compare(conflict).Where(f => f.Mine.Length > 0 || f.Google.Length > 0))
        {
            var mine   = Cell(field.Mine, dark);
            var google = Cell(field.Google, dark);
            AutomationProperties.SetAutomationId(mine, $"ConflictMine_{field.Field.Replace(" ", "", StringComparison.Ordinal)}");
            AutomationProperties.SetAutomationId(google, $"ConflictGoogle_{field.Field.Replace(" ", "", StringComparison.Ordinal)}");
            if (field.Differs)
            {
                AutomationProperties.SetItemStatus(mine, "differs");
                AutomationProperties.SetItemStatus(google, "differs");
            }

            AddRow(grid, [Cell(field.Field, dark, bold: field.Differs, secondary: !field.Differs), Highlight(mine, field.Differs, dark), Highlight(google, field.Differs, dark)]);
        }

        // Google's Copy Isn't Available (deleted there, or it couldn't be read)
        var hasGoogle = conflict.GoogleJson is not null;
        var question  = hasGoogle
            ? "This event changed on Google after you edited it here. Which version do you want to keep?"
            : conflict.Entry.Operation == OutboxOperation.Delete
                ? "Google's copy of this event isn't available, so your delete couldn't be checked. Either way, the event leaves Leaf."
                : "Google's copy of this event isn't available, so your change couldn't be sent. Keep yours to save it on Google as a new event, or discard it.";

        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(grid);

        var scroll = new ScrollViewer { Content = body, MaxHeight = 420 };
        ScrollIndicator.ShowOnHover(scroll);

        return new ContentDialog
        {
            XamlRoot            = root,
            RequestedTheme      = dark ? ElementTheme.Dark : ElementTheme.Light,
            Title               = "Review a change",
            Content             = scroll,
            PrimaryButtonText   = "Keep mine",
            SecondaryButtonText = hasGoogle ? "Keep Google's" : "Discard mine",
            CloseButtonText     = "Decide later",
            DefaultButton       = ContentDialogButton.Close,
        };
    }

    static void AddRow(Grid grid, FrameworkElement[] cells)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetRow(cells[i], grid.RowDefinitions.Count - 1);
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }
    }

    // Event content, shown as plain text only
    static TextBlock Cell(string text, bool dark, bool bold = false, bool secondary = false)
    {
        var cell = new TextBlock
        {
            Text                   = text,
            TextWrapping           = TextWrapping.Wrap,
            IsTextSelectionEnabled = !bold && !secondary,
            FontWeight             = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };

        // Secondary labels use the theme's TextFillColorSecondary (code-built, so picked per theme)
        if (secondary)
        {
            cell.Foreground = LeafBrushes.SecondaryText(dark);
        }

        return cell;
    }

    static FrameworkElement Highlight(TextBlock cell, bool differs, bool dark) =>
        differs ? new Border { Child = cell, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(4), Background = LeafBrushes.CautionBackground(dark) } : cell;
}
