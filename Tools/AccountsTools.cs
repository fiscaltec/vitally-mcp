using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool, Description("List Vitally accounts with optional pagination, filtering and field selection")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        [Description("Maximum number of accounts to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Filter by account status: 'active' (default), 'churned', or 'activeOrChurned'")] string? status = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status))
        {
            additionalParams["status"] = status;
        }

        return await vitallyService.GetResourcesAsync("accounts", limit, from, fields, sortBy, additionalParams);
    }

    [McpServerTool, Description("Get a single Vitally account by ID")]
    public static async Task<string> GetAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("accounts", id, fields);
    }
}
