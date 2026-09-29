using LeafCalendar.Core.Auth;

namespace LeafCalendar.Tests;

public class OAuthClientCredentialsTests
{
    [Theory]
    [InlineData("123-abc.apps.googleusercontent.com", "GOCSPX-secret", null)]
    [InlineData("  123-abc.apps.googleusercontent.com  ", "GOCSPX-secret", null)]
    [InlineData("not-a-client-id", "GOCSPX-secret", "Client ID should end with .apps.googleusercontent.com.")]
    [InlineData("123-abc.apps.googleusercontent.com", "   ", "Enter the client secret.")]
    public void Validate_Input_ReturnsExpectedError(string clientId, string secret, string? error)
    {
        Assert.Equal(error, OAuthClientCredentials.Validate(clientId, secret));
    }

    [Fact]
    public void ToString_Called_OmitsSecret()
    {
        var text = new OAuthClientCredentials("id.apps.googleusercontent.com", "GOCSPX-secret").ToString();

        Assert.DoesNotContain("GOCSPX", text, StringComparison.Ordinal);
    }
}
