using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool, Description("List Vitally accounts with optional pagination and field selection")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        [Description("Maximum number of accounts to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response")] string? cursor = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt')")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("accounts", limit, cursor, fields);
    }

    [McpServerTool, Description("Get a single Vitally account by ID")]
    public static async Task<string> GetAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string id,
        [Description("Comma-separated list of fields to include")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("accounts", id, fields);
    }
}
