using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Filters a tools/list result for read-only deployments: keeps only tools whose annotation marks
/// them read-only (<c>ReadOnlyHint == true</c>) — i.e. drops every create/update/delete tool — so a
/// read-only server never advertises destructive operations. A pass-through when not read-only.
/// </summary>
public static class ReadOnlyToolFilter
{
    public static IList<Tool> FilterTools(IEnumerable<Tool> tools, bool readOnly)
    {
        if (!readOnly)
        {
            return tools.ToList();
        }
        return tools.Where(t => t.Annotations?.ReadOnlyHint == true).ToList();
    }
}
