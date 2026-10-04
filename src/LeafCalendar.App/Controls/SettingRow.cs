using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LeafCalendar.App.Controls;

/// <summary>
/// One Windows 11 Settings row: a card with an optional 16 px icon, a header and a description on the left, and its
/// control (the content) on the right. The look is the implicit style in <c>Styles/LeafTheme.xaml</c>. Give the
/// control inside the row an <c>AutomationProperties.Name</c> matching the header.
/// </summary>
public sealed partial class SettingRow : ContentControl
{
    /// <summary>Identifies <see cref="Header"/>.</summary>
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    /// <summary>Identifies <see cref="Description"/>.</summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata("", OnDescriptionChanged));

    /// <summary>Identifies <see cref="Glyph"/>.</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingRow), new PropertyMetadata("", OnGlyphChanged));

    /// <summary>Identifies <see cref="DescriptionVisibility"/>.</summary>
    public static readonly DependencyProperty DescriptionVisibilityProperty =
        DependencyProperty.Register(nameof(DescriptionVisibility), typeof(Visibility), typeof(SettingRow), new PropertyMetadata(Visibility.Collapsed));

    /// <summary>Identifies <see cref="IconVisibility"/>.</summary>
    public static readonly DependencyProperty IconVisibilityProperty =
        DependencyProperty.Register(nameof(IconVisibility), typeof(Visibility), typeof(SettingRow), new PropertyMetadata(Visibility.Collapsed));

    /// <summary>The setting's name, in sentence case.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>One or two plain sentences under the header; hidden when empty.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>A Segoe Fluent Icons glyph for the left edge; no icon when empty.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Shown when <see cref="Description"/> has text (for the template).</summary>
    public Visibility DescriptionVisibility
    {
        get => (Visibility)GetValue(DescriptionVisibilityProperty);
        private set => SetValue(DescriptionVisibilityProperty, value);
    }

    /// <summary>Shown when <see cref="Glyph"/> has text (for the template).</summary>
    public Visibility IconVisibility
    {
        get => (Visibility)GetValue(IconVisibilityProperty);
        private set => SetValue(IconVisibilityProperty, value);
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).DescriptionVisibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;

    private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingRow)d).IconVisibility = string.IsNullOrEmpty(e.NewValue as string) ? Visibility.Collapsed : Visibility.Visible;
}
