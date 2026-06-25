namespace VitallyMcp;

/// <summary>
/// Usage guidance published in the MCP <c>initialize</c> response (<c>instructions</c> field) so
/// every connecting client/LLM is steered toward effective use of the Vitally server. Kept in one
/// named constant so it is unit-testable and not buried inline in <c>Program.cs</c>.
/// </summary>
public static class VitallyServerInstructions
{
    public const string Text = """
        Vitally MCP server - how to use it well:
        - Rich customer data lives at the ORGANISATION level (mrr, healthScore, renewal dates, NPS,
          segments, traits). Account-level traits are often empty, so prefer organisations for
          customer context.
        - Traits are not the same as custom objects. Traits are key/value fields on a resource
          (request them via the 'traits' parameter; they are excluded by default). Custom objects
          are separate record types (e.g. goals, opportunities). Discover a tenant's custom objects
          with List_custom_objects, then get one organisation's instances in a single call:
          List_custom_object_instances(customObjectId, organizationId=...).
        - Find a customer with List_organizations(nameContains=...). Scope activity to a period with
          createdAfter/createdBefore on List_conversations, List_notes, List_tasks and List_meetings.
          Conversations carry 'source' (e.g. outlook, intercom) and 'status' to tell support tickets
          from calendar/email.
        - Read-only deployments deny all writes and hide the create/update/delete tools; a
          permission error means your token lacks the required tier.
        """;
}
