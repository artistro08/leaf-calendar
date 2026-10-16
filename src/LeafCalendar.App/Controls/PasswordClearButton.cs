using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// Makes the clear button of a password box styled with <c>LeafPasswordBoxStyle</c> empty the box. The stock
/// <see cref="PasswordBox"/> is sealed and has no clear button, so the style adds one and this wires its click.
/// </summary>
/// <remarks>
/// The button shows whenever the enabled box holds text, focused or not, so a saved value that shows as dots can be
/// cleared without first clicking into the box. Clearing moves focus into the box, as TextBox's own clear button does.
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
        if (TemplatePart.Find<Button>(box, "ClearButton") is not { } clear)
        {
            return;
        }

        // Shown While There's Something To Clear
        void Update() => clear.Visibility = box.IsEnabled && box.Password.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        box.PasswordChanged += (_, _) => Update();
        box.IsEnabledChanged += (_, _) => Update();
        Update();

        clear.Click += (_, _) =>
        {
            box.Password = "";
            box.Focus(FocusState.Programmatic);
        };
    }
}
