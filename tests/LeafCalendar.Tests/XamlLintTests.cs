using System.Xml.Linq;

namespace LeafCalendar.Tests;

/// <summary>
/// Reads every XAML file in the app and checks the design standard's rules a machine can check: names and tooltips on
/// icon-only controls (Section 14), x:Bind only (Section 1), HighContrast theme entries (Section 5), the spacing and
/// radius scale (Section 3), live regions on InfoBars, and no fixed heights on text (Section 14, text scaling).
/// </summary>
public class XamlLintTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Known exceptions: "file|rule|detail", each with the reason in a comment. Keep it short.</summary>
    public static readonly HashSet<string> Allowed =
    [
        // Main Window: name and tooltip are set in code (MainWindow.SetWords), or the tooltip sits on the wrapper because a disabled button shows none
        "MainWindow.xaml|icon-button|OfflineIndicator",
        "MainWindow.xaml|icon-button|DetailsEditButton",
        "MainWindow.xaml|icon-button|DeleteEventButton",
        "MainWindow.xaml|spacing|0,-6,-8,0 (InfoBadge in ConflictsBadge)",   // Info badge overlaps the corner of its 32 px icon button
        "MainWindow.xaml|spacing|0,-6,-8,0 (InfoBadge in WaitingBadge)",
        "MainWindow.xaml|spacing|10,0,10,1 (Button in TodayButton)",   // Today label sits 1 px high to line up with the caption glyphs (comment above the View menu)
        "Styles/LeafTheme.xaml|spacing|10,0 (Setter in LeafToolbarButtonStyle)",   // Same 10 px sides as the Today button
        "Styles/LeafTheme.xaml|spacing|9,12,0,4 (Setter in LeafSectionHeaderStyle)",   // 9 on the sidebar's shared left edge (style comment)
        "Styles/LeafTheme.xaml|spacing|9,4,8,4 (Setter in LeafFoldHeaderButtonStyle)",   // The account email stays on the sidebar's shared 9 px left edge

        // Optical alignment tuned by hand (title bar lift, sidebar glyph centers); see the comments in each file
        "Views/CalendarPage.xaml|spacing|0,9,0,8 (TextBlock in PeriodTitle)",
        "Views/SidebarView.xaml|spacing|5,48,5,6 (Grid in Self)",
        "Views/SidebarView.xaml|spacing|9,0,0,0 (TextBlock in MiniMonthTitle)",
        "Views/SidebarView.xaml|spacing|9,0,4,0 (Grid in CalendarList)",
        "Views/SidebarView.xaml|spacing|0,0,11,0 (PinnedItemsControl in CalendarList)",
        "Views/DetailsPanel.xaml|spacing|8,8,0,6 (Button in ShortcutsButton)",   // 6 below, like the sidebar's settings button opposite it

        // Ported from PowerToys (ShortcutDialogContentControl's CondensedInfoBarStyle): the InfoBar template binds its own
        // TemplateSettings.IconElement and Foreground through RelativeSource TemplatedParent, as the stock InfoBar template does
        "Views/Settings/ShortcutDialogContentControl.xaml|binding|Child",
        "Views/Settings/ShortcutDialogContentControl.xaml|binding|Value",

        // Tray flyout page padding is 20, copied from Sony's flyout (design standard, "Flyout page padding")
        "Tray/TrayHost.xaml|spacing|20,20,20,16 (StackPanel in AgendaPanel)",

        // A 3 px tall pill and a 28 px circle: half the size is a circle end, which is not a corner radius
        "Styles/LeafTheme.xaml|radius|1.5",
        "Styles/LeafTheme.xaml|radius|14",
    ];

    static readonly string[] ButtonTypes = ["Button", "ToggleButton", "HyperlinkButton", "RepeatButton", "AppBarButton", "AppBarToggleButton", "DropDownButton", "SplitButton"];
    static readonly string[] IconTypes   = ["FontIcon", "SymbolIcon", "PathIcon", "BitmapIcon", "ImageIcon", "Image", "Viewbox"];
    static readonly HashSet<double> Scale = [0, 1, 2, 4, 8, 12, 16, 24, 32, 36, 48];
    static readonly HashSet<double> Radii = [0, 2, 4, 8];

    static string AppFolder => Path.Combine(FindRepoRoot(), "src", "LeafCalendar.App");

    // Walks up from the test binaries to the folder holding the solution file
    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeafCalendar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("LeafCalendar.slnx not found above the test binaries");
    }

    public static TheoryData<string> Files() => [.. AllFiles()];

    static IEnumerable<string> AllFiles() =>
        Directory.GetFiles(AppFolder, "*.xaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AppFolder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal) && !f.StartsWith("AppPackages/", StringComparison.Ordinal));

    static XDocument Load(string file) => XDocument.Load(Path.Combine(AppFolder, file), LoadOptions.SetLineInfo);

    static string Where(string file, XElement e) => $"{file}:{((System.Xml.IXmlLineInfo)e).LineNumber} <{e.Name.LocalName}>";

    static string? Attr(XElement e, string name) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    static List<string> Failures(string file, string rule, IEnumerable<(XElement Element, string Detail)> hits) =>
        [.. hits.Where(h => !Allowed.Contains($"{file}|{rule}|{h.Detail}")).Select(h => $"{Where(file, h.Element)} {rule}: {h.Detail}")];

    // Fails with the full list of findings (Assert.Empty truncates long ones)
    static void AssertNone(List<string> failures) => Assert.True(failures.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, failures));

    // Section 7 And 14: Icon-Only Buttons Have A Name And A Tooltip
    [Theory, MemberData(nameof(Files))]
    public void IconOnlyButtons_HaveNameAndTooltip(string file)
    {
        var styles = Styles();
        var hits = Load(file).Descendants()
            .Where(e => ButtonTypes.Contains(e.Name.LocalName) && IsIconOnly(e, styles))
            .Where(e => Attr(e, "AutomationProperties.Name") is null || Attr(e, "ToolTipService.ToolTip") is null)
            .Select(e => (e, Attr(e, "AutomationProperties.AutomationId") ?? "(no id)"));

        AssertNone(Failures(file, "icon-button", hits));
    }

    // Every keyed Style in the app, by key (a button's Style can set its Content)
    static Dictionary<string, XElement> Styles() =>
        AllFiles().SelectMany(f => Load(f).Descendants().Where(e => e.Name.LocalName == "Style" && e.Attribute(X + "Key") is not null))
            .GroupBy(s => (string)s.Attribute(X + "Key")!).ToDictionary(g => g.Key, g => g.First());

    // A glyph is text made only of private-use codepoints (Segoe Fluent Icons)
    static bool IsGlyph(string? text) => !string.IsNullOrWhiteSpace(text) && text.Trim().All(c => c is >= '' and <= '');

    static bool IsIcon(XElement? e) => e is not null && IconTypes.Contains(e.Name.LocalName);

    // Content as an attribute, a Button.Content property element, a direct child, or a Style setter
    static bool IsIconOnly(XElement button, Dictionary<string, XElement> styles, int depth = 0)
    {
        if (Attr(button, "Content") is { } content)
        {
            return IsGlyph(content);
        }

        var propertyElement = button.Elements().FirstOrDefault(c => c.Name.LocalName == button.Name.LocalName + ".Content");
        if (propertyElement is not null)
        {
            return IsIcon(propertyElement.Elements().FirstOrDefault());
        }

        if (button.Elements().FirstOrDefault(c => !c.Name.LocalName.Contains('.', StringComparison.Ordinal)) is { } child)
        {
            return IsIcon(child);
        }

        var key = Attr(button, "Style") is { } style && style.StartsWith("{StaticResource ", StringComparison.Ordinal) ? style[16..^1].Trim() : null;
        if (key is null || depth > 0 || !styles.TryGetValue(key, out var found))
        {
            return false;
        }

        var setter = found.Descendants().FirstOrDefault(s => s.Name.LocalName == "Setter" && Attr(s, "Property") == "Content");
        return setter is not null && (IsGlyph(Attr(setter, "Value")) || IsIcon(setter.Descendants().FirstOrDefault(d => IconTypes.Contains(d.Name.LocalName))));
    }

    // Section 1: x:Bind Only (AOT)
    [Theory, MemberData(nameof(Files))]
    public void Bindings_AreXBindOnly(string file)
    {
        var hits = Load(file).Descendants()
            .SelectMany(e => e.Attributes().Select(a => (e, a)))
            .Where(p => p.a.Value.Contains("{Binding", StringComparison.Ordinal) || p.a.Name.LocalName == "DisplayMemberPath")
            .Select(p => (p.e, p.a.Name.LocalName));

        AssertNone(Failures(file, "binding", hits));
    }

    // Section 5: Every Light/Dark Override Has A HighContrast Entry With The Same Keys
    [Theory, MemberData(nameof(Files))]
    public void ThemeDictionaries_HaveHighContrastWithSameKeys(string file)
    {
        var hits = new List<(XElement, string)>();
        foreach (var themes in Load(file).Descendants().Where(e => e.Name.LocalName == "ResourceDictionary.ThemeDictionaries"))
        {
            var byKey = themes.Elements().ToDictionary(d => (string?)d.Attribute(X + "Key") ?? "", d => d.Elements().Select(r => (string?)r.Attribute(X + "Key")).ToHashSet());
            var keys  = byKey.Where(p => p.Key != "HighContrast").SelectMany(p => p.Value).ToHashSet();
            if (!byKey.TryGetValue("HighContrast", out var contrast))
            {
                hits.Add((themes, "no HighContrast dictionary"));
                continue;
            }

            hits.AddRange(keys.Except(contrast).Select(k => (themes, $"HighContrast lacks {k}")));
        }

        AssertNone(Failures(file, "high-contrast", hits));
    }

    // Section 3: Spacing Only From The Scale (2, 4, 8, 12, 16, 24, 32, 36, plus 0, 1, 48)
    [Theory, MemberData(nameof(Files))]
    public void Spacing_UsesTheScale(string file)
    {
        var hits = Metrics(Load(file), "Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing")
            .Where(p => Numbers(p.Value).Any(n => !Scale.Contains(Math.Abs(n))))
            .Select(p => (p.Element, Detail: $"{p.Value} ({Context(p.Element)})"))
            .ToList();

        // Identical findings in one file are told apart by their order in it (#1, #2, ...), so an allowlist entry covers one element
        hits = [.. hits.GroupBy(h => h.Detail).SelectMany(g => g.Select((h, i) => (h.Element, g.Count() > 1 ? $"{h.Detail} #{i + 1}" : h.Detail)))];

        AssertNone(Failures(file, "spacing", hits));
    }

    // Every value set for one of these properties: attributes, <Setter Property Value>, <Prop.Name> text, and <Thickness>/<CornerRadius> elements
    static IEnumerable<(XElement Element, string Value)> Metrics(XDocument doc, params string[] names)
    {
        foreach (var e in doc.Descendants())
        {
            foreach (var a in e.Attributes().Where(a => names.Contains(a.Name.LocalName)))
            {
                yield return (e, a.Value);
            }

            if (e.Name.LocalName == "Setter" && Attr(e, "Property") is { } property && names.Contains(property[(property.LastIndexOf('.') + 1)..]) && Attr(e, "Value") is { } value)
            {
                yield return (e, value);
            }

            var isPropertyElement = e.Name.LocalName.Contains('.', StringComparison.Ordinal) && names.Contains(e.Name.LocalName[(e.Name.LocalName.LastIndexOf('.') + 1)..]);
            if ((isPropertyElement || e.Name.LocalName is "Thickness" or "CornerRadius") && !e.HasElements && e.Value.Trim().Length > 0)
            {
                yield return (e, e.Value.Trim());
            }
        }
    }

    // Numbers in a "8,4" or "8 4" value; theme resources ({...}) and non-numbers give none
    static IEnumerable<double> Numbers(string value) =>
        value.StartsWith('{') ? [] : value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(v => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.NaN).Where(n => !double.IsNaN(n));

    // Names the element for the allowlist: the nearest x:Name or AutomationId up the tree, plus the element's own tag
    static string Context(XElement e)
    {
        var named = e.AncestorsAndSelf().Select(a => (string?)a.Attribute(X + "Name") ?? Attr(a, "AutomationId") ?? Attr(a, "AutomationProperties.AutomationId") ?? (string?)a.Attribute(X + "Key")).FirstOrDefault(n => n is not null);
        return $"{e.Name.LocalName} in {named ?? "(unnamed)"}";
    }

    // Section 3: Corner Radii 4 And 8 (Or Theme Resources); Circles Are Allowlisted
    [Theory, MemberData(nameof(Files))]
    public void CornerRadii_UseTheScale(string file)
    {
        var hits = Metrics(Load(file), "CornerRadius")
            .Where(p => Numbers(p.Value).Any(n => !Radii.Contains(n)))
            .Select(p => (p.Element, p.Value));

        AssertNone(Failures(file, "radius", hits));
    }

    // Section 14: Narrator Hears Changes In InfoBars (undo notice, errors)
    [Theory, MemberData(nameof(Files))]
    public void InfoBars_HaveALiveSetting(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => e.Name.LocalName == "InfoBar" && Attr(e, "AutomationProperties.LiveSetting") is null)
            .Select(e => (e, Attr(e, "AutomationProperties.AutomationId") ?? "(no id)"));

        AssertNone(Failures(file, "live-region", hits));
    }

    // XAML Roots Leaf Can Load (a ComboBox-rooted x:Class failed to parse when built, so the calendar page never opened)
    [Theory, MemberData(nameof(Files))]
    public void Roots_AreLoadableTypes(string file)
    {
        var root = Load(file).Root!;
        var hits = root.Name.LocalName is "Application" or "Window" or "Page" or "UserControl" or "ResourceDictionary"
            ? []
            : new[] { (root, root.Name.LocalName) };

        AssertNone(Failures(file, "root", hits));
    }

    // Section 14: Text Follows The System Text Size, So Text Elements Never Get A Fixed Height
    [Theory, MemberData(nameof(Files))]
    public void TextElements_HaveNoFixedHeight(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => e.Name.LocalName is "TextBlock" or "RichTextBlock" && (Attr(e, "Height") is not null || Attr(e, "MaxHeight") is not null))
            .Select(e => (e, Attr(e, "Text") ?? Attr(e, "AutomationProperties.AutomationId") ?? "(text)"));

        AssertNone(Failures(file, "text-height", hits));
    }
}
