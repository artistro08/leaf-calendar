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

    static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

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
}
