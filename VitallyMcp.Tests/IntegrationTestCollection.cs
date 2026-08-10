namespace VitallyMcp.Tests;

/// <summary>
/// Serialises every integration test class that overrides configuration through environment
/// variables. <c>Program.cs</c> reads <c>OAuth:NoAuth</c> and <c>Authorization:ReadOnly</c> at
/// composition time — before <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>
/// can inject configuration — so environment variables are the only override that works. They are
/// process-wide, and xUnit runs test classes in parallel by default, so without this collection two
/// fixtures setting <c>OAuth__NoAuth</c> to different values race and fail depending on scheduling.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection
{
    public const string Name = "Integration (serialised: mutates environment variables)";
}
