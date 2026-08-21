using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// Options carrying the permissions the synthetic test principal should hold.
/// </summary>
public class TestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public string[] Permissions { get; set; } = [];
}

/// <summary>
/// Authenticates every request as a fixed principal holding <see cref="TestAuthHandlerOptions.Permissions"/>.
/// Lets the integration tests exercise real policy evaluation and tools/list filtering without an
/// Auth0 tenant or a real token.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<TestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TestAuthHandlerOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-subject"),
            new("sub", "test-subject")
        };
        claims.AddRange(Options.Permissions.Select(p => new Claim("permissions", p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
