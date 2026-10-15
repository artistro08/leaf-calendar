using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Makes the clear button of a password box styled with <c>LeafPasswordBoxStyle</c> empty the box. The stock
/// <see cref="PasswordBox"/> is sealed and has no clear button, so the style adds one and this wires its click.
/// </summary>
/// <remarks>
/// Clearing keeps focus in the box, as TextBox's own clear button does, so the box's LostFocus (where a page saves it)
/// runs once the user moves on.
/// </remarks>
internal static class PasswordClearButton
{
    /// <summary>Wires <paramref name="box"/>'s clear button once its template is in place.</summary>
    public static void Attach(PasswordBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box)
        {
            return;
        }

        box.Loaded -= OnLoaded;
        if (TemplatePart.Find<Button>(box, "ClearButton") is { } clear)
        {
            clear.Click += (_, _) =>
            {
                box.Password = "";
                box.Focus(FocusState.Programmatic);
            };
        }
    }
}
