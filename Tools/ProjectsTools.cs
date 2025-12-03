using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ProjectsTools
{
    [McpServerTool(Name = "List_projects", Title = "List projects", ReadOnly = true, Destructive = false), Description("List Vitally projects with optional pagination and field selection")]
    public static async Task<string> ListProjects(
        VitallyService vitallyService,
        [Description("Maximum number of projects to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,durationInDays'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync("projects", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_projects_by_account", Title = "List projects by account", ReadOnly = true, Destructive = false), Description("List Vitally projects for a specific account")]
    public static async Task<string> ListProjectsByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of projects to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/projects", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_projects_by_organization", Title = "List projects by organization", ReadOnly = true, Destructive = false), Description("List Vitally projects for a specific organisation")]
    public static async Task<string> ListProjectsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of projects to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/projects", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_project", Title = "Get project", ReadOnly = true, Destructive = false), Description("Get a single Vitally project by ID")]
    public static async Task<string> GetProject(
        VitallyService vitallyService,
        [Description("The project ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("projects", id, fields, traits);
    }

    [McpServerTool(Name = "Create_project_from_template", Title = "Create project from template", ReadOnly = false, Destructive = false), Description("Create a new Vitally project from a template")]
    public static async Task<string> CreateProjectFromTemplate(
        VitallyService vitallyService,
        [Description("JSON body containing project data. Required fields: templateId (string), accountId or organizationId (string). Optional: name, targetStartDate, targetCompletionDate, projectStatusId, projectCategoryId, ownedByVitallyUserId, traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("projects", jsonBody);
    }

    [McpServerTool(Name = "Update_project", Title = "Update project", ReadOnly = false, Destructive = true), Description("Update an existing Vitally project")]
    public static async Task<string> UpdateProject(
        VitallyService vitallyService,
        [Description("The project ID")] string id,
        [Description("JSON body containing fields to update. All fields optional: name, targetStartDate, targetCompletionDate, projectStatusId, projectCategoryId, ownedByVitallyUserId, traits (object)")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("projects", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_project", Title = "Delete project", ReadOnly = false, Destructive = true), Description("Delete a Vitally project")]
    public static async Task<string> DeleteProject(
        VitallyService vitallyService,
        [Description("The project ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("projects", id);
    }
}
