using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ConversationsTools
{
    [McpServerTool, Description("List Vitally conversations with optional pagination and field selection")]
    public static async Task<string> ListConversations(
        VitallyService vitallyService,
        [Description("Maximum number of conversations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,subject,status'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("conversations", limit, from, fields, sortBy);
    }

    [McpServerTool, Description("Get a single Vitally conversation by ID")]
    public static async Task<string> GetConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("conversations", id, fields);
    }
}
