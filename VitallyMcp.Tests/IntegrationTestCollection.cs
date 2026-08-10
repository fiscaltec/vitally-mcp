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
/// Member classes: <see cref="ReadOnlyToolsListTests"/>, <see cref="ToolsListCachingTests"/>, and
/// <see cref="ServerInstructionsInitializeTests"/>. The last of these is the sharpest illustration
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
