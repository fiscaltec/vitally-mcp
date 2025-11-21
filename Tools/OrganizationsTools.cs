using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class OrganizationsTools
{
    [McpServerTool, Description("List Vitally organisations with optional pagination and field selection")]
    public static async Task<string> ListOrganizations(
        VitallyService vitallyService,
        [Description("Maximum number of organisations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("organizations", limit, from, fields, sortBy);
    }

    [McpServerTool, Description("Get a single Vitally organisation by ID")]
    public static async Task<string> GetOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("organizations", id, fields);
    }
}
