using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class TasksTools
{
    [McpServerTool(Name = "List_tasks", Title = "List tasks", ReadOnly = true), Description("List Vitally tasks with optional pagination and field selection")]
    public static async Task<string> ListTasks(
        VitallyService vitallyService,
        [Description("Maximum number of tasks to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,dueDate'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync("tasks", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "Get_task", Title = "Get task", ReadOnly = true), Description("Get a single Vitally task by ID")]
    public static async Task<string> GetTask(
        VitallyService vitallyService,
        [Description("The task ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("tasks", id, fields, traits);
    }
}
