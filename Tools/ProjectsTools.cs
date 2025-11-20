using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ProjectsTools
{
    [McpServerTool, Description("List Vitally projects with optional pagination and field selection")]
    public static async Task<string> ListProjects(
        VitallyService vitallyService,
        [Description("Maximum number of projects to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response")] string? cursor = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,durationInDays')")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("projects", limit, cursor, fields);
    }

    [McpServerTool, Description("Get a single Vitally project by ID")]
    public static async Task<string> GetProject(
        VitallyService vitallyService,
        [Description("The project ID")] string id,
        [Description("Comma-separated list of fields to include")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("projects", id, fields);
    }
}
