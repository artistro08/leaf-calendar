using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class ResponseBadgeTests
{
    [Theory]
    [InlineData(ResponseStatus.Accepted, "Going", ResponseTone.Positive)]
    [InlineData(ResponseStatus.Tentative, "Maybe", ResponseTone.Caution)]
    [InlineData(ResponseStatus.Declined, "Not going", ResponseTone.Critical)]
    [InlineData(ResponseStatus.NeedsAction, "Not answered", ResponseTone.Neutral)]
    public void For_EachReply_HasItsWordsAndColor(ResponseStatus response, string label, ResponseTone tone) =>
        Assert.Equal(new ResponseBadge(label, tone), ResponseBadge.For(response));

    [Fact]
    public void For_UnknownValue_IsNotAnswered() =>
        Assert.Equal(ResponseTone.Neutral, ResponseBadge.For((ResponseStatus)42).Tone);

    [Fact]
    public void Spoken_ReadsTheWholeLine() =>
        Assert.Equal("Your response: Maybe", ResponseBadge.For(ResponseStatus.Tentative).Spoken);
}
