using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class TasksTools
{
    [McpServerTool(Name = "List_tasks", Title = "List tasks", ReadOnly = true, Destructive = false), Description("List Vitally tasks with optional pagination and field selection")]
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

    [McpServerTool(Name = "List_tasks_by_account", Title = "List tasks by account", ReadOnly = true, Destructive = false), Description("List Vitally tasks for a specific account")]
    public static async Task<string> ListTasksByAccount(
        VitallyService vitallyService,
        [Description("The account ID")] string accountId,
        [Description("Maximum number of tasks to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"accounts/{accountId}/tasks", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_tasks_by_organization", Title = "List tasks by organization", ReadOnly = true, Destructive = false), Description("List Vitally tasks for a specific organisation")]
    public static async Task<string> ListTasksByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of tasks to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourcesAsync($"organizations/{organizationId}/tasks", limit, from, fields, sortBy, null, traits);
    }

    [McpServerTool(Name = "List_task_categories", Title = "List task categories", ReadOnly = true, Destructive = false), Description("List Vitally task categories with optional pagination")]
    public static async Task<string> ListTaskCategories(
        VitallyService vitallyService,
        [Description("Maximum number of task categories to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("taskCategories", limit, from, fields, sortBy, null, null);
    }

    [McpServerTool(Name = "Get_task", Title = "Get task", ReadOnly = true, Destructive = false), Description("Get a single Vitally task by ID")]
    public static async Task<string> GetTask(
        VitallyService vitallyService,
        [Description("The task ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("tasks", id, fields, traits);
    }

    [McpServerTool(Name = "Create_task", Title = "Create task", ReadOnly = false, Destructive = false), Description("Create a new Vitally task")]
    public static async Task<string> CreateTask(
        VitallyService vitallyService,
        [Description("JSON body containing task data. Required fields: name (string), accountId or organizationId (string). Optional: dueDate, assignedToId, traits (object)")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("tasks", jsonBody);
    }

    [McpServerTool(Name = "Update_task", Title = "Update task", ReadOnly = false, Destructive = true), Description("Update an existing Vitally task")]
    public static async Task<string> UpdateTask(
        VitallyService vitallyService,
        [Description("The task ID")] string id,
        [Description("JSON body containing fields to update. All fields optional: name, dueDate, completedAt, assignedToId, traits (object). Set trait to null to remove it.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("tasks", id, jsonBody);
    }

    [McpServerTool(Name = "Delete_task", Title = "Delete task", ReadOnly = false, Destructive = true), Description("Delete a Vitally task")]
    public static async Task<string> DeleteTask(
        VitallyService vitallyService,
        [Description("The task ID")] string id)
    {
        return await vitallyService.DeleteResourceAsync("tasks", id);
    }
}
