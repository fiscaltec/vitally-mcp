using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class AdminsTools
{
    [McpServerTool(Name = "Search_admins", Title = "Search admins", ReadOnly = true, Destructive = false), Description("Search Vitally admins by email")]
    public static async Task<string> SearchAdmins(
        VitallyService vitallyService,
        [Description("Email address to search for")] string email,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,email. Client-side filtering.")] string? fields = null)
    {
        var additionalParams = new Dictionary<string, string> { ["email"] = email };
        return await vitallyService.GetResourcesAsync("admins/search", 100, null, fields, null, additionalParams, null);
    }
}
