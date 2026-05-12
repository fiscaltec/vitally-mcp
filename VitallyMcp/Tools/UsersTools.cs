using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class UsersTools
{
    [McpServerTool(Name = "List_users", Title = "List users", ReadOnly = true, Destructive = false), Description("List Vitally users with optional pagination and field selection")]
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

    [McpServerTool(Name = "List_users_by_account", Title = "List users by account", ReadOnly = true, Destructive = false), Description("List Vitally users for a specific account")]
    public static async Task<string> ListUsersByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of users to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/users", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_users_by_organization", Title = "List users by organization", ReadOnly = true, Destructive = false), Description("List Vitally users for a specific organisation")]
    public static async Task<string> ListUsersByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of users to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/users", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Search_users", Title = "Search users", ReadOnly = true, Destructive = false), Description("Search Vitally users by email or externalId")]
    public static async Task<string> SearchUsers(
        VitallyService vitallyService,
        [Description("Search query (email or externalId)")] string query,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        var additionalParams = new Dictionary<string, string> { ["query"] = query };
        return await vitallyService.GetResourcesAsync("users/search", 100, null, fields, null, additionalParams, traits);
    }

    [McpServerTool(Name = "Get_user", Title = "Get user", ReadOnly = true, Destructive = false), Description("Get a single Vitally user by ID")]
    public static async Task<string> GetUser(
        VitallyService vitallyService,
        [Description("The user ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("users", id, fields, traits);
    }

    [McpServerTool(Name = "Create_user", Title = "Create user", ReadOnly = false, Destructive = false), Description("Create a new Vitally user")]
    public static async Task<string> CreateUser(
        VitallyService vitallyService,
        [Description("JSON body containing user data. Required fields: externalId (string), and at least one of accountIds (array) or organizationIds (array). Optional: name, email, avatar, traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("users", jsonBody);
    }

    [McpServerTool(Name = "Update_user", Title = "Update user", ReadOnly = false, Destructive = true), Description("Update an existing Vitally user")]
    public static async Task<string> UpdateUser(
        VitallyService vitallyService,
        [Description("The user ID")] string id,
        [Description("JSON body containing fields to update. All fields optional: name, email, avatar, accountIds, organizationIds, traits (object)")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("users", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_user", Title = "Delete user", ReadOnly = false, Destructive = true), Description("Delete a Vitally user")]
    public static async Task<string> DeleteUser(
        VitallyService vitallyService,
        [Description("The user ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("users", id);
    }
}
