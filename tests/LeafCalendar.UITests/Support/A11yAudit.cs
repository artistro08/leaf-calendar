using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace LeafCalendar.UITests.Support;

/// <summary>Accessibility checks over a live UIA tree: names Narrator reads, and what Tab reaches.</summary>
public static class A11yAudit
{
    private static readonly ControlType[] Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink, ControlType.ListItem,
        ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner, ControlType.SplitButton, ControlType.TabItem,
        ControlType.TreeItem, ControlType.Document,
    ];

    /// <summary>On-screen interactive elements whose name is empty or a type name (an object's ToString leaking out).</summary>
    public static IReadOnlyList<string> Unnamed(AutomationElement root) =>
        [.. root.FindAllDescendants()
            .Where(e => Interactive.Contains(e.Properties.ControlType.ValueOrDefault) && !e.Properties.IsOffscreen.ValueOrDefault)
            .Where(e => IsBadName(e.Properties.Name.ValueOrDefault))
            .Select(e => $"{e.Properties.ControlType.ValueOrDefault} id='{e.Properties.AutomationId.ValueOrDefault}'")];

    private static bool IsBadName(string? name) =>
        string.IsNullOrWhiteSpace(name)
        || name.StartsWith("LeafCalendar.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.UI.", StringComparison.Ordinal)
        || name.StartsWith("Windows.", StringComparison.Ordinal);

    /// <summary>AutomationIds Tab reaches from the window's first stop, until focus comes back around (or the limit).</summary>
    public static HashSet<string> TabStops(LeafApp leaf, Window window, int limit = 200)
    {
        window.Focus();
        var seen = new HashSet<string>();
        string? first = null;

        for (var i = 0; i < limit; i++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            var focused = leaf.Focused();
            var id = focused?.Properties.AutomationId.ValueOrDefault ?? "";
            if (id.Length > 0 && id == first)
            {
                break;
            }

            first ??= id.Length > 0 ? id : null;
            seen.Add(id);

            // A Box Inside A Control (an AutoSuggestBox's or NumberBox's text box) Counts As Reaching The Control
            var parent = focused?.Parent;
            for (var depth = 0; parent is not null && depth < 3; depth++, parent = parent.Parent)
            {
                seen.Add(parent.Properties.AutomationId.ValueOrDefault ?? "");
            }
        }

        return seen;
    }
}
