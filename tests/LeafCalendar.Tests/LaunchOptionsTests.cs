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

    [Fact]
    public void Parse_FakeGoogleLoopback_AcceptsAndNormalizes()
    {
        var options = LaunchOptions.Parse(["--profile", "uitest-abc", "--fake-google", "http://127.0.0.1:4567", "--start-date", "2026-10-01"]);

        Assert.Equal(new Uri("http://127.0.0.1:4567/"), options.FakeGoogle);
        Assert.Equal(new DateOnly(2026, 10, 1), options.StartDate);
    }

    [Theory]
    [InlineData("https://127.0.0.1:4567/")]
    [InlineData("http://example.com/")]
    [InlineData("http://192.168.1.5:80/")]
    [InlineData("not a url")]
    [InlineData("file:///C:/fake")]
    public void Parse_FakeGoogleNotLoopbackHttp_Ignored(string value)
    {
        var options = LaunchOptions.Parse(["--profile", "uitest-abc", "--fake-google", value, "--start-date", "2026-10-01"]);

        Assert.Null(options.FakeGoogle);
        Assert.Null(options.StartDate);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("work")]
    public void Parse_FakeGoogleOnRealProfile_Ignored(string profile)
    {
        var options = LaunchOptions.Parse(["--profile", profile, "--fake-google", "http://127.0.0.1:4567", "--start-date", "2026-10-01"]);

        Assert.Null(options.FakeGoogle);
        Assert.Null(options.StartDate);
    }

    [Fact]
    public void Parse_FakeGoogleWithNoProfile_Ignored()
    {
        Assert.Null(LaunchOptions.Parse(["--fake-google", "http://127.0.0.1:4567"]).FakeGoogle);
    }

    [Fact]
    public void Parse_StartDateWithoutFakeGoogle_Ignored()
    {
        Assert.Null(LaunchOptions.Parse(["--start-date", "2026-10-01"]).StartDate);
    }

    [Fact]
    public void Parse_NowWithFakeGoogle_IsKept()
    {
        var options = LaunchOptions.Parse(["--profile", "uitest-a", "--fake-google", "http://127.0.0.1:5000/", "--now", "2026-10-01T13:55:00-04:00"]);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 13, 55, 0, TimeSpan.FromHours(-4)), options.Now);
    }

    [Theory]
    [InlineData("--profile", "default")]
    [InlineData("--profile", "uitest-a")]
    public void Parse_NowWithoutFakeGoogle_IsIgnored(string flag, string profile)
    {
        Assert.Null(LaunchOptions.Parse([flag, profile, "--now", "2026-10-01T13:55:00-04:00"]).Now);
    }

    [Theory]
    [InlineData("2026-10-01T13:55:00")]
    [InlineData("tomorrow")]
    [InlineData("")]
    public void Parse_NowWithoutAnOffset_IsIgnored(string value)
    {
        Assert.Null(LaunchOptions.Parse(["--profile", "uitest-a", "--fake-google", "http://127.0.0.1:5000/", "--now", value]).Now);
    }
}
