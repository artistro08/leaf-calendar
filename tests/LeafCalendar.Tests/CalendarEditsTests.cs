using LeafCalendar.Core.Editing;

namespace LeafCalendar.Tests;

public sealed class CalendarEditsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("‮\u0000")]
    public void CleanName_BlankOrHidden_UsesGooglesName(string? text) => Assert.Null(CalendarEdits.CleanName(text));

    [Fact]
    public void CleanName_StripsHiddenCharacters_AndCaps()
    {
        Assert.Equal("Kids", CalendarEdits.CleanName("  Ki‮ds\u0007 "));
        Assert.Equal(CalendarEdits.MaxName, CalendarEdits.CleanName(new string('k', 300))!.Length);
    }

    [Fact]
    public void RenamePatch_NullIsJsonNull()
    {
        Assert.Equal("""{"summaryOverride":null}""", CalendarEdits.RenamePatch(null));
        Assert.Equal("""{"summaryOverride":"Kids"}""", CalendarEdits.RenamePatch("Kids"));
    }

    [Fact]
    public void RenamePatch_QuotesAndMarkup_StayInsideTheString() =>
        Assert.Equal("Bo\"b <b>", System.Text.Json.Nodes.JsonNode.Parse(CalendarEdits.RenamePatch("Bo\"b <b>"))!["summaryOverride"]!.GetValue<string>());

    [Fact]
    public void RemindersPatch_PopupsSortedDistinctInRangeCapped() =>
        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":1},{"method":"popup","minutes":2},{"method":"popup","minutes":3},{"method":"popup","minutes":4},{"method":"popup","minutes":10}]}""",
            CalendarEdits.RemindersPatch([30, 10, 10, -5, 50000, 1, 2, 3, 4]));

    [Fact]
    public void RemindersPatch_None_IsAnEmptyList() =>
        Assert.Equal("""{"defaultReminders":[]}""", CalendarEdits.RemindersPatch([]));
}
