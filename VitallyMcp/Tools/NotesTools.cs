using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class NotesTools
{
    [McpServerTool(Name = "List_notes", Title = "List notes", ReadOnly = true, Destructive = false), Description("List Vitally notes with optional pagination and field selection")]
    public static async Task<string> ListNotes(
        VitallyService vitallyService,
        [Description("Maximum number of notes to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,subject,note'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync("notes", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_notes_by_account", Title = "List notes by account", ReadOnly = true, Destructive = false), Description("List Vitally notes for a specific account")]
    public static async Task<string> ListNotesByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of notes to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/notes", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_notes_by_organization", Title = "List notes by organization", ReadOnly = true, Destructive = false), Description("List Vitally notes for a specific organisation")]
    public static async Task<string> ListNotesByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of notes to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/notes", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_note_categories", Title = "List note categories", ReadOnly = true, Destructive = false), Description("List Vitally note categories with optional pagination")]
    public static async Task<string> ListNoteCategories(
        VitallyService vitallyService,
        [Description("Maximum number of note categories to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("noteCategories", limit, from, fields, sortBy, null, null);
    }

    [McpServerTool(Name = "Get_note", Title = "Get note", ReadOnly = true, Destructive = false), Description("Get a single Vitally note by ID")]
    public static async Task<string> GetNote(
        VitallyService vitallyService,
        [Description("The note ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("notes", id, fields, traits);
    }

    [McpServerTool(Name = "Create_note", Title = "Create note", ReadOnly = false, Destructive = false), Description("Create a new Vitally note")]
    public static async Task<string> CreateNote(
        VitallyService vitallyService,
        [Description("JSON body containing note data. Required fields: accountId or organizationId (string), note (string, may include HTML), noteDate (timestamp). Optional: subject, traits, tags (array)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("notes", jsonBody);
    }

    [McpServerTool(Name = "Update_note", Title = "Update note", ReadOnly = false, Destructive = true), Description("Update an existing Vitally note")]
    public static async Task<string> UpdateNote(
        VitallyService vitallyService,
        [Description("The note ID")] string id,
        [Description("JSON body containing fields to update. All fields optional. IMPORTANT: When updating notes with tags, include complete tags array or all tags will be removed.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("notes", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_note", Title = "Delete note", ReadOnly = false, Destructive = true), Description("Delete a Vitally note")]
    public static async Task<string> DeleteNote(
        VitallyService vitallyService,
        [Description("The note ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("notes", id);
    }
}
