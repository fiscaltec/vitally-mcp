using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ConversationsTools
{
    [McpServerTool(Name = "List_conversations", Title = "List conversations", ReadOnly = true, Destructive = false), Description("List Vitally conversations with optional pagination and field selection")]
    public static async Task<string> ListConversations(
        VitallyService vitallyService,
        [Description("Maximum number of conversations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,subject,status'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("conversations", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "List_conversations_by_account", Title = "List conversations by account", ReadOnly = true, Destructive = false), Description("List Vitally conversations for a specific account")]
    public static async Task<string> ListConversationsByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of conversations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/conversations", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "List_conversations_by_organization", Title = "List conversations by organization", ReadOnly = true, Destructive = false), Description("List Vitally conversations for a specific organisation")]
    public static async Task<string> ListConversationsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of conversations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/conversations", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "Get_conversation", Title = "Get conversation", ReadOnly = true, Destructive = false), Description("Get a single Vitally conversation by ID")]
    public static async Task<string> GetConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("conversations", id, fields);
    }

    [McpServerTool(Name = "Create_conversation", Title = "Create conversation", ReadOnly = false, Destructive = false), Description("Create a new Vitally conversation with messages")]
    public static async Task<string> CreateConversation(
        VitallyService vitallyService,
        [Description("JSON body containing conversation data. Required fields: externalId (string), subject (string), messages (array of message objects). Optional: traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("conversations", jsonBody);
    }

    [McpServerTool(Name = "Update_conversation", Title = "Update conversation", ReadOnly = false, Destructive = true), Description("Update an existing Vitally conversation and its messages")]
    public static async Task<string> UpdateConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id,
        [Description("JSON body containing fields to update. Required fields: subject (string), messages (array). Optional: traits (object)")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("conversations", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_conversation", Title = "Delete conversation", ReadOnly = false, Destructive = true), Description("Delete a Vitally conversation including all messages")]
    public static async Task<string> DeleteConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("conversations", id);
    }
}
