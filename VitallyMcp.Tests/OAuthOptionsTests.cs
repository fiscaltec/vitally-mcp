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
    [InlineData("http://127.0.0.2")]  // anywhere in 127.0.0.0/8
    [InlineData("http://[::1]")]
    [InlineData("http://[::1]:1234/x")]
    public void IsRedirectUriAllowed_LoopbackAnyPort_AllowedWithoutAllowlist(string redirectUri)
    {
        var options = new OAuthOptions();

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue(
            "RFC 8252 §7.3 requires native clients on loopback to use any ephemeral port");
    }

    [Theory]
    [InlineData(" http://localhost:8080")]                // leading space
    [InlineData("http://localhost:8080 ")]                // trailing space
    [InlineData(" https://claude.ai/api/mcp/auth_callback")]
    [InlineData("\thttp://localhost:8080")]
    public void IsRedirectUriAllowed_WhitespacePadded_Rejected(string redirectUri)
    {
        // Uri.TryCreate tolerates leading whitespace and parses the URI, but the allowlist
        // string comparison would still see the un-normalised raw value. Reject explicitly
        // rather than silently normalise — a well-behaved client never sends padding.
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
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
    [InlineData("https://claude.ai/api/mcp/auth_callback/extra/path?q=1")]
    public void IsRedirectUriAllowed_AllowedHosted_Accepted(string redirectUri)
    {
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback#fragment")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?state=x#fragment")]
    [InlineData("http://localhost:8080/cb#fragment")]
    public void IsRedirectUriAllowed_FragmentInUri_Rejected(string redirectUri)
    {
        // OAuth 2.0 §3.1.2 forbids fragment components in redirect_uri. They would also
        // silently break the /oauth/callback append (code+state would land in the fragment
        // rather than the query string).
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback.evil.com")]
    [InlineData("https://claude.ai.evil.com/api/mcp/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback_extra")]
    public void IsRedirectUriAllowed_PrefixCannotBeSpoofed(string redirectUri)
    {
        // Make sure the prefix match doesn't allow attacker-controlled subdomain or appended
        // path segment to look like a legitimate callback. Only "/"-delimited extensions count
        // as matching the same callback (the path comparison enforces a path-segment boundary).
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://CLAUDE.AI/api/mcp/auth_callback")]
    [InlineData("https://Claude.Ai/api/mcp/auth_callback")]
    [InlineData("HTTPS://claude.ai/api/mcp/auth_callback")]
    public void IsRedirectUriAllowed_SchemeAndHostAreCaseInsensitive(string redirectUri)
    {
        // RFC 3986 §6.2.2: scheme and host are case-insensitive equivalence components.
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://claude.ai/API/MCP/AUTH_CALLBACK")]
    [InlineData("https://claude.ai/api/MCP/auth_callback")]
    [InlineData("https://claude.ai/api/mcp/Auth_Callback")]
    public void IsRedirectUriAllowed_PathIsCaseSensitive(string redirectUri)
    {
        // RFC 3986 §6.2.2.3: path/query are assumed case-sensitive. Treating them as
        // case-insensitive would let a client route through a different endpoint than the
        // one the server administrator allowlisted.
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback"]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com/", "https://example.com")]
    public void IsRedirectUriAllowed_RootPathEntry_MatchesOnlyRoot(string allowed, string redirectUri)
    {
        // Regression guard: a root-path allowlist entry must not become a wildcard. After
        // TrimEnd('/'), the path on both sides reduces to "", and the path-segment prefix
        // check would match every path on the host without the explicit root special case.
        var options = ValidOptions([allowed]);

        options.IsRedirectUriAllowed(redirectUri).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://example.com/anything")]
    [InlineData("https://example.com/api/mcp/auth_callback")]
    [InlineData("https://example.com/admin")]
    public void IsRedirectUriAllowed_RootPathEntry_DoesNotWildcardOtherPaths(string redirectUri)
    {
        // The exploit Copilot flagged: with allowed="https://example.com", the path-segment
        // prefix check used to accept any subpath as a match. It must not.
        var options = ValidOptions(["https://example.com"]);

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

    [Theory]
    [InlineData("http://example.com/cb")]
    [InlineData("http://internal.local:8080/cb")]
    [InlineData("http://localhost:8080/cb")]
    public void Validate_HttpAllowlistEntry_Throws(string entry)
    {
        // Per OAuth 2.0 Security BCP, non-loopback redirect URIs must use https. We don't
        // accept http even for loopback in the allowlist — loopback is already covered by
        // IsRedirectUriAllowed's RFC 8252 exemption and never needs to be listed.
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            AllowedClientRedirectUris = [entry]
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*https*");
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
