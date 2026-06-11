# Vitally MCP — user-feedback backlog: decomposition & sequencing

**Date:** 2026-06-10
**Status:** Portfolio spec (approved). Each sub-project gets its own detailed spec → plan → implementation cycle.
**Source:** First-hand feedback from three value-report runs (Lesley in Claude Desktop on Specsavers ×2; a Claude Code run on Aberdeen City Council).

## 1. Context

We are productising a customer value report as a Claude skill running on the Vitally + Rosetta
MCPs. Live runs surfaced ~13 improvement requests. The data layer is sound; the issues are
**scoping, filtering and tool-surface** — they are what burned the MCP tool budget and produced
the slow runs. In Claude Code the report author could brute-force the missing filters with a
local file parse; in Claude Desktop there is no local filesystem, so the only way to filter is
**more MCP calls** — which is the tool-limit burn we watched.

This document does **not** design the fixes. It records what each request actually means once
checked against the Vitally REST API, groups the work into independently shippable
sub-projects, and fixes the order we will tackle them. The sequence is **value-first**
(approved): SP1 → SP2 → SP4 → SP3 → SP5.

## 2. Feasibility reality-check (Vitally REST API)

Verified against the Vitally Help Center REST API reference (June 2026):

| # | Request | What Vitally actually allows | Verdict |
|---|---------|------------------------------|---------|
| P1.1 | `organizationId` on instances list | Instances **`/search`** endpoint (GET) accepts `organizationId` or `customerId`, but **exactly one** criterion; it is a *different* endpoint from the plain list | Feasible via `/search` |
| P1.6 | `Search_custom_object_instances` broken | `/search` exists (GET, one-criterion). Our tool sends `limit=100` **plus** the criteria, likely violating the one-criterion rule and/or assuming the wrong envelope | Diagnose + fix or remove |
| P2.7 | Get single instance | **No** `GET …/instances/:id` endpoint exists; `PUT`/`DELETE` use that path but there is no read. `/search?id=…` returns the one record | Feasible *via search-by-id* |
| P1.2 | Conversations subject + source | List payload **already includes `subject`** (and so do our defaults). `source` (e.g. `google`, `intercom`) and `status` are real conversation fields but are **not** in our default set | Small — add fields; reconcile the "timestamps only" report against the deployed build |
| P2.9 | Trait subset on instance lists | Instances carry `traits`; our client-side filter already supports subsetting — it just isn't exposed on the instance tools. Instance lists also fall back to the bare `id,createdAt,updatedAt` default | Small |
| P2.10 | Filter `List_custom_traits` | `customFields` returns a bare array; any filter is client-side only | Small (client-side) |
| P1.3 | Date-range filters | Vitally list endpoints accept **only** `limit`, `from`, `sortBy` — **no** server-side date filter | Only via our-server paging+filtering |
| P1.4 | Org name search | **No** name search on Vitally; get-by-id does accept `externalId` | Only via our-server paging+filtering (or externalId) |
| P1.5 | Read-only / role-scoped | Server-side RBAC is **already built** (`ToolAuthorizer`, enforced in `VitallyService.SendAsync`). Gap is rollout/config + the fact destructive tools still appear in `tools/list` (the `Destructive` flag is only an advisory client hint) | Mostly config/ops + a tool-surface design choice |
| P2.12 | Server instructions / hints | MCP supports a server `instructions` field; tool descriptions are editable | High-leverage, low-risk |
| P2.13 | Composite `Get_organization_summary` | Orchestrate existing calls server-side | Larger feature; depends on SP1 |
| P2.8 | `totalCount` in envelopes | Vitally returns **no** total/count field anywhere | Infeasible without paging everything |
| P2.11 | Timeouts on ID query params | Vitally-side performance (likely the `/search` path) | Not ours — escalate; mitigate with timeout/retry |

Two structural observations drove the grouping:

- **P1.1, P1.6 and P2.7 all collapse onto the single `/search` endpoint** — so they are one
  cohesive piece of work, not three.
- **P1.3, P1.4 and P2.10 all need the same mechanism** — "our server pages Vitally and filters
  the results, because Vitally won't" — with the same cost/cap/timeout trade-offs.

## 3. Sub-projects

### SP1 — Custom-object-instance scoping & ergonomics *(value-report unblocker)*
**Items:** P1.1, P1.6, P2.7, P2.9.
**Files:** `Tools/CustomObjectsTools.cs`, `VitallyService.cs`.
**Shape:** scope instance listing by `organizationId`/`customerId` by routing to `/search`;
diagnose and fix (or remove) `Search_custom_object_instances`; add
`Get_custom_object_instance` implemented as search-by-id; expose `traits` and give instance
lists a real default field set (e.g. `id,name,externalId,createdAt,updatedAt,organizationId,
customerId,archivedAt`).
**Open questions for its spec:**
- The `/search` envelope and whether it accepts `limit`/`from`/`sortBy` (drives whether scoped
  lists can paginate). Needs live verification.
- Instance default fields use a dynamic resource path (`customObjects/{id}/instances`), so the
  exact-match `ResourceDefaultFields` lookup misses them — need a path-suffix rule or explicit
  defaults.
- Fix vs remove for the broken search tool (removal may be cleaner once scoped-list +
  get-by-id cover its use cases).

### SP2 — Conversations for support-ticket reporting
**Items:** P1.2.
**Files:** `VitallyService.cs` (`ResourceDefaultFields["conversations"]`), possibly
`Tools/ConversationsTools.cs` descriptions.
**Shape:** add `source` and `status` to the conversation default fields so genuine support
tickets are distinguishable from calendar invites/emails.
**Open questions for its spec:**
- Confirm the *list* endpoint actually returns `source`/`status` (the documented list example
  showed only `id,externalId,externalUrl,subject,traits`); if absent on list, document the
  limitation rather than implying a per-record fetch is avoidable.
- Reconcile the "list returns timestamps only" report — current defaults contain `subject`, not
  timestamps, so this is likely a stale deployed build; verify before assuming a code change is
  even needed.

### SP3 — Server-side "page-and-filter" helpers
**Items:** P1.4, P1.3, P2.10.
**Files:** `VitallyService.cs` (new bounded auto-paging helper), the relevant list tools,
`Tools/CustomTraitsTools.cs`.
**Shape:** a single bounded helper that pages an endpoint up to a cap, applies a client-side
predicate (name-contains, created/updated within range, trait-label-contains), and returns the
matches plus a truncation flag. Org name search, date-range filtering and the custom-traits
catalogue filter all reuse it.
**Open questions for its spec (the real decision):**
- Is auto-paging acceptable at all given the 1000 req/min budget, or do we cap hard and surface
  "truncated — narrow your query"? What page-count / item-count / wall-clock caps?
- Which list tools get date-range filtering?
- For org name search, prefer exact `externalId` lookup where the caller has it.

### SP4 — Safety / least-privilege rollout
**Items:** P1.5 (and the "remove" half of P1.6 if SP1 chooses removal).
**Files:** mostly **outside this repo** — Auth0 Action, Entra group assignments, Terraform/IaC;
within the repo, an optional deploy-time "read-only mode".
**Shape:** verify the RBAC backstop is actually enforced in the deployed revision
(`Authorization:Enabled`, `LiveGroupCheck`), assign the reader/editor/admin Entra groups so CS
users genuinely lack `vitally:delete`, and decide whether to also **hide** destructive tools
(stateless HTTP + a static `tools/list` makes per-caller filtering non-trivial; a deploy-time
read-only build that simply doesn't register destructive tools may be the pragmatic answer).
**Open questions for its spec:**
- Why are deletions happening now — enforcement off, or groups unassigned so everyone has
  write/delete? Establish the actual current state first.
- Hide-tools vs deny-on-call vs client-side hints — pick the model.
- Relationship to the pending data-classification gate before wider rollout.

### SP5 — Guidance & composites
**Items:** P2.12, P2.13.
**Files:** `Program.cs` (server `instructions`), tool descriptions, a new composite tool.
**Shape:** ship server-level `instructions` and a few targeted tool-description hints ("rich
data lives at organisation level; account traits are usually empty; customer goals = the
`customerGoals` custom object; traits ≠ custom objects"); add a composite
`Get_organization_summary(orgId)` that returns organisation + traits + goals + product feedback
+ ticket rollups in one call.
**Open questions for its spec:**
- SDK support for setting `instructions` via `AddMcpServer`.
- `Get_organization_summary` depends on SP1's org-scoped instance fetch; define the exact
  sub-calls, partial-failure handling and response size budget.

## 4. Won't-fix / escalate (not a sub-project)

- **P2.8 `totalCount`** — Vitally exposes no count. Default: won't-fix. Optional later: an
  explicit, clearly-expensive `Count_*` tool that pages to exhaustion only when asked.
- **P2.11 timeouts** — Vitally-side performance; raise with Vitally. We can tune the
  `HttpClient` timeout and the existing `VitallyRateLimitHandler` retry behaviour as mitigation.

## 5. Sequence (approved: value-first)

1. **SP1** — instances (unblocks the value report).
2. **SP2** — conversations (tiny, high value, can trail or parallel SP1).
3. **SP4** — safety (urgent per the feedback; flagged as a judgement call — revisit if the
   rollout timeline tightens).
4. **SP3** — page-and-filter (needs the auto-paging decision).
5. **SP5** — guidance & composite (polish + leverage; P2.13 depends on SP1).

## 6. Credit (from the feedback, retained for context)

Cursors are reliable; the `fields` selector is excellent and is what made the light-scan
workaround possible; the data is accurate (rollup counts matched the instances exactly). The
foundations are right — the asks above are mostly scoping and filtering.
