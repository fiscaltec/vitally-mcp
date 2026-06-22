using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class CustomTraitsTools
{
    [McpServerTool(Name = "List_custom_traits", Title = "List custom traits", ReadOnly = true, Destructive = false), Description("List custom trait definitions (custom fields) configured on a Vitally model. Returns trait label, type, path and createdAt. Useful for discovering which trait names can be passed to the 'traits' parameter on other tools.")]
    public static async Task<string> ListCustomTraits(
        VitallyService vitallyService,
        [Description("The model to list traits for. One of: users, accounts, organizations, customObjects, tasks, notes, projects, conversations, team")] string model,
        [Description("Required when model='customObjects': the ID of the custom object whose traits should be returned")] string? customObjectId = null,
        [Description("Case-insensitive substring to filter the trait catalogue by label or path (client-side). Useful to trim the otherwise large catalogue.")] string? nameContains = null)
    {
        var queryParams = new Dictionary<string, string> { ["model"] = model };
        if (!string.IsNullOrEmpty(customObjectId))
        {
            queryParams["customObjectId"] = customObjectId;
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            return await vitallyService.GetRawArrayFilteredAsync("customFields", queryParams, nameContains);
        }

        return await vitallyService.GetRawAsync("customFields", queryParams);
    }
}
