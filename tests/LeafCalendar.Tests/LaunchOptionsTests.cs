using LeafCalendar.Core.Hosting;

namespace LeafCalendar.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void Parse_NoArgs_UsesDefaults()
    {
        Assert.Equal(new LaunchOptions("default", false), LaunchOptions.Parse([]));
    }

    [Fact]
    public void Parse_ProfileAndProbe_ReadsBoth()
    {
        Assert.Equal(new LaunchOptions("uitest-abc_1", true), LaunchOptions.Parse(["--profile", "uitest-abc_1", "--tray-probe"]));
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("C:")]
    public void Parse_UnsafeProfile_FallsBackToDefault(string profile)
    {
        Assert.Equal("default", LaunchOptions.Parse(["--profile", profile]).Profile);
    }

    [Fact]
    public void LeafPaths_Profile_BuildsFoldersUnderRoot()
    {
        var paths = new LeafPaths(@"C:\Local", "work");

        Assert.Equal(@"C:\Local\profiles\work\leaf.db", paths.DatabasePath);
        Assert.Equal(@"C:\Local\profiles\work\Logs", paths.LogDirectory);
    }
}
