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

    [Theory]
    [InlineData("vitally.fiscaltec.com")]                 // no scheme
    [InlineData("/mcp")]                                  // relative — and `file:///mcp` on Linux, see below
    [InlineData("file:///etc/passwd")]                    // absolute, wrong scheme
    [InlineData("urn:example:vitally")]                   // absolute, but not an http resource
    [InlineData("https://vitally.fiscaltec.com/#frag")]    // fragment — RFC 8707 §2 forbids it
    public void Validate_MalformedResource_Throws(string resource)
    {
        // PublishedResourceIdentifier is no longer only published: it is what an incoming RFC 8707
        // `resource` is validated against, so a value that cannot be parsed rejects *every* request
        // carrying the parameter. That has to surface at boot, not at the first sign-in.
        //
        // The scheme is checked, not merely absoluteness, because `Uri.TryCreate("/mcp", Absolute)`
        // *succeeds* on Unix — as `file:///mcp` — and fails on Windows. CI on ubuntu caught exactly
        // that after this passed locally, and production runs Linux containers, so an absoluteness
        // check alone would have been inert where it matters most.
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            Resource = resource
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuth:Resource*");
    }

    [Fact]
    public void Validate_MalformedAudienceStandingInForTheResource_Throws()
    {
        // With Resource unset, Audience is what gets published and validated against — so the same
        // check has to reach it. An Entra-style client-ID GUID would land here: it is a fine
        // `aud` value but not a resource identifier, and it must not be published as one.
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "11111111-2222-3333-4444-555555555555"
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*OAuth:Resource*");
    }

    [Fact]
    public void Validate_NormalisesTrailingSlashes()
    {
        var options = ValidOptions(["https://claude.ai/api/mcp/auth_callback/"]);

        options.AllowedClientRedirectUris.Should().ContainSingle()
            .Which.Should().Be("https://claude.ai/api/mcp/auth_callback");
    }


    [Theory]
    [InlineData("https://vitally.fiscaltec.com/", "https://vitally.fiscaltec.com/")]  // exact
    [InlineData("https://vitally.fiscaltec.com/", "https://vitally.fiscaltec.com")]   // client dropped the slash
    [InlineData("https://vitally.fiscaltec.com", "https://vitally.fiscaltec.com/")]   // client added one
    [InlineData("https://vitally.fiscaltec.com/mcp", "https://vitally.fiscaltec.com/mcp")]
    [InlineData("https://vitally.fiscaltec.com/mcp", "https://vitally.fiscaltec.com/mcp/")]  // exactly one slash
    [InlineData("https://VITALLY.fiscaltec.com/", "https://vitally.fiscaltec.com/")]  // host is case-insensitive
    [InlineData("https://vitally.fiscaltec.com/", "HTTPS://vitally.fiscaltec.com/")]  // so is scheme
    public void IsResourceIndicatorAllowed_MatchesThePublishedIdentifier(string published, string requested)
    {
        // Trailing-slash tolerance is not cosmetic: Entra refuses to register an identifierUri
        // that ends in a slash, while Claude Code normalises a bare-host resource to the
        // trailing-slash form. Both forms name the same resource and both must be accepted.
        var options = ValidOptions([]);
        options.Resource = published;

        options.IsResourceIndicatorAllowed(requested).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://vitally.fiscaltec.com.evil.com/")]     // suffix spoof
    [InlineData("https://vitally.fiscaltec.com/mcp/other")]
    [InlineData("https://vitally.fiscaltec.com/mcp//")]        // two slashes is a different path, not the same name
    [InlineData("https://vitally.fiscaltec.com:8443/mcp")]      // different port
    [InlineData("http://vitally.fiscaltec.com/mcp")]            // different scheme
    [InlineData("https://vitally.fiscaltec.com/MCP")]           // path is case-sensitive (RFC 3986 6.2.2.1)
    [InlineData("not-a-uri")]
    [InlineData("")]                                            // present but empty binds nothing
    [InlineData("   ")]
    public void IsResourceIndicatorAllowed_RejectsAnythingElse(string requested)
    {
        // `resource` is an audience-binding control (RFC 8707). Accepting a value we do not
        // publish would hand the caller a token bound to an audience we never vouched for.
        var options = ValidOptions([]);
        options.Resource = "https://vitally.fiscaltec.com/mcp";

        options.IsResourceIndicatorAllowed(requested).Should().BeFalse();
    }

    [Fact]
    public void IsResourceIndicatorAllowed_RejectsAFragment()
    {
        // RFC 8707 section 2 forbids a fragment on a resource indicator outright.
        var options = ValidOptions([]);
        options.Resource = "https://vitally.fiscaltec.com/";

        options.IsResourceIndicatorAllowed("https://vitally.fiscaltec.com/#frag").Should().BeFalse();
    }

    [Fact]
    public void IsResourceIndicatorAllowed_FallsBackToAudienceWhenResourceIsUnset()
    {
        // The same fallback ProtectedResourceMetadataBuilder publishes, so the value validated
        // against is always the value clients were told to send.
        var options = ValidOptions([]);
        options.Resource = string.Empty;
        options.Audience = "https://api.example.com/";

        options.IsResourceIndicatorAllowed("https://api.example.com").Should().BeTrue();
        options.IsResourceIndicatorAllowed("https://elsewhere.example.com").Should().BeFalse();
    }

    [Fact]
    public void IsResourceIndicatorAllowed_AcceptsAnythingWhenNoIdentifierIsConfigured()
    {
        // Reachable only in a NoAuth dev configuration: Validate() requires Audience whenever
        // NoAuth is false, so production always has something to compare against. With nothing
        // published there is no audience binding to protect, and rejecting every value would
        // break the local proxy for no gain.
        var options = new OAuthOptions();

        options.IsResourceIndicatorAllowed("https://anything.example.com/").Should().BeTrue();
    }

    // ---- UpstreamResourceScope: the Auth0-relay / Entra-terminate switch (#105 part B, #108) ----

    [Fact]
    public void TerminatesResourceParameter_IsFalse_WhenNoUpstreamScopeIsConfigured()
    {
        // The Auth0 posture, and the default: `resource` is relayed, because the tenant's Resource
        // Parameter Compatibility Profile consuming it is the only thing binding the audience there.
        new OAuthOptions().TerminatesResourceParameter.Should().BeFalse();
    }

    [Fact]
    public void Validate_RejectsAnUpstreamResourceScopeContainingWhitespace()
    {
        // It names exactly one resource — the one whose `resource` parameter it stands in for — and
        // whitespace would smuggle extra scopes into every authorize request this server makes.
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            UpstreamResourceScope = "https://api.example.com/mcp.access openid"
        };

        options.Invoking(o => o.Validate()).Should().Throw<InvalidOperationException>()
            .WithMessage("*UpstreamResourceScope*");
    }

    [Fact]
    public void Validate_TrimsTheUpstreamResourceScope()
    {
        var options = new OAuthOptions
        {
            Authority = "https://example.auth0.com/",
            Audience = "https://api.example.com",
            UpstreamResourceScope = "  https://api.example.com/mcp.access  "
        };
        options.Validate();

        options.UpstreamResourceScope.Should().Be("https://api.example.com/mcp.access");
        options.TerminatesResourceParameter.Should().BeTrue();
    }

    [Theory]
    [InlineData("", "https://api.example.com/mcp.access")]
    [InlineData("openid profile", "openid profile https://api.example.com/mcp.access")]
    [InlineData("  openid   profile  ", "openid profile https://api.example.com/mcp.access")]
    [InlineData("openid https://api.example.com/mcp.access", "openid https://api.example.com/mcp.access")]
    public void MergeUpstreamScope_AppendsOnceAndPreservesTheClientsOwnScopes(string requested, string expected)
    {
        // Dropping a client scope here would have consequences well past this method: losing
        // offline_access costs the refresh token, and the failure would surface an hour later.
        var options = new OAuthOptions { UpstreamResourceScope = "https://api.example.com/mcp.access" };

        options.MergeUpstreamScope(requested).Should().Be(expected);
    }

    [Fact]
    public void MergeUpstreamScope_DoesNotDuplicateOnACasingDifference()
    {
        // RFC 6749 §3.3 makes scope tokens case-sensitive, so this is deliberate leniency: a
        // duplicate is a request the provider rejects, which is strictly worse than accepting the
        // client's spelling of a scope it evidently already has.
        var options = new OAuthOptions { UpstreamResourceScope = "https://api.example.com/mcp.access" };

        options.MergeUpstreamScope("openid HTTPS://API.EXAMPLE.COM/MCP.ACCESS")
            .Should().Be("openid HTTPS://API.EXAMPLE.COM/MCP.ACCESS");
    }

    // ---- ValidAudiences ----

    [Fact]
    public void ValidAudiences_CarriesBothTheAppIdUriAndTheClientId()
    {
        // An Entra v1 access token names the App ID URI; a v2 token names the resource
        // application's appId GUID — and one registration is both our client and our API, so that
        // GUID is SharedClientId. Accepting each settles which one arrives rather than betting.
        var options = new OAuthOptions
        {
            Audience = "https://vitally.fiscaltec.com",
            SharedClientId = "c3812e7d-a413-4169-b57e-803326611ba3"
        };

        options.ValidAudiences.Should().BeEquivalentTo(
            new[] { "https://vitally.fiscaltec.com", "c3812e7d-a413-4169-b57e-803326611ba3" });
    }

    [Fact]
    public void ValidAudiences_OmitsAnUnconfiguredSharedClientId()
    {
        // A null or empty entry in ValidAudiences is not inert — it would be compared against the
        // token's `aud` like any other candidate.
        new OAuthOptions { Audience = "https://api.example.com" }.ValidAudiences
            .Should().ContainSingle().Which.Should().Be("https://api.example.com");
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
