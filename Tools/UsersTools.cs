using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class UsersTools
{
    [McpServerTool, Description("List Vitally users with optional pagination and field selection")]
    public static async Task<string> ListUsers(
        VitallyService vitallyService,
        [Description("Maximum number of users to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response")] string? cursor = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,email')")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("users", limit, cursor, fields);
    }

    [McpServerTool, Description("Get a single Vitally user by ID")]
    public static async Task<string> GetUser(
        VitallyService vitallyService,
        [Description("The user ID")] string id,
        [Description("Comma-separated list of fields to include")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("users", id, fields);
    }
}
