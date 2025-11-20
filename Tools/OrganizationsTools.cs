using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class OrganizationsTools
{
    [McpServerTool, Description("List Vitally organizations with optional pagination and field selection")]
    public static async Task<string> ListOrganizations(
        VitallyService vitallyService,
        [Description("Maximum number of organizations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response")] string? cursor = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt')")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("organizations", limit, cursor, fields);
    }

    [McpServerTool, Description("Get a single Vitally organization by ID")]
    public static async Task<string> GetOrganization(
        VitallyService vitallyService,
        [Description("The organization ID")] string id,
        [Description("Comma-separated list of fields to include")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("organizations", id, fields);
    }
}
