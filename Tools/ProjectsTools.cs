using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ProjectsTools
{
    [McpServerTool(Name = "List_projects", Title = "List projects", ReadOnly = true), Description("List Vitally projects with optional pagination and field selection")]
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

    [McpServerTool(Name = "Get_project", Title = "Get project", ReadOnly = true), Description("Get a single Vitally project by ID")]
    public static async Task<string> GetProject(
        VitallyService vitallyService,
        [Description("The project ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("projects", id, fields, traits);
    }
}
