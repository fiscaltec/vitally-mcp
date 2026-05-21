using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

[McpServerToolType]
public static class CustomObjectsTools
{
    [McpServerTool(Name = "List_custom_objects", Title = "List custom objects", ReadOnly = true, Destructive = false), Description("List Vitally custom objects with optional pagination")]
    public static async Task<string> ListCustomObjects(
        VitallyService vitallyService,
        [Description("Maximum number of custom objects to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync("customObjects", limit, from, fields, sortBy, null, null);
    }

    [McpServerTool(Name = "Get_custom_object", Title = "Get custom object", ReadOnly = true, Destructive = false), Description("Get a single Vitally custom object by ID")]
    public static async Task<string> GetCustomObject(
        VitallyService vitallyService,
        [Description("The custom object ID")] string id,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        return await vitallyService.GetResourceByIdAsync("customObjects", id, fields);
    }

    [McpServerTool(Name = "List_custom_object_instances", Title = "List custom object instances", ReadOnly = true, Destructive = false), Description("List instances of a Vitally custom object with optional pagination")]
    public static async Task<string> ListCustomObjectInstances(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("Maximum number of instances to return (default: 20, max: 100)")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt)")] string? sortBy = null)
    {
        return await vitallyService.GetResourcesAsync($"customObjects/{customObjectId}/instances", limit, from, fields, sortBy, null, null);
    }

    [McpServerTool(Name = "Search_custom_object_instances", Title = "Search custom object instances", ReadOnly = true, Destructive = false), Description("Search for custom object instances by various criteria")]
    public static async Task<string> SearchCustomObjectInstances(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("Search criteria as query parameters (e.g., id, externalId, customerId, organizationId, customFieldId, customFieldValue)")] string searchQuery,
        [Description("Comma-separated list of fields to include. Defaults to: id,createdAt,updatedAt. Client-side filtering.")] string? fields = null)
    {
        // Parse search query (key=value pairs separated by &) into a parameter dictionary.
        // - Split on the first '=' so values can themselves contain '=' (e.g. a=b=c -> key "a", value "b=c").
        // - Pairs without an '=' are silently dropped.
        // - Duplicate keys: last value wins, matching typical lenient URL-form parsing.
        // - Percent-decode keys/values so callers can pass either raw ("Acme Corp") or
        //   already-encoded ("Acme%20Corp") values; VitallyService.GetResourcesAsync then
        //   re-encodes consistently, avoiding double-encoding (%20 -> %2520).
        var additionalParams = new Dictionary<string, string>();
        foreach (var part in searchQuery.Split('&'))
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length != 2) continue;
            additionalParams[Uri.UnescapeDataString(keyValue[0].Trim())] = Uri.UnescapeDataString(keyValue[1].Trim());
        }

        return await vitallyService.GetResourcesAsync($"customObjects/{customObjectId}/instances/search", 100, null, fields, null, additionalParams, null);
    }

    [McpServerTool(Name = "Create_custom_object", Title = "Create custom object", ReadOnly = false, Destructive = false), Description("Create a new Vitally custom object")]
    public static async Task<string> CreateCustomObject(
        VitallyService vitallyService,
        [Description("JSON body containing custom object data. Required fields vary by custom object type.")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync("customObjects", jsonBody);
    }

    [McpServerTool(Name = "Update_custom_object", Title = "Update custom object", ReadOnly = false, Destructive = true), Description("Update an existing Vitally custom object")]
    public static async Task<string> UpdateCustomObject(
        VitallyService vitallyService,
        [Description("The custom object ID")] string id,
        [Description("JSON body containing fields to update.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync("customObjects", id, jsonBody);
    }

    [McpServerTool(Name = "Create_custom_object_instance", Title = "Create custom object instance", ReadOnly = false, Destructive = false), Description("Create a new instance of a Vitally custom object")]
    public static async Task<string> CreateCustomObjectInstance(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("JSON body containing instance data. Required fields vary by custom object type.")] string jsonBody)
    {
        return await vitallyService.CreateResourceAsync($"customObjects/{customObjectId}/instances", jsonBody);
    }

    [McpServerTool(Name = "Update_custom_object_instance", Title = "Update custom object instance", ReadOnly = false, Destructive = true), Description("Update an existing custom object instance")]
    public static async Task<string> UpdateCustomObjectInstance(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("The instance ID")] string instanceId,
        [Description("JSON body containing fields to update.")] string jsonBody)
    {
        return await vitallyService.UpdateResourceAsync($"customObjects/{customObjectId}/instances", instanceId, jsonBody);
    }

    [McpServerTool(Name = "Delete_custom_object_instance", Title = "Delete custom object instance", ReadOnly = false, Destructive = true), Description("Delete a custom object instance")]
    public static async Task<string> DeleteCustomObjectInstance(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("The instance ID")] string instanceId)
    {
        return await vitallyService.DeleteResourceAsync($"customObjects/{customObjectId}/instances", instanceId);
    }
}
