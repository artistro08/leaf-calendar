using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class PkceTests
{
    [Fact]
    public void CreateChallenge_Rfc7636Vector_MatchesSpec()
    {
        var challenge = Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void CreateVerifier_Called_Returns43UrlSafeUniqueCharacters()
    {
        var first  = Pkce.CreateVerifier();
        var second = Pkce.CreateVerifier();

        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "ab", false)]
    [InlineData("abc", null, false)]
    public void StateMatches_Values_ComparesExactly(string expected, string? actual, bool result)
    {
        Assert.Equal(result, Pkce.StateMatches(expected, actual));
    }
}
