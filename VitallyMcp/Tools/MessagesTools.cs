using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class MessagesTools
{
    [McpServerTool(Name = "List_messages_by_conversation", Title = "List messages by conversation", ReadOnly = true, Destructive = false), Description("List Vitally messages for a specific conversation")]
    public static async Task<string> ListMessagesByConversation(
        VitallyService vitallyService,
        [Description("The conversation ID")] string conversationId,
        [Description("Maximum number of messages to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,type,timestamp,message,from,to. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"conversations/{conversationId}/messages", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "Get_message", Title = "Get message", ReadOnly = true, Destructive = false), Description("Get a single Vitally message by ID")]
    public static async Task<string> GetMessage(
        VitallyService vitallyService,
        [Description("The message ID")] string id,
        [Description("Comma-separated list of fields to include (e.g., 'id,message,from,to'). Defaults to: id,type,timestamp,message,from,to. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("messages", id, fields);
    }

    [McpServerTool(Name = "Create_message", Title = "Create message", ReadOnly = false, Destructive = false), Description("Create a new message in a Vitally conversation")]
    public static async Task<string> CreateMessage(
        VitallyService vitallyService,
        [Description("The conversation ID")] string conversationId,
        [Description("JSON body containing message data. Required fields: externalId (string), message (string), from (string), to (string). Optional: type, timestamp")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync($"conversations/{conversationId}/messages", jsonBody);
    }

    [McpServerTool(Name = "Delete_message", Title = "Delete message", ReadOnly = false, Destructive = true), Description("Delete a Vitally message")]
    public static async Task<string> DeleteMessage(
        VitallyService vitallyService,
        [Description("The message ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("messages", id);
    }
}
