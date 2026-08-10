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

    /// <summary>
    /// Same sweep as <see cref="AllTools"/>, but pairs each tool with the raw <see cref="CustomAttributeData"/>
    /// for its <see cref="McpServerToolAttribute"/> usage. <c>ReadOnly</c>/<c>Destructive</c>/<c>Idempotent</c>/
    /// <c>OpenWorld</c> are plain <c>bool</c> (not <c>bool?</c>), so an unset hint is indistinguishable from an
    /// explicit <c>false</c> once the attribute is instantiated — <c>NamedArguments</c> is the only way to see
    /// which properties the source actually assigned.
    /// </summary>
    private static IEnumerable<(string Name, McpServerToolAttribute Attr, CustomAttributeData Data)> AllToolsWithAttributeData()
    {
        var assembly = typeof(VitallyService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    var data = method.GetCustomAttributesData().First(d => d.AttributeType == typeof(McpServerToolAttribute));
                    yield return (attr.Name ?? method.Name, attr, data);
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

    /// <summary>
    /// Makes the invariant prefix-independent: unlike the tests above, this runs over every tool
    /// regardless of name, so a future tool named outside List_/Get_/Create_/Update_/Delete_ (e.g. a
    /// hypothetical Sync_*) cannot slip through with unset or contradictory hints. Two checks:
    /// (a) all four hints are explicitly assigned in the attribute usage — not merely defaulted to
    /// false by the CLR because the property was never mentioned — and (b) the repo's ReadOnly/Destructive
    /// convention documented in CLAUDE.md holds: a tool is either read-only or destructive, never both
    /// and never neither.
    /// </summary>
    [Fact]
    public void EveryTool_HasAllFourHintsExplicitlySetAndConsistentReadOnlyDestructive()
    {
        foreach (var (name, attr, data) in AllToolsWithAttributeData())
        {
            var setProperties = data.NamedArguments!.Select(a => a.MemberName).ToHashSet(StringComparer.Ordinal);

            setProperties.Should().Contain("ReadOnly", $"{name} must explicitly set ReadOnly");
            setProperties.Should().Contain("Destructive", $"{name} must explicitly set Destructive");
            setProperties.Should().Contain("Idempotent", $"{name} must explicitly set Idempotent");
            setProperties.Should().Contain("OpenWorld", $"{name} must explicitly set OpenWorld");

            if (attr.ReadOnly)
            {
                attr.Destructive.Should().BeFalse($"{name} is ReadOnly, so per the repo convention it must not also be Destructive");
            }
            else
            {
                attr.Destructive.Should().BeTrue($"{name} mutates (ReadOnly=false), so per the repo convention it must be Destructive");
            }
        }
    }
}
