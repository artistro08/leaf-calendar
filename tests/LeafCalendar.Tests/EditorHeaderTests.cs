using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public class EditorHeaderTests
{
    [Theory]
    [InlineData("Lunch", "Lunch")]
    [InlineData("  Lunch  ", "Lunch")]
    [InlineData("", "New event")]
    [InlineData("   ", "New event")]
    [InlineData("​‎", "New event")]
    [InlineData(" ‮⁦ ", "New event")]
    public void Text_NewEvent_FollowsAVisibleTitle(string title, string expected)
    {
        Assert.Equal(expected, EditorHeader.Text(isNew: true, title));
    }

    [Fact]
    public void Text_ExistingEvent_IsEditEvent()
    {
        Assert.Equal("Edit event", EditorHeader.Text(isNew: false, "Lunch"));
    }
}
