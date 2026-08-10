using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tests;

/// <summary>
/// Reflection sweep asserting that <b>every</b> tool carries exactly one <c>[Authorize]</c> policy,
/// and that the policy agrees with the tool's own <c>ReadOnly</c> annotation.
///
/// <para>
/// This is prefix-independent on purpose. Without it a future tool could ship with no
/// <c>[Authorize]</c> at all and be silently advertised to every caller in <c>tools/list</c>,
/// including readers who cannot invoke it — a security-relevant regression that no other test in the
/// suite would catch. It is discovery hygiene rather than the security boundary itself (that remains
/// <c>VitallyService.SendAsync</c>), but a wrong or missing policy here is exactly what makes the
/// advertised list and the enforced tier disagree.
/// </para>
/// </summary>
public class ToolAuthorizePolicyCoverageTests
{
    private const string ReadPolicy = "vitally:read";
    private const string WritePolicy = "vitally:write";
    private const string DeletePolicy = "vitally:delete";

    private static IEnumerable<(string Name, McpServerToolAttribute Attr, MethodInfo Method)> AllTools()
    {
        var assembly = typeof(VitallyService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (attr.Name ?? method.Name, attr, method);
                }
            }
        }
    }

    [Fact]
    public void EveryTool_CarriesExactlyOneAuthorizePolicyConsistentWithItsAnnotations()
    {
        foreach (var (name, attr, method) in AllTools())
        {
            // CustomAttributeData rather than the attribute instance: it is the only way to see the
            // policy exactly as the source assigned it, and to count the usages on this member
            // without inheritance folding two declarations into one.
            var authorizeUsages = method.GetCustomAttributesData()
                .Where(d => d.AttributeType == typeof(AuthorizeAttribute))
                .ToList();

            authorizeUsages.Should().HaveCount(1,
                $"{name} must carry exactly one [Authorize] attribute so tools/list filtering has an unambiguous policy");

            var policy = authorizeUsages[0].NamedArguments
                .Where(a => a.MemberName == nameof(AuthorizeAttribute.Policy))
                .Select(a => a.TypedValue.Value as string)
                .FirstOrDefault()
                // A positional [Authorize("policy")] also sets Policy, via the constructor.
                ?? authorizeUsages[0].ConstructorArguments.FirstOrDefault().Value as string;

            policy.Should().BeOneOf([ReadPolicy, WritePolicy, DeletePolicy],
                $"{name} must use one of the three registered vitally:* policies");

            if (attr.ReadOnly)
            {
                policy.Should().Be(ReadPolicy,
                    $"{name} is ReadOnly=true, so a reader-tier caller must be allowed to discover it");
            }
            else
            {
                policy.Should().BeOneOf([WritePolicy, DeletePolicy],
                    $"{name} mutates (ReadOnly=false), so it must not be discoverable with only {ReadPolicy}");
            }
        }
    }

    /// <summary>
    /// Guards the split itself: the counts are asserted so that a tool silently changing tier (e.g. a
    /// delete re-labelled as a write) shows up as a failure rather than passing the consistency check
    /// above. Totals must sum to the full tool count.
    /// </summary>
    [Fact]
    public void PolicyDistribution_MatchesTheHttpVerbTiers()
    {
        var byPolicy = AllTools()
            .Select(t => t.Method.GetCustomAttributesData()
                .First(d => d.AttributeType == typeof(AuthorizeAttribute))
                .NamedArguments.First(a => a.MemberName == nameof(AuthorizeAttribute.Policy))
                .TypedValue.Value as string)
            .GroupBy(p => p!)
            .ToDictionary(g => g.Key, g => g.Count());

        byPolicy.Should().ContainKey(ReadPolicy).And.ContainKey(WritePolicy).And.ContainKey(DeletePolicy);
        byPolicy.Values.Sum().Should().Be(93, "the server exposes 93 tools and each must be classified");
        byPolicy[ReadPolicy].Should().Be(56, "List_*/Get_*/Search_* all issue GETs");
        byPolicy[WritePolicy].Should().Be(25, "Create_*/Update_*/Add_meeting_participant all issue POST/PUT");
        byPolicy[DeletePolicy].Should().Be(12, "Delete_*/Remove_meeting_participant issue DELETEs");
    }
}
