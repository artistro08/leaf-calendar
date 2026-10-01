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

    [Fact]
    public void RemindersPatch_KeepsGooglesEmailReminders()
    {
        const string stored = """[{"method":"email","minutes":60},{"method":"popup","minutes":10},{"method":"sms","minutes":-3}]""";

        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":30},{"method":"email","minutes":60}]}""", CalendarEdits.RemindersPatch([30], stored));
    }

    [Fact]
    public void RemindersPatch_PopupsAndEmailsTogetherNeverExceedFive()
    {
        const string stored = """[{"method":"email","minutes":60},{"method":"email","minutes":120}]""";

        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":1},{"method":"popup","minutes":2},{"method":"popup","minutes":3},{"method":"email","minutes":60},{"method":"email","minutes":120}]}""",
            CalendarEdits.RemindersPatch([1, 2, 3, 4, 5, 6], stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void RemindersPatch_UnreadableStored_KeepsNothingExtra(string? stored) =>
        Assert.Equal("""{"defaultReminders":[{"method":"popup","minutes":5}]}""", CalendarEdits.RemindersPatch([5], stored));
}
