using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class UsersTools
{
    [McpServerTool(Name = "List_users", Title = "List users", ReadOnly = true), Description("List Vitally users with optional pagination and field selection")]
    public static async Task<string> ListUsers(
        VitallyService vitallyService,
        [Description("Maximum number of users to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,email'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync("users", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_user", Title = "Get user", ReadOnly = true), Description("Get a single Vitally user by ID")]
    public static async Task<string> GetUser(
        VitallyService vitallyService,
        [Description("The user ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("users", id, fields, traits);
    }
}
