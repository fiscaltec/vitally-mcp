using System.Net.Http;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ToolErrorResultTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public void IsSurfaceable_TrueForExpectedTypes(Type exType)
    {
        var ex = (Exception)Activator.CreateInstance(exType, "msg")!;
        ToolErrorResult.IsSurfaceable(ex).Should().BeTrue();
    }

    [Fact]
    public void IsSurfaceable_FalseForUnexpectedType()
    {
        ToolErrorResult.IsSurfaceable(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Fact]
    public void Build_ReturnsErrorResultWithExceptionMessage()
    {
        var result = ToolErrorResult.Build(new ArgumentException("bad input"));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().First().Text.Should().Be("bad input");
    }
}
