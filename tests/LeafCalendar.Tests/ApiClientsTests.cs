using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiClientsTests
{
    [Fact]
    public void PipeName_Default_IsTheUserOnly()
    {
        Assert.Equal("leaf-calendar-devin green", ApiClients.PipeName("Devin Green", "default"));
    }

    [Fact]
    public void PipeName_OtherProfile_AddsIt()
    {
        Assert.Equal("leaf-calendar-devin-uitest-1234", ApiClients.PipeName("Devin", "uitest-1234"));
    }

    [Theory]
    [InlineData("Windows PowerShell", "Microsoft® Windows® Operating System", "Windows PowerShell")]
    [InlineData("  Raycast  ", "Raycast", "Raycast")]
    [InlineData("", "Node.js", "Node.js")]
    [InlineData(null, null, "my-tool")]
    [InlineData(" ", " ", "my-tool")]
    public void DisplayName_PrefersTheDescription(string? description, string? product, string expected)
    {
        Assert.Equal(expected, ApiClients.DisplayName(description, product, @"C:\Tools\my-tool.exe"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe", true)]
    [InlineData(@"C:\Python312\python.exe", true)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Raycast\Raycast.exe", false)]
    public void IsGeneralRuntime_KnowsScriptHosts(string path, bool expected)
    {
        Assert.Equal(expected, ApiClients.IsGeneralRuntime(path));
    }

    private const string RaycastFolder = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.7.3.0_x64__qypenmj9wpt2a";

    [Theory]
    [InlineData(RaycastFolder + @"\Raycast\backend\node.exe", true)]
    [InlineData(RaycastFolder + @"\raycast\BACKEND\node.exe", true)]
    [InlineData(RaycastFolder + @"\", false)]
    [InlineData(RaycastFolder, false)]
    [InlineData(RaycastFolder + @"-evil\node.exe", false)]
    [InlineData(RaycastFolder + @"\..\Other\node.exe", false)]
    [InlineData(@"C:\Program Files\nodejs\node.exe", false)]
    [InlineData("", false)]
    public void IsInFolder_OnlyFilesInsideIt(string path, bool expected)
    {
        Assert.Equal(expected, ApiClients.IsInFolder(path, RaycastFolder));
    }

    [Fact]
    public void IsInFolder_FolderWithTrailingSeparator_StillMatches()
    {
        Assert.True(ApiClients.IsInFolder(RaycastFolder + @"\Raycast\backend\node.exe", RaycastFolder + @"\"));
    }

    [Theory]
    [InlineData("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties><DisplayName>Raycast</DisplayName></Properties></Package>""", "Raycast")]
    [InlineData("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties><DisplayName>  Raycast  </DisplayName></Properties></Package>""", "Raycast")]
    [InlineData("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties><DisplayName>ms-resource:AppName</DisplayName></Properties></Package>""", null)]
    [InlineData("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties><DisplayName> </DisplayName></Properties></Package>""", null)]
    [InlineData("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"><Properties /></Package>""", null)]
    [InlineData("not xml", null)]
    public void ManifestDisplayName_ReadsThePackagesOwnName(string manifest, string? expected)
    {
        Assert.Equal(expected, ApiClients.ManifestDisplayName(manifest));
    }

    private const string Manifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Applications>
            <Application Id="App" Executable="{0}" />
            <Application Id="Helper" Executable="Raycast\Raycast.UIAccess.exe" />
          </Applications>
        </Package>
        """;

    [Theory]
    [InlineData(@"Raycast\Raycast.exe", RaycastFolder + @"\Raycast\Raycast.exe")]
    [InlineData(@"..\Other\Evil.exe", null)]
    [InlineData(@"C:\Windows\notepad.exe", null)]
    [InlineData("", null)]
    public void ManifestProgram_FirstAppInsideThePackage(string executable, string? expected)
    {
        Assert.Equal(expected, ApiClients.ManifestProgram(Manifest.Replace("{0}", executable, StringComparison.Ordinal), RaycastFolder));
    }

    [Fact]
    public void ManifestProgram_NoApplicationsOrNotXml_IsNull()
    {
        Assert.Null(ApiClients.ManifestProgram("""<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" />""", RaycastFolder));
        Assert.Null(ApiClients.ManifestProgram("not xml", RaycastFolder));
    }
}
