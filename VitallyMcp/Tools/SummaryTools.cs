using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

/// <summary>
/// Composite, read-only summary tool. Collapses the common "give me everything about this customer"
/// shape into one call. Owns the tenant-specific policy (the curated rollup-trait set and the two
/// custom-object names) as constants; the orchestration in
/// <see cref="VitallyService.GetOrganizationSummaryAsync"/> is generic and takes these as arguments,
/// so retuning them — or promoting them to configuration for a second tenant — touches only this file.
/// </summary>
[McpServerToolType]
public static class SummaryTools
{
    /// <summary>Default custom object treated as the customer's goals (resolved to an id by name at runtime).</summary>
    public const string DefaultGoalsObjectName = "customerGoals";

    /// <summary>Default custom object treated as the customer's product feedback (resolved by name at runtime).</summary>
    public const string DefaultProductFeedbackObjectName = "productFeedback";

    /// <summary>
    /// Curated organisation rollup traits surfaced by default, as full trait-key paths (the org
    /// traits object is keyed by path, e.g. <c>vitally.custom.countAllSupportTickets</c>). Tuned to
    /// the FISCAL tenant; overridable per call via the <c>traits</c> parameter.
    /// </summary>
    public const string DefaultRollupTraits =
        "vitally.custom.countAllSupportTickets,vitally.custom.countAllProductFeedback," +
        "vitally.custom.countOfOpenCustomerGoals,vitally.custom.openZendeskTickets," +
        "vitally.custom.closedZendeskTickets,vitally.custom.npsScore,vitally.custom.npsGroup," +
        "vitally.custom.lastNpsFeedbackRollup,sfdc.Customer_Health_Score__c," +
        "sfdc.Health_Score_Status__c,sfdc.Renew_Date__c,vitally.custom.mostRecentRenewalDate";

    [McpServerTool(Name = "Get_organization_summary", Title = "Get organization summary", ReadOnly = true, Destructive = false),
     Description("One-call customer summary for an organisation: the organisation with its curated rollup traits (support-ticket / product-feedback / open-goal counts, NPS, health, renewal), plus its open goals and product-feedback custom-object instances. Collapses the ~10+ calls a full customer review otherwise needs. Returns { organization, goals, productFeedback }; goals/productFeedback are each { results: [...] } or { error: ... } if that part could not be fetched.")]
    public static async Task<string> GetOrganizationSummary(
        VitallyService vitallyService,
        [Description("Organisation id (Vitally id or externalId).")] string organizationId,
        [Description("Optional comma-separated trait keys to override the curated default rollup set.")] string? traits = null,
        [Description("Optional custom-object name to use as the customer's goals (default 'customerGoals'). Resolved to an id by name.")] string? goalsObjectName = null,
        [Description("Optional custom-object name to use as product feedback (default 'productFeedback'). Resolved to an id by name.")] string? productFeedbackObjectName = null)
    {
        // Trim the object-name overrides: LLM-supplied args often carry stray whitespace, which would
        // otherwise miss the name match in ResolveCustomObjectIdsAsync and yield a false "not found".
        var effectiveTraits = string.IsNullOrWhiteSpace(traits) ? DefaultRollupTraits : traits;
        var effectiveGoals = string.IsNullOrWhiteSpace(goalsObjectName) ? DefaultGoalsObjectName : goalsObjectName.Trim();
        var effectiveFeedback = string.IsNullOrWhiteSpace(productFeedbackObjectName) ? DefaultProductFeedbackObjectName : productFeedbackObjectName.Trim();

        return await vitallyService.GetOrganizationSummaryAsync(
            organizationId, effectiveTraits, effectiveGoals, effectiveFeedback);
    }
}
