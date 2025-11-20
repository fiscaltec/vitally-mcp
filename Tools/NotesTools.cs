using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class NotesTools
{
    [McpServerTool, Description("List Vitally notes with optional pagination and field selection")]
    public static async Task<string> ListNotes(
        VitallyService vitallyService,
        [Description("Maximum number of notes to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response")] string? cursor = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,subject,note')")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("notes", limit, cursor, fields);
    }

    [McpServerTool, Description("Get a single Vitally note by ID")]
    public static async Task<string> GetNote(
        VitallyService vitallyService,
        [Description("The note ID")] string id,
        [Description("Comma-separated list of fields to include")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("notes", id, fields);
    }
}
