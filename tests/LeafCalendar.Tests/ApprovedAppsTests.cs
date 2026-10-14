using LeafCalendar.Core.Settings;

namespace LeafCalendar.Tests;

public sealed class ApprovedAppsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private const string RaycastKey = @"Raycast.Raycast_qypenmj9wpt2a!Raycast\backend\node.exe";
    private const string Raycast273 = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.7.3.0_x64__qypenmj9wpt2a\Raycast\backend\node.exe";
    private const string Raycast28 = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.8.0.0_x64__qypenmj9wpt2a\Raycast\backend\node.exe";
    private const string Raycast28Icon = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.8.0.0_x64__qypenmj9wpt2a\Raycast\Raycast.exe";
    private const string Raycast29 = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.9.0.0_x64__qypenmj9wpt2a\Raycast\backend\node.exe";
    private const string Raycast29Icon = @"C:\Program Files\WindowsApps\Raycast.Raycast_2.9.0.0_x64__qypenmj9wpt2a\Raycast\Raycast.exe";

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

        Assert.True(settings.IsApproved(@"c:\apps\raycast\NODE.EXE", packageKey: null));
        Assert.False(settings.IsApproved(@"C:\Apps\Other\node.exe", packageKey: null));
    }

    [Fact]
    public void IsApproved_AppApprovedInsideItsPackage_OnlyWhileItStillRunsAsThatPackage()
    {
        var settings = new LeafSettings().WithApprovedApp(new ApprovedApp(@"C:\Program Files\WindowsApps\Raycast\node.exe", "Raycast", At, @"C:\Program Files\WindowsApps\Raycast\Raycast.exe"));

        Assert.True(settings.IsApproved(@"C:\Program Files\WindowsApps\Raycast\node.exe", packageKey: "Raycast.Raycast_qypenmj9wpt2a!node.exe"));
        Assert.False(settings.IsApproved(@"C:\Program Files\WindowsApps\Raycast\node.exe", packageKey: null));
    }

    [Fact]
    public void IsApproved_PackagedApp_StillApprovedAfterItsPackageUpdates()
    {
        var settings = new LeafSettings().WithApprovedApp(new ApprovedApp(Raycast28, "Raycast", At, Raycast28Icon, RaycastKey));

        Assert.True(settings.IsApproved(Raycast29, RaycastKey.ToUpperInvariant()));
        Assert.False(settings.IsApproved(Raycast29, null));
        Assert.False(settings.IsApproved(Raycast29, @"Other.App_qypenmj9wpt2a!Raycast\backend\node.exe"));
    }

    [Fact]
    public void Normalize_ApprovalsFromBeforePackageKeys_GetThemFromTheirInstallFolder_OneRowPerApp()
    {
        var settings = new LeafSettings
        {
            ApprovedApps =
            [
                new ApprovedApp(Raycast273, "Raycast", At),
                new ApprovedApp(Raycast28, "Raycast", At.AddDays(1), Raycast28Icon),
                new ApprovedApp(@"C:\Program Files\PowerShell\7\pwsh.exe", "PowerShell 7", At),
            ],
        }.Normalize();

        Assert.Equal([Raycast28, @"C:\Program Files\PowerShell\7\pwsh.exe"], settings.ApprovedApps.Select(a => a.Path));
        Assert.Equal(RaycastKey, settings.ApprovedApps[0].PackageKey);
        Assert.Null(settings.ApprovedApps[1].PackageKey);
        Assert.True(settings.IsApproved(Raycast29, RaycastKey));
    }

    [Fact]
    public void WithApprovedApp_SamePackage_ReplacesIt()
    {
        var settings = new LeafSettings()
            .WithApprovedApp(new ApprovedApp(Raycast28, "Old", At, Raycast28Icon, RaycastKey))
            .WithApprovedApp(new ApprovedApp(Raycast29, "New", At, null, RaycastKey));

        Assert.Equal("New", Assert.Single(settings.ApprovedApps).Name);
    }

    [Fact]
    public void WithAppMoved_UpdatesThePathAndIconOfThatPackagesApproval()
    {
        var settings = new LeafSettings()
            .WithApprovedApp(new ApprovedApp(Raycast28, "Raycast", At, Raycast28Icon, RaycastKey))
            .WithApprovedApp(App(@"C:\Apps\tool.exe", "Tool"))
            .WithAppMoved(RaycastKey, Raycast29, Raycast29Icon);

        var raycast = settings.ApprovedApps[0];
        Assert.Equal(Raycast29, raycast.Path);
        Assert.Equal(Raycast29Icon, raycast.IconPath);
        Assert.Equal(At, raycast.ApprovedAt);
        Assert.Equal(@"C:\Apps\tool.exe", settings.ApprovedApps[1].Path);
    }

    [Fact]
    public void IsApproved_AppApprovedOutsideAPackage_EitherWay()
    {
        var settings = new LeafSettings().WithApprovedApp(App(@"C:\Apps\Raycast\node.exe"));

        Assert.True(settings.IsApproved(@"C:\Apps\Raycast\node.exe", packageKey: "Raycast.Raycast_qypenmj9wpt2a!node.exe"));
        Assert.True(settings.IsApproved(@"C:\Apps\Raycast\node.exe", packageKey: null));
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

    [Theory]
    [InlineData(@"C:\Apps\Raycast\Raycast.exe", @"C:\Apps\Raycast\Raycast.exe")]
    [InlineData(@"Raycast\Raycast.exe", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalize_IconPath_OnlyAFullPath(string? iconPath, string? expected)
    {
        var settings = new LeafSettings { ApprovedApps = [App(@"C:\Apps\Raycast\node.exe") with { IconPath = iconPath }] }.Normalize();

        Assert.Equal(expected, Assert.Single(settings.ApprovedApps).IconPath);
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
