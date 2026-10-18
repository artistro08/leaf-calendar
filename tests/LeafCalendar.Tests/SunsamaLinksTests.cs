using LeafCalendar.Core.Sunsama;

namespace LeafCalendar.Tests;

public sealed class SunsamaLinksTests
{
    [Fact]
    public void TaskLink_SunsamaId_OpensTheApp() =>
        Assert.Equal(new Uri("sunsama://action/details/6ac7a44534029300011d076e"), SunsamaLinks.TaskLink("6ac7a44534029300011d076e"));

    [Theory]
    [InlineData("")]
    [InlineData("6ac7a44534029300011d076e/../x")]
    [InlineData("javascript:alert(1)")]
    public void TaskLink_BadId_ReturnsNull(string id) => Assert.Null(SunsamaLinks.TaskLink(id));

    [Fact]
    public void Fallback_Always_IsSunsamaHome() => Assert.Equal(new Uri("https://app.sunsama.com/"), SunsamaLinks.Fallback);
}
