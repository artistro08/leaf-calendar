using LeafCalendar.Core.Api;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ApiPeopleTests : IDisposable
{
    private readonly ApiHarness _api = new();

    public ApiPeopleTests()
    {
        // Sam twice, Kim once, you on both
        foreach (var (id, day) in new[] { ("a", "01"), ("b", "02") })
        {
            _api.Store(ApiHarness.Primary, $$"""{"id":"evt-{{id}}","status":"confirmed","summary":"Sync","start":{"dateTime":"2026-10-{{day}}T10:00:00-04:00"},"end":{"dateTime":"2026-10-{{day}}T11:00:00-04:00"},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"},{"email":"sam@example.com","displayName":"Sam Rivera","responseStatus":"accepted"}]}""");
        }

        _api.Store(ApiHarness.Primary, """{"id":"evt-c","status":"confirmed","summary":"Lunch","start":{"dateTime":"2026-10-03T12:00:00-04:00"},"end":{"dateTime":"2026-10-03T13:00:00-04:00"},"attendees":[{"email":"leaf.tester@gmail.com","self":true,"responseStatus":"accepted"},{"email":"kim@other.org","responseStatus":"accepted"}]}""");
    }

    public void Dispose() => _api.Dispose();

    private async Task<List<(string? Name, string Email)>> SearchAsync(string paramsJson) =>
        [.. (await _api.CallAsync("people.search", paramsJson)).EnumerateArray().Select(p => (p.GetProperty("name").GetString(), p.GetProperty("email").GetString()!))];

    [Fact]
    public async Task Search_NoQuery_IsThePeopleYouMeetMostOften()
    {
        Assert.Equal([("Sam Rivera", "sam@example.com"), (null, "kim@other.org")], await SearchAsync("{}"));
    }

    [Theory]
    [InlineData("riv")]
    [InlineData("SAM")]
    [InlineData("example")]
    public async Task Search_MatchesANameWordOrTheAddress(string query)
    {
        Assert.Equal([("Sam Rivera", "sam@example.com")], await SearchAsync($$"""{"query":"{{query}}"}"""));
    }

    [Fact]
    public async Task Search_NothingMatches_IsEmpty()
    {
        Assert.Empty(await SearchAsync("""{"query":"zzz"}"""));
    }

    [Fact]
    public async Task Search_TooLong_IsInvalid()
    {
        Assert.Equal(ApiErrors.InvalidParams, (await _api.FailAsync("people.search", $$"""{"query":"{{new string('a', 101)}}"}""")).Code);
    }
}
