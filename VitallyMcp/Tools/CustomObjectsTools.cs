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

    [McpServerTool(Name = "List_custom_object_instances", Title = "List custom object instances", ReadOnly = true, Destructive = false), Description("List instances of a Vitally custom object. Optionally scope to a single organisation, customer, external id, or custom-field value — Vitally allows exactly ONE scope criterion. When a scope is supplied the limit/from/sortBy paging params are ignored (the matching set is returned as Vitally provides it).")]
    public static async Task<string> ListCustomObjectInstances(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("Maximum number of instances for an UNSCOPED list (default: 20, max: 100). Ignored when a scope criterion is supplied.")] int limit = 20,
        [Description("Pagination cursor for an UNSCOPED list (use the 'next' value). Ignored when a scope criterion is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,externalId,createdAt,updatedAt,organizationId,customerId,archivedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort an UNSCOPED list by 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a scope criterion is supplied.")] string? sortBy = null,
        [Description("Comma-separated trait names to include (requires 'traits' in fields). Client-side filtering.")] string? traits = null,
        [Description("Scope to a single organisation ID. Mutually exclusive with the other scope criteria.")] string? organizationId = null,
        [Description("Scope to a single customer/account ID. Mutually exclusive with the other scope criteria.")] string? customerId = null,
        [Description("Scope to a single external ID. Mutually exclusive with the other scope criteria.")] string? externalId = null,
        [Description("Find instances where this custom field ID equals customFieldValue. Must be supplied together with customFieldValue.")] string? customFieldId = null,
        [Description("The value to match for customFieldId. Must be supplied together with customFieldId.")] string? customFieldValue = null)
    {
        var criteria = BuildInstanceSearchCriteria(organizationId, customerId, externalId, customFieldId, customFieldValue);

        if (criteria.Count == 0)
        {
            return await vitallyService.GetResourcesAsync(
                $"customObjects/{customObjectId}/instances", limit, from, fields, sortBy,
                additionalParams: null, traits: traits, defaultsKey: "customObjectInstances");
        }

        return await vitallyService.SearchCustomObjectInstancesAsync(customObjectId, criteria, fields, traits);
    }

    /// <summary>
    /// Builds the single-criterion search dictionary for instance scoping and validates Vitally's
    /// "exactly one criterion" rule (customFieldId+customFieldValue count as one paired criterion).
    /// Throws <see cref="ArgumentException"/> with an actionable message if the rule is violated.
    /// Returns an empty dictionary when no scope is supplied (caller uses the plain-list path).
    /// </summary>
    internal static Dictionary<string, string> BuildInstanceSearchCriteria(
        string? organizationId, string? customerId, string? externalId,
        string? customFieldId, string? customFieldValue)
    {
        var criteria = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(organizationId)) criteria["organizationId"] = organizationId;
        if (!string.IsNullOrWhiteSpace(customerId)) criteria["customerId"] = customerId;
        if (!string.IsNullOrWhiteSpace(externalId)) criteria["externalId"] = externalId;

        var hasFieldId = !string.IsNullOrWhiteSpace(customFieldId);
        var hasFieldValue = !string.IsNullOrWhiteSpace(customFieldValue);
        if (hasFieldId != hasFieldValue)
        {
            throw new ArgumentException("customFieldId and customFieldValue must be supplied together.");
        }

        var criterionGroups = criteria.Count + (hasFieldId ? 1 : 0);
        if (criterionGroups > 1)
        {
            throw new ArgumentException(
                "Vitally instance search accepts exactly one of organizationId, customerId, externalId, or customFieldId+customFieldValue.");
        }

        if (hasFieldId)
        {
            criteria["customFieldId"] = customFieldId!;
            criteria["customFieldValue"] = customFieldValue!;
        }

        return criteria;
    }

    [McpServerTool(Name = "Get_custom_object_instance", Title = "Get custom object instance", ReadOnly = true, Destructive = false), Description("Get a single custom object instance by its ID. Implemented via Vitally's instance search (Vitally has no direct single-instance GET). Returns a not-found message if the ID does not match.")]
    public static async Task<string> GetCustomObjectInstance(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("The instance ID")] string instanceId,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,externalId,createdAt,updatedAt,organizationId,customerId,archivedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated trait names to include (requires 'traits' in fields). Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetCustomObjectInstanceByIdAsync(customObjectId, instanceId, fields, traits);
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
