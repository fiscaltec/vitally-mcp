using FluentAssertions;

namespace VitallyMcp.Tests;

/// <summary>
/// Validation and defaults for <see cref="ToolAuthorizationOptions"/>, mirroring
/// <see cref="OAuthOptionsTests"/> and <see cref="VitallyServerOptionsTests"/>.
/// </summary>
public class ToolAuthorizationOptionsTests
{
    // Minimum shape that passes Validate() with the live check on, so each test varies one thing.
    private static ToolAuthorizationOptions WithLiveCheck() => new()
    {
        Enabled = true,
        LiveGroupCheck = true,
        ReaderGroupId = "71451cc9-f5df-44ee-8ed1-3acc41a911eb",
    };

    [Fact]
    public void LiveGroupStaleSeconds_DefaultsToOneHour()
    {
        new ToolAuthorizationOptions().LiveGroupStaleSeconds.Should().Be(3600);
    }

    [Fact]
    public void Validate_Rejects_NegativeLiveGroupStaleSeconds()
    {
        var options = WithLiveCheck();
        options.LiveGroupStaleSeconds = -1;

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*LiveGroupStaleSeconds*");
    }

    [Fact]
    public void Validate_Accepts_ZeroLiveGroupStaleSeconds_BecauseZeroDisablesStaleServing()
    {
        var options = WithLiveCheck();
        options.LiveGroupStaleSeconds = 0;

        var act = options.Validate;

        act.Should().NotThrow();
    }
}
