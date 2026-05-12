using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class NpsResponsesTools
{
    [McpServerTool(Name = "List_nps_responses", Title = "List nps responses", ReadOnly = true, Destructive = false), Description("List Vitally NPS responses with optional pagination and field selection")]
    public static async Task<string> ListNpsResponses(
        VitallyService vitallyService,
        [Description("Maximum number of NPS responses to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,score,feedback'). Defaults to: id,externalId,userId,score,respondedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Target type: 'accounts' or 'organization' (default: accounts)")] string? target = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(target))
        {
            additionalParams["target"] = target;
        }

        return await vitallyService.GetResourcesAsync("npsResponses", limit, from, fields, sortBy, additionalParams);
    }

    [McpServerTool(Name = "List_nps_responses_by_account", Title = "List nps responses by account", ReadOnly = true, Destructive = false), Description("List Vitally NPS responses for a specific account")]
    public static async Task<string> ListNpsResponsesByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of NPS responses to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,externalId,userId,score,respondedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/npsResponses", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "List_nps_responses_by_organization", Title = "List nps responses by organization", ReadOnly = true, Destructive = false), Description("List Vitally NPS responses for a specific organisation")]
    public static async Task<string> ListNpsResponsesByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of NPS responses to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,externalId,userId,score,respondedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/npsResponses", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "Get_nps_response", Title = "Get nps response", ReadOnly = true, Destructive = false), Description("Get a single Vitally NPS response by ID")]
    public static async Task<string> GetNpsResponse(
        VitallyService vitallyService,
        [Description("The NPS response ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,externalId,userId,score,respondedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("npsResponses", id, fields);
    }

    [McpServerTool(Name = "Create_nps_response", Title = "Create nps response", ReadOnly = false, Destructive = false), Description("Create a new Vitally NPS response (or update if externalId exists)")]
    public static async Task<string> CreateNpsResponse(
        VitallyService vitallyService,
        [Description("JSON body containing NPS response data. Required fields: userId (string), respondedAt (timestamp), score (number). Optional: externalId, feedback. Note: NPS responses are unique on externalId, so this can create or update.")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("npsResponses", jsonBody);
    }

    [McpServerTool(Name = "Update_nps_response", Title = "Update nps response", ReadOnly = false, Destructive = true), Description("Update an existing Vitally NPS response")]
    public static async Task<string> UpdateNpsResponse(
        VitallyService vitallyService,
        [Description("The NPS response ID or externalId")] string id,
        [Description("JSON body containing fields to update. Required fields: userId, respondedAt, score. Optional: feedback")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("npsResponses", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_nps_response", Title = "Delete nps response", ReadOnly = false, Destructive = true), Description("Delete a Vitally NPS response")]
    public static async Task<string> DeleteNpsResponse(
        VitallyService vitallyService,
        [Description("The NPS response ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("npsResponses", id);
    }
}
