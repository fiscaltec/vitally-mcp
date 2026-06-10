# SP1 — Custom-object-instance scoping & ergonomics

**Date:** 2026-06-10
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback items covered:** P1.1 (org scoping), P1.6 (broken search), P2.7 (get single instance), P2.9 (traits + default fields on instance lists).

## 1. Purpose

Make "give me *this organisation's* goal instances" a single cheap MCP call instead of paging
every customer's instances (~1.7 MB/page) and sifting client-side; let callers read one instance
by id; let callers subset traits and get a useful default field set on instance lists; and remove
the consistently-broken free-text search tool that erodes trust in the surface.

## 2. Goals / non-goals

**Goals**
- Scope instance listing by a single Vitally `/search` criterion via typed parameters.
- Add a single-instance read (`Get_custom_object_instance`) using search-by-id.
- Give instance lists a real default field set and expose trait subsetting.
- Remove `Search_custom_object_instances`.

**Non-goals**
- Server-side date-range / name filtering (that is SP3's "page-and-filter" mechanism).
- A composite organisation summary (SP5).
- Any change to other resource types.

## 3. Public tool surface (the contract)

### 3.1 `List_custom_object_instances` (modified)

Existing params unchanged: `customObjectId` (required), `limit` (default 20), `from`, `fields`,
`sortBy`. New optional params:

| Param | Purpose |
|---|---|
| `organizationId` | Scope to one organisation (the headline win) |
| `customerId` | Scope to one customer/account |
| `externalId` | Scope by external key |
| `customFieldId` | Find instances where a custom field equals a value — **must** accompany `customFieldValue` |
| `customFieldValue` | The value to match — **must** accompany `customFieldId` |
| `traits` | Comma-separated trait names to include (requires `traits` in `fields`) |

**Routing rule** — Vitally's `/search` accepts *exactly one* criterion, where
`customFieldId`+`customFieldValue` count as one paired criterion:

- **Zero** scope criteria → plain list as today:
  `GET /resources/customObjects/{customObjectId}/instances?limit=…&from=…&sortBy=…`.
- **Exactly one** criterion → search:
  `GET /resources/customObjects/{customObjectId}/instances/search?{criterion}` — **only the
  criterion is sent** (no `limit`/`from`/`sortBy`; see §6). Client-side field/trait filtering and
  `next` pass-through are applied as for any list.
- **More than one** scope criterion → throw `ArgumentException` before any HTTP call:
  *"Vitally instance search accepts exactly one of organizationId, customerId, externalId, or
  customFieldId+customFieldValue."*
- `customFieldId` without `customFieldValue` (or vice versa) → throw `ArgumentException`:
  *"customFieldId and customFieldValue must be supplied together."*

The param descriptions must state that `limit`/`from`/`sortBy` apply to **unscoped** listing only.

### 3.2 `Get_custom_object_instance` (new)

```
Get_custom_object_instance(customObjectId, instanceId, fields?, traits?)
```
Vitally has no `GET …/instances/:id`, so this issues `GET …/instances/search?id={instanceId}`,
unwraps the first element of `results`, and returns it as a single filtered object.
On an empty result set (HTTP 200, `results: []`) it returns
`{"message":"No custom object instance found with id {instanceId}"}` rather than throwing.

### 3.3 `Search_custom_object_instances` (removed)

Tool method and its test removed. Its capabilities are fully covered by the scoped list (§3.1)
and get-by-id (§3.2).

## 4. Service changes (`VitallyService.cs`)

1. **`limit`-optional query building.** Factor the list query-string assembly so a variant can
   omit `limit`. The scoped-search path uses it to send only the criterion.
2. **Scoped-list path.** A method that issues the `/search` GET with the single criterion,
   applies `FilterJsonFields(isListResponse: true)`, and forwards `next` if present.
3. **Get-by-id path.** A method that issues `/search?id=…`, extracts the first `results`
   element, applies single-object filtering, and returns the not-found message when empty.
4. **Instance default fields.** Add `ResourceDefaultFields["customObjectInstances"]
   = [id, name, externalId, createdAt, updatedAt, organizationId, customerId, archivedAt]`.
   Decouple the **defaults key** from the **URL path** so the instance paths (whose URL is
   `customObjects/{id}/instances`, not an exact-match key) resolve to this set — e.g. the
   instance service methods pass an explicit `defaultsKey: "customObjectInstances"` into the
   filter, leaving `GetResourcesAsync`'s existing behaviour for other resources unchanged.

All new paths still funnel through `SendAsync`, preserving the RBAC backstop and audit logging
(GET → `vitally:read`).

## 5. Validation & error handling

- Scope-criteria validation (§3.1) happens **before** any HTTP call; messages are actionable so
  the LLM can self-correct.
- API errors continue to surface via `SendAsync`'s `HttpRequestException` with the Vitally body.
- Get-by-id "not found" is a normal 200 with empty `results` → returns the explicit message in
  §3.2 (not an exception).

## 6. Open / verification items (for the implementation plan)

- **`/search` envelope & pagination (must verify live).** The docs confirm `/search` is GET and
  takes one criterion, but do not confirm whether it paginates or honours `limit`/`from`. The
  plan must include a smoke-test against a live (or dev-key) Vitally instance to confirm the
  response is the `{results, next}` envelope and whether `from` is honoured. If `from` *is*
  honoured, wire `from` (and a client-cap `limit`) into the scoped path as a low-risk follow-up;
  if not, the scoped query returns the full matching set as Vitally provides it (acceptable —
  scoped sets are small, e.g. an org's handful of goals).

## 7. Testing

`VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs` and `VitallyServiceTests`:

- Scoped list (`organizationId`) issues `GET …/instances/search?organizationId=…` with **no**
  `limit` query param (Moq protected URI verification).
- `customFieldId`+`customFieldValue` issues the paired criterion correctly.
- Two scope criteria → `ArgumentException`, no HTTP call.
- `customFieldId` without `customFieldValue` → `ArgumentException`, no HTTP call.
- `Get_custom_object_instance` issues `…/instances/search?id=…`, unwraps the single object;
  empty `results` → the not-found message.
- Unscoped list unchanged (still sends `limit`).
- Instance default fields applied when `fields` omitted; trait subsetting applied when
  `traits` set with `traits` in `fields`.
- Remove the `Search_custom_object_instances` test.

Use `TestHelpers.BuildVitallyService(httpClient)` per existing convention.

## 8. Docs

- `CLAUDE.md`: update the custom-object-instance tool surface (removed `Search_…`, new
  `Get_custom_object_instance`, scoping params), and add a **Custom Object Instances** row to the
  default-fields table.

## 9. Acceptance criteria

- One call `List_custom_object_instances(customObjectId, organizationId=X)` returns only org X's
  instances.
- `Get_custom_object_instance` returns a single instance by id, or a clear not-found message.
- Instance lists return the new default field set and support `traits` subsetting.
- `Search_custom_object_instances` no longer appears in `tools/list`.
- Passing more than one scope criterion fails fast with an actionable message.
- All existing tests pass; new tests cover the above; `dotnet test` is green.
