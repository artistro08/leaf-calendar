using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class ApprovedAppsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static ApprovedApp App(string path, string name = "Raycast") => new(path, name, At);

    [Fact]
    public void Defaults_AreOffAndEmpty()
    {
        var settings = new LeafSettings().Normalize();

        Assert.False(settings.AllowApiConnections);
        Assert.Empty(settings.ApprovedApps);
    }

    [Fact]
    public void IsApproved_IgnoresCase()
    {
        var settings = new LeafSettings().WithApprovedApp(App(@"C:\Apps\Raycast\node.exe"));

        Assert.True(settings.IsApproved(@"c:\apps\raycast\NODE.EXE"));
        Assert.False(settings.IsApproved(@"C:\Apps\Other\node.exe"));
    }

    [Fact]
    public void WithApprovedApp_SamePath_ReplacesIt()
    {
        var settings = new LeafSettings()
            .WithApprovedApp(App(@"C:\a.exe", "Old"))
            .WithApprovedApp(App(@"C:\A.EXE", "New"));

        Assert.Equal("New", Assert.Single(settings.ApprovedApps).Name);
    }

    [Fact]
    public void WithoutApprovedApp_RemovesIt()
    {
        var settings = new LeafSettings().WithApprovedApp(App(@"C:\a.exe")).WithoutApprovedApp(@"c:\A.exe");

        Assert.Empty(settings.ApprovedApps);
    }

    [Fact]
    public void Normalize_DropsBlankRelativeAndRepeatedPaths_KeepingTheLast()
    {
        var settings = new LeafSettings
        {
            ApprovedApps = [App(""), App("relative.exe"), App(@"C:\x\tool.exe", "First"), App(@"C:\X\TOOL.EXE", "Second")],
        }.Normalize();

        var app = Assert.Single(settings.ApprovedApps);
        Assert.Equal(@"C:\X\TOOL.EXE", app.Path);
        Assert.Equal("Second", app.Name);
    }

    [Fact]
    public void Normalize_BlankName_BecomesTheFileName()
    {
        var settings = new LeafSettings { ApprovedApps = [App(@"C:\x\tool.exe", "  ")] }.Normalize();

        Assert.Equal("tool", Assert.Single(settings.ApprovedApps).Name);
    }

    [Fact]
    public void Normalize_KeepsTheNewestFifty()
    {
        var settings = new LeafSettings
        {
            ApprovedApps = [.. Enumerable.Range(0, 60).Select(i => App($@"C:\apps\{i}.exe"))],
        }.Normalize();

        Assert.Equal(LeafSettings.MaxApprovedApps, settings.ApprovedApps.Count);
        Assert.Equal(@"C:\apps\10.exe", settings.ApprovedApps[0].Path);
    }

    [Fact]
    public void Normalize_Twice_IsEqual()
    {
        var once = new LeafSettings { ApprovedApps = [App(@"C:\a.exe")] }.Normalize();

        Assert.Equal(once, once.Normalize());
    }
}
