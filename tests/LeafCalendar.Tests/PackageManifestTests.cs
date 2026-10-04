using System.Xml.Linq;

namespace LeafCalendar.Tests;

/// <summary>Checks on the app project and package manifest that keep the MSIX small and Store-ready.</summary>
public class PackageManifestTests
{
    /// <summary>The folder holding <c>LeafCalendar.slnx</c>.</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeafCalendar.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root (LeafCalendar.slnx) not found.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    // Windows App SDK: Component Packages Only, None Of The AI/ML/Search/Widgets Ones (spec 13)
    [Fact]
    public void AppProject_UsesComponentPackagesWithoutAi()
    {
        var references = XDocument.Parse(Read("src", "LeafCalendar.App", "LeafCalendar.App.csproj"))
            .Descendants("PackageReference").Select(r => (string?)r.Attribute("Include")).ToList();

        Assert.DoesNotContain("Microsoft.WindowsAppSDK", references);
        Assert.Contains("Microsoft.WindowsAppSDK.WinUI", references);
        Assert.Contains("Microsoft.WindowsAppSDK.Foundation", references);
        Assert.DoesNotContain(references, r => r is "Microsoft.WindowsAppSDK.AI" or "Microsoft.WindowsAppSDK.ML" or "Microsoft.WindowsAppSDK.Search" or "Microsoft.WindowsAppSDK.Widgets");
    }

    // The Sign-In Pages' "Open Leaf Calendar" Link (leaf-calendar:) Opens Leaf
    [Fact]
    public void Manifest_RegistersLeafCalendarProtocol()
    {
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var extension = XDocument.Parse(Read("src", "LeafCalendar.App", "Package.appxmanifest"))
            .Descendants(uap + "Extension").Single(e => (string?)e.Attribute("Category") == "windows.protocol");

        Assert.Equal("leaf-calendar", (string?)extension.Element(uap + "Protocol")?.Attribute("Name"));
    }

    // Icons: Every Manifest Image At Every Scale, The Taskbar Sizes, And The Window Icon (tools/make-icons.ps1; the daily
    // tray icons are checked in TrayGlyphTests)
    [Fact]
    public void Assets_HoldEveryIcon()
    {
        var assets = Path.Combine(RepoRoot(), "src", "LeafCalendar.App", "Assets");
        var manifest = Read("src", "LeafCalendar.App", "Package.appxmanifest");
        string[] images = ["StoreLogo", "Square150x150Logo", "Square44x44Logo", "Wide310x150Logo", "SplashScreen"];
        int[] scales = [100, 125, 150, 200, 400];
        int[] targetSizes = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256];
        var expected = images.SelectMany(i => scales.Select(s => $"{i}.scale-{s}.png"))
            .Concat(targetSizes.SelectMany(s => (string[])[$"Square44x44Logo.targetsize-{s}.png", $"Square44x44Logo.targetsize-{s}_altform-unplated.png", $"Square44x44Logo.targetsize-{s}_altform-lightunplated.png"]))
            .Append("AppLogo.png")
            .Append("LeafCalendar.ico")
            .Append("ThirdPartyNotices.txt");

        Assert.All(images, i => Assert.Contains($@"Assets\{i}.png", manifest, StringComparison.Ordinal));
        Assert.All(expected, f => Assert.True(File.Exists(Path.Combine(assets, f)), $"Missing Assets/{f}"));
    }
}
