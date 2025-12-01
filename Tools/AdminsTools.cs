using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class AdminsTools
{
    [McpServerTool(Name = "List_admins", Title = "List admins", ReadOnly = true), Description("List Vitally admins with optional pagination and field selection")]
    public static async Task<string> ListAdmins(
        VitallyService vitallyService,
        [Description("Maximum number of admins to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,email'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("admins", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "Get_admin", Title = "Get admin", ReadOnly = true), Description("Get a single Vitally admin by ID")]
    public static async Task<string> GetAdmin(
        VitallyService vitallyService,
        [Description("The admin ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("admins", id, fields);
    }
}
