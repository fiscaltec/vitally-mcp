using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool(Name = "List_account", Title = "List account", ReadOnly = true, Destructive = false), Description("List Vitally accounts with optional pagination, filtering and field selection")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        [Description("Maximum number of accounts to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Filter by account status: 'active' (default), 'churned', or 'activeOrChurned'")] string? status = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status))
        {
            additionalParams["status"] = status;
        }

        return await vitallyService.GetResourcesAsync("accounts", limit, from, fields, sortBy, additionalParams, traits);
    }

    [McpServerTool(Name = "List_accounts_by_organization", Title = "List accounts by organization", ReadOnly = true, Destructive = false), Description("List Vitally accounts for a specific organisation")]
    public static async Task<string> ListAccountsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of accounts to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/accounts", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_account", Title = "Get account", ReadOnly = true, Destructive = false), Description("Get a single Vitally account by ID")]
    public static async Task<string> GetAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("accounts", id, fields, traits);
    }

    [McpServerTool(Name = "Get_account_health_scores", Title = "Get account health scores", ReadOnly = true, Destructive = false), Description("Get health score breakdown for a Vitally account")]
    public static async Task<string> GetAccountHealthScores(
        VitallyService vitallyService,
        [Description("The account ID")] string id,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{id}/healthScores", 1, null, fields, null, null, null);
    }

    [McpServerTool(Name = "Create_account", Title = "Create account", ReadOnly = false, Destructive = false), Description("Create a new Vitally account")]
    public static async Task<string> CreateAccount(
        VitallyService vitallyService,
        [Description("JSON body containing account data. Required fields: externalId (string), name (string). Optional: organizationId, traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("accounts", jsonBody);
    }

    [McpServerTool(Name = "Update_account", Title = "Update account", ReadOnly = false, Destructive = true), Description("Update an existing Vitally account")]
    public static async Task<string> UpdateAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string id,
        [Description("JSON body containing fields to update. Optional fields: name, organizationId, traits (object). Traits are merged with existing data.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("accounts", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_account", Title = "Delete account", ReadOnly = false, Destructive = true), Description("Delete a Vitally account")]
    public static async Task<string> DeleteAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("accounts", id);
    }
}
