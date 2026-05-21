using FluentAssertions;

namespace VitallyMcp.Tests;

public class OAuthOptionsTests
{
    [Theory]
    [InlineData("http://localhost")]
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:8080")]
    [InlineData("http://localhost:8080/callback")]
    [InlineData("http://localhost:54321/oauth/callback")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://127.0.0.1:9000/cb")]
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:1234/x")]
    public void IsRedirectUriAllowed_LoopbackAnyPort_AllowedWithoutAllowlist(string redirectUri)
    {
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue(
            "RFC 8252 §7.3 requires native clients on loopback to use any ephemeral port");
    }

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://localhost:8080")]
    public void IsRedirectUriAllowed_HttpsLoopback_NotImplicitlyAllowed(string redirectUri)
    {
        // RFC 8252 only requires the http-on-loopback exemption; https-loopback is a sign of
        // something unusual (a client that can provision TLS for localhost) and should be
        // listed explicitly if needed.
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://evil.example.com/steal")]
    [InlineData("https://attacker.local/cb")]
    [InlineData("http://example.com")]
    public void IsRedirectUriAllowed_NonLoopbackWithoutAllowlist_Rejected(string redirectUri)
    {
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?session=abc")]
    [InlineData("https://claude.ai/api/mcp/auth_callback#fragment")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/extra/path?q=1#frag")]
    public void IsRedirectUriAllowed_AllowedHosted_Accepted(string redirectUri)
    {
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback.evil.com")]
    [InlineData("https://claude.ai.evil.com/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback_extra")]
    public void IsRedirectUriAllowed_PrefixCannotBeSpoofed(string redirectUri)
    {
        // Make sure the prefix match doesn't allow attacker-controlled subdomain or appended
        // path segment to look like a legitimate callback. Only "/"-delimited or "?"-delimited
        // extensions count as matching the same callback.
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Fact]
    public void IsRedirectUriAllowed_EmptyOrWhitespace_Rejected()
    {
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed("").Should().BeFalse();
        options.IsRedirectUriAllowed("   ").Should().BeFalse();
        options.IsRedirectUriAllowed(null!).Should().BeFalse();
    }

    [Fact]
    public void IsRedirectUriAllowed_RelativeUri_Rejected()
    {
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed("/oauth/callback").Should().BeFalse();
        options.IsRedirectUriAllowed("oauth/callback").Should().BeFalse();
    }

    [Fact]
    public void Validate_InvalidAllowlistEntry_Throws()
    {
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            AllowedClientRedirectUris = ["not-a-uri"]
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AllowedClientRedirectUris*");
    }

    [Fact]
    public void Validate_NormalisesTrailingSlashes()
    {
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback/"]);

        options.AllowedClientRedirectUris.Should().ContainSingle()
            .Which.Should().Be("https://claude.ai/api/mcp/auth_callback");
    }

    private static OAuthOptions ValidOptions(string[] allowedRedirectUris)
    {
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            AllowedClientRedirectUris = allowedRedirectUris
        };
        options.Validate();
        return options;
    }
}
