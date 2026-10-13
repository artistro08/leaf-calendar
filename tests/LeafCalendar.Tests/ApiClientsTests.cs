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
    [InlineData(@"C:\Program Files\nodejs\node.exe", true)]
    [InlineData(@"C:\Python312\python.exe", true)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", true)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Raycast\Raycast.exe", false)]
    public void IsGeneralRuntime_KnowsScriptHosts(string path, bool expected)
    {
        Assert.Equal(expected, ApiClients.IsGeneralRuntime(path));
    }
}
