namespace VitallyMcp.Tests;

/// <summary>
/// Serialises every integration test class that overrides configuration through environment
/// variables. <c>Program.cs</c> reads <c>OAuth:NoAuth</c> and <c>Authorization:ReadOnly</c> at
/// composition time — before <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>
/// can inject configuration — so environment variables are the only override that works. They are
/// process-wide, and xUnit runs test classes in parallel by default, so without this collection two
/// fixtures setting <c>OAuth__NoAuth</c> to different values race and fail depending on scheduling.
///
/// <para>
/// Member classes: <see cref="ReadOnlyToolsListTests"/>, <see cref="ToolsListCachingTests"/>,
/// <see cref="AuthorizationFilterToolsListTests"/>, <see cref="ResourceMetadataDiscoveryTests"/>,
/// <see cref="ServerInstructionsInitializeTests"/>, <see cref="StaleEntitlementCompositionTests"/>,
/// <see cref="LoggingFilterTests"/>, <see cref="ToolCallAuditCompositionTests"/>, and the classes
/// that compose a host WITHOUT setting environment variables themselves:
/// <see cref="OAuthProxyEndpointsTests"/>, <see cref="OAuthProxyPublicOriginTests"/>,
/// <see cref="OAuthProxyResourceTerminationTests"/>, <see cref="OAuthTokenProxyForwardTests"/> and
/// <see cref="UpstreamOidcStartupFailFastTests"/>.
/// <para>
/// ⚠️ <b>A class that only READS configuration needs to be here too</b>, which is the non-obvious
/// half. Those five inject in-memory configuration and never touch the environment — but they omit
/// keys a sibling sets as an environment variable, so a sibling running concurrently leaks its value
/// in. Serialising prevents that; it does <b>not</b> prevent a fixture that never clears its
/// variables from leaking them into whichever class runs next, which is why every fixture here must
/// reset what it set on dispose. <see cref="ResourceMetadataDiscoveryTests"/> did not, and nine proxy
/// tests failed on its <c>https://example.test</c> in Release builds once #94's added tests shifted
/// the order to put them after it — latent on <c>main</c>, found and fixed in #94.
/// </para>
/// Keep this list complete — it is what a future
/// author reads when deciding whether a new environment-variable-mutating class needs to join, and
/// an incomplete list makes the collection look narrower in purpose than it is.
/// <see cref="ServerInstructionsInitializeTests"/> is the sharpest illustration
/// of why serialisation is required, not just desirable: its <c>Factory.Dispose</c> resets
/// <c>OAuth__NoAuth</c>, <c>Vitally__Region</c> and <c>Vitally__DevelopmentApiKey</c> to
/// <c>null</c>, so running it unserialised could null out a sibling fixture's configuration while
/// that sibling's host is still composing.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection
{
    public const string Name = "Integration (serialised: mutates environment variables)";
}
