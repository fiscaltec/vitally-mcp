# SP5b — Composite `Get_organization_summary`

**Date:** 2026-06-25
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback item:** P2.13 (the composite half of SP5, deliberately split out from SP5a so its
tenant-specific data model could be validated against live Vitally first).

## 1. Purpose

The customer value report drives ~10+ separate Vitally calls per organisation (org get, traits,
goals instances, product-feedback instances, ticket rollups). In Claude Desktop (no local
filesystem, limited tool budget) that fan-out is costly and easy for the LLM to get wrong. A single
composite tool — `Get_organization_summary(organizationId)` — collapses the common shape into one
call returning the organisation, its rollup traits, its open goals, and its product feedback.

## 2. Live-validated data model

Confirmed against the live FISCAL tenant (temp key, EU region) before speccing — no assumptions:

- **Custom objects exist as named:** `customerGoals` and `productFeedback` are real custom objects
  in this tenant. Their ids are *not* hardcoded — they are resolved by name at runtime (ids can
  change between environments; names are stable tenant configuration).
- **Org `traits` object is keyed by full path**, e.g. `vitally.custom.countAllSupportTickets`,
  `vitally.custom.countOfOpenCustomerGoals`, `vitally.custom.npsScore`,
  `sfdc.Customer_Health_Score__c`. A curated default trait set therefore uses those full-path keys.
- **Scoped instance fetch works:** `customObjects/:id/instances/search?organizationId=…` returns a
  **bare array** (the SP1 lesson — not a `{results,next}` envelope) and does not paginate.

These names/paths are **tenant-specific**. The server runs on one shared Vitally key (single tenant
today), so curated defaults are pragmatic; they are isolated to one policy class and overridable per
call (see §4) so the tool is not silently hardcoded to one tenant.

## 3. Tool surface

New `[McpServerToolType]` class `Tools/SummaryTools.cs`, one tool method:

```
Get_organization_summary(
    VitallyService vitallyService,
    string organizationId,                                  // Vitally id OR externalId (org get-by-id accepts both)
    string? traits = null,                                  // null => curated rollup-trait set; override with a CSV of trait keys
    string? goalsObjectName = "customerGoals",              // custom object resolved to id by name at runtime
    string? productFeedbackObjectName = "productFeedback")  // custom object resolved to id by name at runtime
```

- `ReadOnly = true`, `Destructive = false`.
- Thin wrapper: resolves the effective traits CSV (default constant when `traits` is null/blank) and
  the two object names, then delegates to `VitallyService.GetOrganizationSummaryAsync(...)`.

### Response shape (single JSON object)

```json
{
  "organization":    { "id": "...", "name": "...", "mrr": ..., "healthScore": ..., "traits": { <curated rollups> } },
  "goals":           { "results": [ <customerGoals instances scoped to the org> ] },
  "productFeedback": { "results": [ <productFeedback instances scoped to the org> ] }
}
```

- `organization`: the org's default fields **plus** `traits` filtered to the curated/overridden set.
- `goals` / `productFeedback`: reuse SP1's custom-object-instance default-field projection (lean;
  traits excluded by default). The bare array from `/search` is wrapped as `{ "results": [...] }` for
  a consistent, self-describing shape.
- On a per-section failure, that section is replaced by `{ "error": "<message>" }` (see §5).

## 4. Tenant-coupling isolation (policy in the tool layer)

`SummaryTools` owns the tenant-specific policy as constants so the service stays generic:

- `DefaultGoalsObjectName = "customerGoals"`
- `DefaultProductFeedbackObjectName = "productFeedback"`
- `DefaultRollupTraits` — CSV of the curated full-path trait keys:
  `vitally.custom.countAllSupportTickets`, `vitally.custom.countAllProductFeedback`,
  `vitally.custom.countOfOpenCustomerGoals`, `vitally.custom.openZendeskTickets`,
  `vitally.custom.closedZendeskTickets`, `vitally.custom.npsScore`, `vitally.custom.npsGroup`,
  `vitally.custom.lastNpsFeedbackRollup`, `sfdc.Customer_Health_Score__c`,
  `sfdc.Health_Score_Status__c`, `sfdc.Renew_Date__c`, `vitally.custom.mostRecentRenewalDate`.

`VitallyService.GetOrganizationSummaryAsync(organizationId, traitsCsv, goalsObjectName,
productFeedbackObjectName, ct)` takes those as plain parameters and contains **no** tenant literals —
retuning the curated set or moving it to config later touches only `SummaryTools`.

## 5. Orchestration (in `VitallyService.GetOrganizationSummaryAsync`)

JSON shaping already lives in the service (field/trait filtering, the bare-array handling from SP1),
so the composite is assembled there and the tool stays thin. Steps:

1. **Organisation** — fetch the org by id with the org default fields + `traits`, filtered to
   `traitsCsv`. Reuses the existing get-by-id + filter path. If this fails, the exception propagates
   (a bad org id means there is no summary to return) — surfaced to the client by the existing
   `ToolErrorResult` call-tool filter.
2. **Resolve object ids** — a single `customObjects` list call (raw); map `name -> id` for both
   `goalsObjectName` and `productFeedbackObjectName` in one pass. A name with no match yields a
   per-section error (step 4), not a thrown exception.
3. **Scoped instances** — for each resolved id, call the existing scoped instance search
   (`customObjects/:id/instances/search?organizationId=<orgId>`), applying the instance default-field
   projection, wrapping the bare array as `{ "results": [...] }`.
4. **Assemble** — build the `{ organization, goals, productFeedback }` object. `goals` and
   `productFeedback` are each produced under an independent try/catch: any failure (object name not
   found, upstream 5xx, malformed body) is caught and that section becomes
   `{ "error": "<message>" }` so a single sub-failure never sinks the whole summary.

Total upstream calls: **4** (1 org get + 1 customObjects list + 2 instance searches), collapsing the
report's ~10+.

## 6. Non-goals (YAGNI)

- **Support-ticket / conversation rollups beyond the trait counts.** The `countAll*` / `*ZendeskTickets`
  traits already convey the volumes; pulling individual conversations into the summary is out of scope
  (callers use `List_conversations` with `source`/`status` + date filters from SP2/SP3).
- **Caching the `customObjects` resolution.** One extra list call per summary is negligible against
  the 1000 req/min budget; a cache is premature.
- **Multi-organisation / batch summaries.** One org per call.
- **Configuration surface for the curated defaults.** Constants in `SummaryTools` are sufficient
  while single-tenant; promoting them to `VitallyServerOptions` is a later change if a second tenant
  appears.
- **Account-level summary.** Rich data lives at the organisation level (per SP5a guidance).

## 7. Testing

- **`VitallyServiceTests` (additions)** — mock the 4 HTTP calls with Moq:
  - Happy path: org get + customObjects list + two `/search` calls returning the **real bare-array**
    shape → assert the assembled object has `organization.traits` filtered to the curated keys and
    `goals.results` / `productFeedback.results` populated.
  - Partial failure: one `/search` returns 500 → that section is `{ "error": ... }`, the other
    section and `organization` are still present.
  - Name not found: `customObjects` list lacks `goalsObjectName` → `goals` is `{ "error": ... }`.
  - Verb/URL assertions: the instance searches hit `customObjects/<id>/instances/search` with
    `organizationId=<orgId>`.
- **`Tools/SummaryToolsTests`** — `DefaultRollupTraits` contains the key full-path markers
  (`countAllSupportTickets`, `countOfOpenCustomerGoals`, `Customer_Health_Score__c`); default object
  names are `customerGoals` / `productFeedback`; the wrapper passes the effective values through to a
  stubbed/`TestHelpers.BuildVitallyService` service (happy-path string returned).
- All built via `TestHelpers.BuildVitallyService(httpClient)`.

## 8. Live validation (temp key, EU)

After the suite is green, run the local server in NoAuth dev mode and call
`Get_organization_summary` for Enfield (`d3639d2d-bf12-4848-b446-764f834bcd2a`):
expect `organization.traits` to include `countAllSupportTickets=10`, `countOfOpenCustomerGoals=5`,
`Customer_Health_Score__c=156`; `goals.results` to contain 1 instance; `productFeedback.results` to
be empty. Confirm the whole thing returns in one call.

## 9. Docs

- `CLAUDE.md`: add `Get_organization_summary` to the tool inventory and a short note under the tool
  structure / service sections describing the composite (4 upstream calls, curated overridable
  defaults isolated in `SummaryTools`, per-section error capture).

## 10. Acceptance criteria

- `Get_organization_summary(organizationId)` returns a single `{ organization, goals, productFeedback }`
  JSON object; `organization` carries the curated rollup traits; goals/feedback are the org-scoped
  custom-object instances.
- Object ids are resolved by name at runtime (no hardcoded ids); curated trait set + object names are
  overridable per call and isolated to `SummaryTools`.
- A failure of one sub-section yields a `{ "error": ... }` for that section only; a bad org id
  surfaces an error to the client.
- Full suite green; behaviour confirmed live against Enfield with the temp key.
