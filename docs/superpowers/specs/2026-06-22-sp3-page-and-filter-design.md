# SP3 — Server-side "page-and-filter" helpers

**Date:** 2026-06-22
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback items:** P1.4 (org name search), P1.3 (date-range filters), P2.10 (custom-traits catalogue filter).

## 1. Purpose

Vitally's list endpoints accept only `limit`/`from`/`sortBy` — no server-side name or date
filtering — and the trait catalogue comes back as one ~120K-char array. This sub-project adds
those filters **on our side**: finding an organisation by name, scoping activity to a date window,
and filtering the trait catalogue — so MCP callers (notably the value report in Claude Desktop,
which has no local filesystem to sift with) get answers in one call instead of paging and sifting.

## 2. Verified constraints (live + docs)

- List endpoints: `limit`, `from`, `sortBy` (`createdAt`|`updatedAt`) only — **no** name/date filter.
- `customFields` (trait catalogue) returns a **bare array**, filtered client-side (it has no
  `{results}` envelope; see [[reference-vitally-api-constraints]]).
- Rate budget: 1000 req/min (the existing `VitallyRateLimitHandler` retries 429s). One MCP call
  must not be allowed to fan out unboundedly into page fetches.

## 3. Architecture — a bounded auto-pager

A single reusable pager in `VitallyService`, reused by the name-search and date-range tools:

- **Config:** `Vitally:MaxAutoPageFetches` (new `VitallyServerOptions` field, default **10**;
  validated `> 0`). 10 pages × 100/page ≈ 1000 items — a hard ceiling on fan-out per call.
- **Method (shape):** `GetFilteredAsync(resourceType, Func<JsonElement,bool> predicate, fields,
  sortBy, additionalParams, traits, defaultsKey, earlyStop?)`:
  1. Page via `from`/`next`, fetching raw `{results, next}` per page (a new internal raw-page
     fetch — distinct from `GetResourcesAsync`, which projects fields inline; the pager needs the
     unprojected items to evaluate the predicate).
  2. Apply `predicate` to each raw item; accumulate matches.
  3. Stop when: `next == null` (exhausted), OR `MaxAutoPageFetches` reached (**`truncated = true`**),
     OR the optional `earlyStop(item)` returns true (see date-range optimisation below).
  4. Field/trait-project the accumulated matches.
  5. Return `{ results: [...], truncated: bool, pagesFetched: int }`.
- Every page goes through `SendAsync`, so RBAC, audit and rate-limit handling are preserved.
- **Never silent:** when `truncated`, the tool result includes the flag plus a short
  "result capped at N pages — narrow your query (e.g. tighter date range / more specific name)".

## 4. The filters

### P2.10 — `List_custom_traits` gains `nameContains` (no paging)
The catalogue is a single bare array; add a case-insensitive substring filter on each trait's
`label` and `path`, applied client-side before returning. Separate cheap path — does **not** use
the pager.

### P1.4 — `List_organizations` gains `nameContains`
When supplied, route through the pager with a predicate matching `name` (case-insensitive
substring). ~230 orgs ≈ ≤3 pages — comfortably under the cap. Returns the matches (+ `truncated`
if ever capped).

### P1.3 — `createdAfter` / `createdBefore` (ISO-8601) on the activity list tools
Tools: `List_conversations`, `List_conversations_by_account`, `List_conversations_by_organization`,
`List_notes`, `List_tasks`, `List_meetings`. When either bound is supplied, route through the pager
with a `createdAt`-in-`[createdAfter, createdBefore]` predicate.
- **Cost optimisation (early-stop):** the pager fetches with `sortBy=createdAt` (descending, the
  API default direction). Once an item older than `createdAfter` is seen, all later items are
  older too, so paging stops early — bounding cost to the date window rather than the cap.
- Unparseable date → `ArgumentException` with an actionable message, before any HTTP call.

## 5. Non-goals (YAGNI)

- `updated*` date filters (only `created*` for now).
- Name search on accounts (organisations only — what the feedback asked for).
- Date-range on every list endpoint (only the activity set above).
- Combining multiple client-side filters in one call beyond what each tool exposes.

## 6. Output shape

Filtered list tools return `{ results, truncated, pagesFetched }`. Unfiltered calls (no
`nameContains`/`createdAfter`/`createdBefore`) keep today's exact behaviour and shape
(`{results, next}` via `GetResourcesAsync`) — the pager path is only taken when a client-side
filter is supplied.

## 7. Testing

- **Pager unit tests:** predicate filters correctly; cap hit → `truncated:true` + `pagesFetched`
  capped; exhaustion before cap → `truncated:false` with all matches; multi-page paging driven by
  the `from` cursor (Moq returns a different page per `from` value); early-stop halts once items
  fall before `createdAfter`.
- **Tool tests:** `nameContains` on organisations + custom traits; `createdAfter`/`createdBefore`
  on each activity tool; invalid date → `ArgumentException`, no HTTP call; unfiltered call still
  uses the plain `GetResourcesAsync` path (no extra page fetches).
- **Live check** (SP1 lesson): org name search returns the expected org; a date-bounded
  conversations-by-organization call returns only in-window items.

## 8. Docs

- `CLAUDE.md`: note the new filter params + the `truncated`/`pagesFetched` envelope and the
  `MaxAutoPageFetches` cap; add `Vitally:MaxAutoPageFetches` to the configuration section.

## 9. Acceptance criteria

- `List_organizations(nameContains: "Aberdeen")` returns matching orgs in one call.
- `List_conversations_by_organization(orgId, createdAfter, createdBefore)` returns only in-window
  conversations.
- `List_custom_traits(model, nameContains)` returns only matching trait definitions.
- A filter that would exceed the cap returns partial results with `truncated:true` and guidance —
  never a silent truncation.
- Unfiltered list calls are byte-for-byte unchanged; full suite green; behaviour confirmed live.
