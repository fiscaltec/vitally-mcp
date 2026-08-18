using System.ComponentModel;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ConversationsTools
{
    [Authorize(Policy = "vitally:read")]
    [McpServerTool(Name = "List_conversations", Title = "List conversations", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List Vitally conversations with optional pagination and field selection")]
    public static async Task<string> ListConversations(
        VitallyService vitallyService,
        [Description("Maximum number of conversations to return (default: 20, max: 100). Ignored when a created date range is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when a created date range is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,subject,status'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a created date range is supplied.")] string? sortBy = null,
        [Description("ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored.")] string? createdAfter = null,
        [Description("ISO-8601 upper bound on createdAt. See createdAfter.")] string? createdBefore = null)
    {
        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync("conversations", createdAfter, createdBefore, fields, defaultsKey: "conversations");
        }

        return await vitallyService.GetResourcesAsync("conversations", limit, from, fields, sortBy);
    }

    [Authorize(Policy = "vitally:read")]
    [McpServerTool(Name = "List_conversations_by_account", Title = "List conversations by account", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List Vitally conversations for a specific account")]
    public static async Task<string> ListConversationsByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of conversations to return (default: 20, max: 100). Ignored when a created date range is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when a created date range is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a created date range is supplied.")] string? sortBy = null,
        [Description("ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored.")] string? createdAfter = null,
        [Description("ISO-8601 upper bound on createdAt. See createdAfter.")] string? createdBefore = null)
    {
        var resourceType = $"accounts/{accountId}/conversations";
        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync(resourceType, createdAfter, createdBefore, fields, defaultsKey: "conversations");
        }

        return await vitallyService.GetResourcesAsync(resourceType, limit, from, fields, sortBy);
    }

    [Authorize(Policy = "vitally:read")]
    [McpServerTool(Name = "List_conversations_by_organization", Title = "List conversations by organization", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List Vitally conversations for a specific organisation")]
    public static async Task<string> ListConversationsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of conversations to return (default: 20, max: 100). Ignored when a created date range is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when a created date range is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a created date range is supplied.")] string? sortBy = null,
        [Description("ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored.")] string? createdAfter = null,
        [Description("ISO-8601 upper bound on createdAt. See createdAfter.")] string? createdBefore = null)
    {
        var resourceType = $"organizations/{organizationId}/conversations";
        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync(resourceType, createdAfter, createdBefore, fields, defaultsKey: "conversations");
        }

        return await vitallyService.GetResourcesAsync(resourceType, limit, from, fields, sortBy);
    }

    [Authorize(Policy = "vitally:read")]
    [McpServerTool(Name = "Get_conversation", Title = "Get conversation", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("Get a single Vitally conversation by ID")]
    public static async Task<string> GetConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("conversations", id, fields);
    }

    [Authorize(Policy = "vitally:write")]
    [McpServerTool(Name = "Create_conversation", Title = "Create conversation", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Create a new Vitally conversation with messages")]
    public static async Task<string> CreateConversation(
        VitallyService vitallyService,
        [Description("JSON body containing conversation data. Required fields: externalId (string), subject (string), messages (array of message objects). Optional: traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("conversations", jsonBody);
    }

    [Authorize(Policy = "vitally:write")]
    [McpServerTool(Name = "Update_conversation", Title = "Update conversation", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Update an existing Vitally conversation and its messages")]
    public static async Task<string> UpdateConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id,
        [Description("JSON body containing fields to update. Required fields: subject (string), messages (array). Optional: traits (object)")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("conversations", id, jsonBody);
    }

    [Authorize(Policy = "vitally:delete")]
    [McpServerTool(Name = "Delete_conversation", Title = "Delete conversation", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Delete a Vitally conversation including all messages")]
    public static async Task<string> DeleteConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("conversations", id);
    }
}
