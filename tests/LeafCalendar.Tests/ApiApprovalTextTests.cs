using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiApprovalTextTests
{
    private static ApiCaller Caller(string path, string name) => new(path, name, 1, ApiHello.None);

    [Fact]
    public void Headline_NamesTheApp()
    {
        Assert.Equal("Raycast wants to connect to Leaf", ApiApprovalText.Headline(Caller(@"C:\Apps\Raycast.exe", "Raycast")));
    }

    [Fact]
    public void Headline_DropsCharactersThatReorderText()
    {
        Assert.Equal("Raycast wants to connect to Leaf", ApiApprovalText.Headline(Caller(@"C:\Apps\Raycast.exe", "Ray\u202Ecast")));
    }

    [Fact]
    public void Headline_NameThatCleansToNothing_UsesTheFileName()
    {
        Assert.Equal("Raycast wants to connect to Leaf", ApiApprovalText.Headline(Caller(@"C:\Apps\Raycast.exe", "\u202E\u200B")));
    }

    [Fact]
    public void Path_DropsCharactersThatReorderText()
    {
        Assert.Equal(@"C:\Apps\evil\Raycast.exe", ApiApprovalText.DisplayPath(Caller("C:\\Apps\\e\u202Evil\\Raycast.exe", "Raycast")));
    }

    [Fact]
    public void Body_SaysWhatTheAppCanDo()
    {
        Assert.Equal("It will be able to see your events, create and move them, and join meetings.", ApiApprovalText.Body);
    }

    [Fact]
    public void RuntimeWarning_GeneralRuntime_WarnsOfFullAccess()
    {
        Assert.Equal("Allowing node.exe to connect will give access to all of your calendars.", ApiApprovalText.RuntimeWarning(Caller(@"C:\nodejs\node.exe", "Node.js")));
    }

    [Fact]
    public void RuntimeWarning_RuntimeInsideItsAppsPackage_IsNull()
    {
        var caller = new ApiCaller(@"C:\Program Files\WindowsApps\Raycast.Raycast_2.7.3.0_x64__qypenmj9wpt2a\Raycast\backend\node.exe", "Raycast", 1, ApiHello.None, AppProgram: @"C:\Program Files\WindowsApps\Raycast.Raycast_2.7.3.0_x64__qypenmj9wpt2a\Raycast\Raycast.exe");
        Assert.Null(ApiApprovalText.RuntimeWarning(caller));
    }

    [Fact]
    public void RuntimeWarning_OrdinaryApp_IsNull()
    {
        Assert.Null(ApiApprovalText.RuntimeWarning(Caller(@"C:\Apps\Raycast.exe", "Raycast")));
    }
}
