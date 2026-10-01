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
        "MainWindow.xaml|spacing|0,-6,-8,0",   // Info badge overlaps the corner of its 32 px icon button
        "MainWindow.xaml|spacing|10,0,10,1",   // Today label sits 1 px high to line up with the caption glyphs (comment above the View menu)

        // Optical alignment tuned by hand (title bar lift, sidebar glyph centers); see the comments in each file
        "Views/CalendarPage.xaml|spacing|0,9,0,8",
        "Views/SidebarView.xaml|spacing|5,48,5,6",
        "Views/SidebarView.xaml|spacing|9,0,0,0",
        "Views/SidebarView.xaml|spacing|9,0,4,0",
        "Views/SidebarView.xaml|spacing|0,0,11,0",

        // A 3 px tall pill: a radius of half its height is a circle end, which is not a corner radius
        "Styles/LeafTheme.xaml|radius|1.5",

        // TODO: Task 9 fixes (owned by Track B)
        "Views/EventEditorView.xaml|spacing|6",
        "Views/EventEditorView.xaml|icon-button|{x:Bind RemoveId}",
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

    public static TheoryData<string> Files() =>
    [
        .. Directory.GetFiles(AppFolder, "*.xaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(AppFolder, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("bin/", StringComparison.Ordinal) && !f.StartsWith("obj/", StringComparison.Ordinal) && !f.StartsWith("AppPackages/", StringComparison.Ordinal)),
    ];

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
        var hits = Load(file).Descendants()
            .Where(e => ButtonTypes.Contains(e.Name.LocalName) && Attr(e, "Content") is null)
            .Where(e => e.Elements().FirstOrDefault(c => !c.Name.LocalName.Contains('.', StringComparison.Ordinal)) is { } child && IconTypes.Contains(child.Name.LocalName))
            .Where(e => Attr(e, "AutomationProperties.Name") is null || Attr(e, "ToolTipService.ToolTip") is null)
            .Select(e => (e, Attr(e, "AutomationProperties.AutomationId") ?? "(no id)"));

        AssertNone(Failures(file, "icon-button", hits));
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
        string[] names = ["Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing"];
        var hits = Load(file).Descendants()
            .SelectMany(e => e.Attributes().Where(a => names.Contains(a.Name.LocalName)).Select(a => (e, a.Value)))
            .Where(p => !p.Value.StartsWith('{') && p.Value.Split(',').Any(v => double.TryParse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) && !Scale.Contains(Math.Abs(n))))
            .Select(p => (p.e, p.Value));

        AssertNone(Failures(file, "spacing", hits));
    }

    // Section 3: Corner Radii 4 And 8 (Or Theme Resources); Circles Are Allowlisted
    [Theory, MemberData(nameof(Files))]
    public void CornerRadii_UseTheScale(string file)
    {
        var hits = Load(file).Descendants()
            .Where(e => Attr(e, "CornerRadius") is { } v && !v.StartsWith('{'))
            .Where(e => Attr(e, "CornerRadius")!.Split(',').Any(v => double.TryParse(v.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var n) && !Radii.Contains(n)))
            .Select(e => (e, Attr(e, "CornerRadius")!));

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
