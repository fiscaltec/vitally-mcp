using FluentAssertions;
using ModelContextProtocol.Protocol;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ReadOnlyToolFilterTests
{
    private static Tool ReadTool() => new() { Name = "Get_thing", Annotations = new ToolAnnotations { ReadOnlyHint = true } };
    private static Tool DeleteTool() => new() { Name = "Delete_thing", Annotations = new ToolAnnotations { DestructiveHint = true } };
    private static Tool CreateTool() => new() { Name = "Create_thing" }; // no annotations

    [Fact]
    public void ReadOnly_KeepsOnlyReadHintedTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Get_thing");
    }

    [Fact]
    public void NotReadOnly_KeepsAllTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: false);

        result.Should().HaveCount(3);
    }

    [Fact]
    public void ReadOnly_DropsToolsWithNullAnnotations()
    {
        var tools = new[] { CreateTool() }; // null annotations -> not read-only -> dropped

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().BeEmpty();
    }
}
