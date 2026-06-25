using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class VitallyServerInstructionsTests
{
    [Theory]
    [InlineData("organisation")]
    [InlineData("Traits")]
    [InlineData("custom object")]
    [InlineData("List_organizations(nameContains")]
    [InlineData("createdAfter")]
    [InlineData("Read-only")]
    public void Text_ContainsKeyGuidanceMarkers(string marker)
    {
        VitallyServerInstructions.Text.Should().Contain(marker);
    }

    [Fact]
    public void Text_IsNotEmpty()
    {
        VitallyServerInstructions.Text.Should().NotBeNullOrWhiteSpace();
    }
}
