using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class ProjectTemplatesTools
{
    [McpServerTool(Name = "List_project_templates", Title = "List project templates", ReadOnly = true, Destructive = false), Description("List Vitally project templates with optional pagination and filtering")]
    public static async Task<string> ListProjectTemplates(
        VitallyService vitallyService,
        [Description("Maximum number of project templates to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,description'). Defaults to: id,name,createdAt,updatedAt,projectCategoryId. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null,
        [Description("Filter by project category ID")] string? categoryId = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(categoryId))
        {
            additionalParams["categoryId"] = categoryId;
        }

        return await vitallyService.GetResourcesAsync("projectTemplates", limit, from, fields, sortBy, additionalParams, traits);
    }

    [McpServerTool(Name = "Get_project_template", Title = "Get project template", ReadOnly = true, Destructive = false), Description("Get a single Vitally project template by ID")]
    public static async Task<string> GetProjectTemplate(
        VitallyService vitallyService,
        [Description("The project template ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt,projectCategoryId. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated list of trait names to include (e.g., 'customField1,customField2'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetResourceByIdAsync("projectTemplates", id, fields, traits);
    }

    [McpServerTool(Name = "List_project_categories", Title = "List project categories", ReadOnly = true, Destructive = false), Description("List Vitally project categories with optional pagination")]
    public static async Task<string> ListProjectCategories(
        VitallyService vitallyService,
        [Description("Maximum number of project categories to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("projectCategories", limit, from, fields, sortBy);
    }

    [McpServerTool(Name = "Get_project_category", Title = "Get project category", ReadOnly = true, Destructive = false), Description("Get a single Vitally project category by ID")]
    public static async Task<string> GetProjectCategory(
        VitallyService vitallyService,
        [Description("The project category ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("projectCategories", id, fields);
    }
}
