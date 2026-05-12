using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class OrganizationsTools
{
    [McpServerTool(Name = "List_organizations", Title = "List organizations", ReadOnly = true, Destructive = false), Description("List Vitally organisations with optional pagination and field selection")]
    public static async Task<string> ListOrganizations(
        VitallyService vitallyService,
        [Description("Maximum number of organisations to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync("organizations", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_organization", Title = "Get organization", ReadOnly = true, Destructive = false), Description("Get a single Vitally organisation by ID")]
    public static async Task<string> GetOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("organizations", id, fields, traits);
    }

    [McpServerTool(Name = "Create_organization", Title = "Create organization", ReadOnly = false, Destructive = false), Description("Create a new Vitally organisation")]
    public static async Task<string> CreateOrganization(
        VitallyService vitallyService,
        [Description("JSON body containing organisation data. Required fields: externalId (string), name (string). Optional: traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("organizations", jsonBody);
    }

    [McpServerTool(Name = "Update_organization", Title = "Update organization", ReadOnly = false, Destructive = true), Description("Update an existing Vitally organisation")]
    public static async Task<string> UpdateOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string id,
        [Description("JSON body containing fields to update. Optional fields: name, traits (object). Traits are merged with existing data.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("organizations", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_organization", Title = "Delete organization", ReadOnly = false, Destructive = true), Description("Delete a Vitally organisation")]
    public static async Task<string> DeleteOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("organizations", id);
    }
}
