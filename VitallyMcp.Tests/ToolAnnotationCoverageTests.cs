using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tests;

/// <summary>
/// Reflection sweep over every [McpServerTool] method, asserting the four annotation hints are set
/// consistently with the tool's name prefix. This is enforcement rather than documentation: a new
/// tool added without annotations fails here instead of shipping with misleading retry semantics.
/// </summary>
public class ToolAnnotationCoverageTests
{
    private static IEnumerable<(string Name, McpServerToolAttribute Attr)> AllTools()
    {
        var assembly = typeof(VitallyService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (attr.Name ?? method.Name, attr);
                }
            }
        }
    }

    [Fact]
    public void EveryToolIsDiscovered()
    {
        AllTools().Should().HaveCountGreaterThan(90, "the server exposes ~95 tools; a big drop means discovery broke");
    }

    [Theory]
    [InlineData("List_")]
    [InlineData("Get_")]
    public void ReadTools_AreReadOnlyIdempotentAndClosedWorld(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty($"there must be tools named {prefix}*");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(true, $"{name} only reads");
            attr.Destructive.Should().Be(false, $"{name} only reads");
            attr.Idempotent.Should().Be(true, $"{name} is safe to repeat");
            attr.OpenWorld.Should().Be(false, $"{name} addresses one closed Vitally tenant");
        }
    }

    [Fact]
    public void CreateTools_AreDestructiveAndNotIdempotent()
    {
        var tools = AllTools().Where(t => t.Name.StartsWith("Create_", StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty();

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(false, $"repeating {name} would create a second record");
            attr.OpenWorld.Should().Be(false);
        }
    }

    [Theory]
    [InlineData("Update_")]
    [InlineData("Delete_")]
    public void UpdateAndDeleteTools_AreDestructiveAndIdempotent(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty($"there must be tools named {prefix}*");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(true, $"repeating {name} lands the same final state");
            attr.OpenWorld.Should().Be(false);
        }
    }
}
