# SP5a — Server-level MCP `instructions`

**Date:** 2026-06-25
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback item:** P2.12 (the guidance half of SP5). The composite `Get_organization_summary`
(P2.13) is deliberately split out into its own later spec — its data model (which custom object is
"goals", what "product feedback"/"ticket rollups" map to) is tenant-specific and needs live
validation with a Vitally key.

## 1. Purpose

The server ships no usage guidance, so the most common failure watched in the value-report runs was
the LLM wandering into the *account* when the rich data lives at the *organisation* (plus
trait-vs-custom-object confusion). MCP lets a server publish free-text `instructions` in the
`initialize` response — a single place that steers every client/LLM at once. Add concise,
accurate guidance there.

## 2. Design

- **`VitallyServerInstructions`** (new static class, `VitallyMcp`): a single `public const string
  Text` holding the guidance. Keeping it in a named constant (not inline in `Program.cs`) makes it
  unit-testable and keeps `Program.cs` tidy.
- **`Program.cs`:** pass the text to the MCP server via the `AddMcpServer` options action —
  `AddMcpServer(options => options.ServerInstructions = VitallyServerInstructions.Text)` — then the
  existing `.WithHttpTransport(...).WithToolsFromAssembly()` and `WithRequestFilters(...)` chain is
  unchanged. (`McpServerOptions.ServerInstructions` is confirmed present in the installed
  `ModelContextProtocol` 1.3.0. If the exact option property name differs, adjust to the installed
  API — the intent is: set the server's `instructions`.)

### Guidance content (the `Text`)
Concise, accurate, and references the tools actually shipped (SP1–SP4). It deliberately phrases
custom-object/goals discovery generically rather than asserting a specific object name
(`customerGoals`) that is unverified for a given tenant:

> Vitally MCP server. To use it well:
> • Rich customer data lives at the **organisation** level (mrr, healthScore, renewal dates, NPS,
>   segments, traits). Account-level traits are often empty — prefer organisations for customer
>   context.
> • Traits ≠ custom objects. Traits are key/value fields on a resource (request via the `traits`
>   parameter; excluded by default). Custom objects are separate record types (goals, opportunities,
>   …) — discover them with `List_custom_objects`, then get one organisation's instances in a single
>   call: `List_custom_object_instances(customObjectId, organizationId=…)`.
> • Find a customer with `List_organizations(nameContains=…)`. Scope activity to a period with
>   `createdAfter`/`createdBefore` on `List_conversations`/`_notes`/`_tasks`/`_meetings`.
>   Conversations carry `source` (e.g. outlook/intercom) and `status` to tell support tickets from
>   calendar/email.
> • Read-only deployments deny writes and hide create/update/delete tools; a permission error means
>   your token lacks the required tier.

(Exact wording may be lightly adjusted in implementation; the four themes above are the
requirements: organisation-level data, traits-vs-custom-objects + instance scoping, the
find/period-scope/source tools, and the read-only/permission model.)

## 3. Non-goals (YAGNI)

- The composite `Get_organization_summary` (P2.13) — separate spec, needs live data validation.
- Per-tool `[Description]` rewrites — the single server `instructions` block covers the guidance
  need; scattered description edits are out of scope.
- Asserting tenant-specific names (e.g. a literal `customerGoals` object) as fact — guidance is
  phrased so the LLM discovers the tenant's actual objects.

## 4. Testing

- **`VitallyServerInstructionsTests`** (unit): `Text` is non-empty and contains the key guidance
  markers (e.g. "organisation", "Traits", "custom object", "nameContains", "read-only") so a future
  edit can't silently gut it.
- **Integration** (the existing `WebApplicationFactory` harness, as used by `ReadOnlyToolsListTests`
  / `OAuthProxyEndpointsTests`): a `tools`-less MCP `initialize` request to `/mcp` returns a result
  whose `instructions` field contains a distinctive marker from the guidance. This proves the text
  is actually wired into the server's `initialize` response end-to-end.

## 5. Docs

- `CLAUDE.md`: in the hosting/transport section, note that the server publishes guidance via the
  MCP `instructions` field (`McpServerOptions.ServerInstructions`, text in
  `VitallyServerInstructions`).

## 6. Acceptance criteria

- The MCP `initialize` response includes the guidance in its `instructions` field.
- The guidance covers: organisation-level data, traits-vs-custom-objects + instance scoping, the
  find/period-scope/source tooling, and the read-only/permission model — without asserting an
  unverified tenant-specific object name.
- Full suite green; behaviour confirmed via the integration test (and a quick live `initialize`).
