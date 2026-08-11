# Observability for the Vitally MCP server — design

**Date:** 2026-08-11
**Status:** Approved

## Problem

The server needs telemetry good enough to answer three questions: **who touched customer data**,
**which tools are actually used**, and **is it running well**. Today it can answer none of them
reliably — but not for the reason one would assume.

The application code is the healthy part. `AuditLogger` already emits structured, attributable,
deliberately PII-free records at the two choke points that matter (`VitallyService.SendAsync` for
actions and denials, `VitallyPermissionHandler` for tier-mismatch refusals), and MCP SDK 2.1.0 now
ships OpenTelemetry instrumentation that yields per-tool usage counts and latency histograms for
free.

The gaps are in the telemetry **platform** and in **coverage**:

1. **Nothing can be queried.** Log Analytics, App Insights and the AMPLS scope are all locked to
   private-only *query*, so there is no read path from an operator's laptop — no portal KQL, no
   Workbooks, and probably no scheduled log-search alerts.
2. **The audit trail expires after 30 days**, against a stated requirement of one year.
3. **Audit and operational records share one table** (`AppTraces`), and Log Analytics retention is
   per-table, so they cannot be retained differently as things stand.
4. **Most usage is invisible by design.** `Audit:IncludeReads` defaults to `false` because reads are
   high-volume, so the read tools — the majority of the 93 — leave no usage record.
5. **No timings anywhere.** `AuditLogger` records verb, resource and status, but no duration.

## Verified current state (2026-08-11)

Established with `az` as `dsearle.adm` against subscription `IT-Production`
(`282207c6-4107-47fa-9d4e-b2fa9b3066cb`), resource group `vitally-prod-rg-uksouth`:

| Property | Value |
|---|---|
| Log Analytics `vitally-prod-law-uksouth` — ingestion / query | `Disabled` / `Disabled` |
| Log Analytics retention | **30 days** |
| App Insights `vitally-prod-appi-uksouth` — ingestion / query | `Disabled` / `Disabled` |
| App Insights retention | 90 listed; workspace-based, so the workspace's 30 days governs |
| AMPLS `vitally-prod-ampls-uksouth` | `ingestionAccessMode: PrivateOnly`, `queryAccessMode: PrivateOnly` |
| VNet `vitally-prod-vnet-uksouth` | no peerings; subnets `snet-pe`, `snet-app`, `snet-pe-monitor`; NAT gateway egress only |

SDK instrumentation confirmed by inspecting `ModelContextProtocol.Core` 2.1.0:

- ActivitySource **`ModelContextProtocol`**; Meter **`Experimental.ModelContextProtocol`**
- Metrics: `mcp.server.operation.duration`, `mcp.server.session.duration` (and client equivalents)
- Tags: `mcp.method.name`, `gen_ai.tool.name`, `rpc.response.status_code`,
  `mcp.protocol.version`, `mcp.session.id`, `mcp.resource.uri`

## Goals

In priority order, as agreed:

1. **Prove who touched customer data** — attributable, durable, queryable per user. One-year retention.
2. **Understand adoption** — which tools are used, by whom, how often; and critically which are
   never used. With 93 tools and a known concern that a large `tools/list` costs model context,
   evidence of dead tools is what justifies pruning.
3. **Run it reliably** — per-tool latency, error rates, Vitally 429 pressure against the documented
   1000 req/min budget, and page-cap truncations from the bounded auto-pager.

Explicitly *not* a design pillar: reconstructing an individual user's request on demand. Traces are
enabled at low sampling as a near-free by-product (see Phase 3), but nothing is built around them.

### Decomposition

The three phases are sequentially dependent but independently shippable, and they span different
change-control regimes — Phase 1 is two Azure settings, Phase 2 is application code plus new Azure
resources, Phase 3 is application code plus dashboards. **Each phase should get its own
implementation plan** rather than one plan spanning all three; Phase 1 in particular is small enough
to execute directly from this design.

## Overriding constraint: no customer PII in telemetry

`CLAUDE.md` and FISCAL's data-handling policy forbid personal data in telemetry. `AuditLogger`
honours this deliberately: it records the opaque `sub` (an Entra object id, attributable via Entra
but not itself PII) rather than the email, and `ResourcePath()` strips the query string so filter
values never land in logs.

**OpenTelemetry does not honour this by default, and that is the single most important risk in this
design.** The HttpClient instrumentation records `url.full` *including the query string*. The
`Search_users` tool passes its search term as `?query=<value>`, and its own description is "Search
Vitally users by email or externalId" — so that value is routinely a **customer email**. Wiring OTel
naively would write customer emails into telemetry.

Therefore: **redaction is a gate on enabling OpenTelemetry at all, not an enhancement to it.** OTel
is wired in Phase 3, so the redaction work sits there — but within that phase it must land in the
same change as the wiring, never after it. It is required the moment HTTP instrumentation is enabled
for Vitally latency visibility, whether or not tracing is switched on.

## Phase 1 — restore the query path

**Decouple query from ingestion.** Set AMPLS `queryAccessMode: Open`, and
`publicNetworkAccessForQuery: Enabled` on both the workspace and the App Insights component. Leave
`ingestionAccessMode: PrivateOnly` and both ingestion settings `Disabled` exactly as they are.

This must come first because everything else depends on it, including *measuring* current ingest
volume — which is the input to any cost-aware retention decision.

### Why this is defensible security-wise

It partially reverses a Defender-for-Cloud hardening item, so the justification is recorded here.
The two directions carry different risk:

- **Ingestion** public would let anything on the internet push telemetry into the workspace or reach
  the ingestion endpoint from outside the network. That is the exfiltration- and spoofing-relevant
  direction, and it stays locked.
- **Query** is not an anonymous surface. It requires Entra authentication plus workspace RBAC and
  inherits tenant Conditional Access. Holding it `PrivateOnly` does not protect against an
  unauthenticated attacker; it blocks the organisation's own authorised operators.

Log Analytics and App Insights **do not support IP-based firewall rules** — unlike Key Vault,
Storage or SQL there is no `networkAcls` allowlist for query. The only resource-level controls are
the public-access toggle and AMPLS. So "restrict to the office range" cannot be expressed at the
resource; it must be expressed at the identity layer.

### Companion control: considered and declined

A **Conditional Access named-location policy** scoped to the Azure Management API was considered —
restricting query to office and VPN ranges, and optionally requiring a compliant device. It was
**declined (dsearle, 2026-08-11)** in favour of relying on Entra authentication plus workspace RBAC
alone.

Recorded here so a future reader does not mistake the absence of a location restriction for an
oversight. The reasoning: query is not an anonymous surface, RBAC is the substantive control, and a
tenant-level Conditional Access policy carries blast radius well beyond this project. If a location
restriction is later required, the named-location policy is the mechanism — not an IP allowlist,
which Azure Monitor does not support.

### Preferred long-term hardening

FISCAL operates a connectivity layer with a VNG/VPN setup. **Peering `vitally-prod-vnet-uksouth` to
that hub would allow query to stay `PrivateOnly`**, reached over existing corporate connectivity —
strictly better than opening public query. It is not proposed as part of Phase 1 because it depends
on another team's network and would block the immediate need, but it is the right end state and
Phase 1 should be treated as reversible in its favour.

### Verification

- Portal KQL and `az monitor log-analytics query` succeed from an operator workstation.
- **Confirm a scheduled log-search alert rule actually evaluates.** Alert evaluation under a
  private-only scope is a known limitation; if rules still fail after this change, Phase 3 alerting
  needs a different mechanism and its shape changes.
- Ingestion remains private: telemetry from the Container App still arrives, and an ingestion
  attempt from outside the VNet still fails.

## Phase 2 — route the audit trail to its own table, then set retention

In this order deliberately: routing first, retention second. The alternative — raising retention on
the shared `AppTraces` table — would buy compliance immediately but pay a year of storage on
operational noise, and that noise is about to grow when read coverage improves.

### Mechanism

A dedicated audit sink writing to a custom table (`VitallyAudit_CL`) via the Azure Monitor **Logs
Ingestion API**, authenticated with the existing user-assigned managed identity. `AuditLogger` gains
an `IAuditSink` seam so the destination is swappable and unit-testable; the default implementation
continues to write through `ILogger`, with the custom-table sink enabled by configuration.

The alternative considered was a **workspace transformation DCR** splitting matching `AppTraces` rows
into a custom table with no application change. Attractive, but transformations are easy to get
subtly wrong and the exact routing capability must be confirmed against current Azure documentation
before relying on it. **Decided 2026-08-11: the app-side sink**, for predictability and testability. **If the
transformation approach is confirmed workable during implementation it may be substituted, provided
the resulting table and schema are identical.**

Infrastructure required: a Data Collection Endpoint, a Data Collection Rule, the custom table, and
`Monitoring Metrics Publisher` on the DCR for the managed identity.

### Retention

- `VitallyAudit_CL`: **365 days** interactive retention (per-table).
- Operational tables including `AppTraces`: left at the workspace default of 30 days, or raised to
  90 if Phase 1 measurement shows the cost is trivial.

### Schema

Fields only, no free-text blobs: timestamp, `AuditUserId` (the opaque `sub`), event type
(`action` / `denied` / `tool_call_denied`), MCP tool name, HTTP verb, Vitally resource **path**
(query string stripped), status code, required permission, and a correlation id. **No email, no
request or response body, no tool arguments, no query strings.**

Two further fields, added on review:

- **`EffectivePermissionTier`** — the tier the caller actually resolved to at the moment of the call.
  This is the more important of the two. Because `LiveGroupCheck` resolves entitlement from *live*
  Entra group membership, entitlement at a past moment **cannot be reconstructed afterwards** — once
  someone leaves a group, the audit trail can no longer answer "was this person entitled to do that
  at the time?" That is a real hole in an access record, and capturing the resolved tier alongside
  the required permission closes it.
- **`McpClient`** — which client made the call. Useful for adoption analysis and for scoping the
  blast radius when one client misbehaves. Note the mechanism: in stateless mode there is no
  `initialize` handshake to read `clientInfo` from, because the 2026-07-28 revision removed it —
  client identity arrives in per-request `_meta` instead, so it is read per call rather than per
  session.

**Duration was considered and deliberately excluded.** The OTel histogram already carries latency at
better fidelity, and performance data has no compliance value — putting it in a table retained for
365 days means paying a year of storage for something useful for a week. The audit table stays lean;
performance lives in metrics.

## Phase 3 — coverage, dashboards, alerts

### Wiring

Register OpenTelemetry, subscribing to the SDK's `ModelContextProtocol` ActivitySource and
`Experimental.ModelContextProtocol` meter, plus HttpClient and ASP.NET Core instrumentation, exported
to the existing App Insights component.

**With redaction applied at registration**, as a hard requirement: `url.full` and any URL-bearing
attribute reduced to its path, matching `AuditLogger.ResourcePath()`. Enforced by a test that
asserts a known-sensitive value (an email passed to `Search_users`) never appears in any emitted
telemetry attribute. This test is the gate on the whole phase.

### Coverage gaps to close

- **Tool name on success.** `LogAction` records the Vitally verb and path but not the MCP tool that
  caused it, so usage-by-tool is currently unanswerable from the audit trail. The SDK metric carries
  `gen_ai.tool.name`, which covers adoption; the audit record should carry it too so a compliance
  query can say *which tool* touched a record.
- **Read visibility.** Emit one audit record per *tool call* regardless of verb, while leaving
  per-upstream-call records governed by `IncludeReads`. This decouples "was this tool used" from
  "how many HTTP calls did it make", which is what makes reads affordable to record.
- **Rate-limit pressure.** `VitallyRateLimitHandler` already logs a warning below the remaining
  threshold; add a counter for 429 retries so pressure against the 1000 req/min budget is a metric,
  not a log line to grep.
- **Page-cap truncations.** The bounded auto-pager returns `truncated: true` when it hits
  `MaxAutoPageFetches`; emit a counter so silently narrowed results are visible.

### Tracing

Enabled at **10% head sampling, with errors sampled at 100%**. Justification: metrics can report that
`Get_organization_summary` has a slow p95 but cannot say *which* of its four upstream calls
dominated, and the auto-pager can make up to ten. Traces decompose fan-out latency, which aggregates
structurally cannot. Since redaction is mandatory regardless, the marginal cost is ingest on sampled
spans only.

### Dashboards

One Workbook, three sections:

- **Adoption** — calls by `gen_ai.tool.name`, distinct users per tool, and the **zero-use list**
  (tools with no calls in the period), which is the actionable output for pruning the tool surface.
- **Reliability** — latency percentiles per tool from `mcp.server.operation.duration`, error rate by
  `rpc.response.status_code`, Vitally 429 counts, truncation counts.
- **Audit** — actions and denials per user over time.

### Alerts

Seven, all agreed on review. Kept deliberately few so each one means something when it fires.

1. **Audit ingestion stops.** A silent audit trail is worse than a noisy one — you discover the gap
   only when you need the evidence. Catches the telemetry itself failing.
2. **Microsoft Graph group-lookup failure.** This is a *security* alert, not an operational one.
   When `LiveGroupCheck` is on and the Graph lookup fails, `HasEffectivePermissionAsync` silently
   falls back to the token claim. That is deliberate fail-degraded design, but it means permissions
   are resolving from a **frozen claim** rather than live membership — so a revocation just made
   would not take effect. The degradation is currently invisible.
3. **Spike in 401s on `/mcp`.** The canary for the auth-discovery rework: if it misbehaves against a
   real client the signature is a cliff of 401s, and the action is to roll back. Most valuable in
   the weeks following that change.
4. **Key Vault secret fetch failure.** If the managed identity cannot retrieve `vitally-shared`,
   every tool call fails — total outage, currently only a log line. Distinct from the existing
   near-expiry alerting, which catches a different problem.
5. **Vitally 429s appearing at all.** The budget is 1000 req/min; hitting it means something is
   looping or a page-cap sweep has gone wrong. Any occurrence is worth knowing, so no threshold.
6. **Denial rate for a single user** exceeding a threshold in a short window — misconfigured access
   or probing. Needs threshold tuning once normal volumes are visible.
7. **Tool-call error rate** above a threshold. The most likely of the seven to be noisy before
   baseline data exists, so tune it after Phase 1 measurement rather than guessing now.

Page-cap truncations are deliberately **not** alerted — real, but not actionable at 3am. Dashboard
only.

## Not covered

- Per-user request reconstruction as a designed capability (traces exist but nothing depends on them).
- Vitally's own audit log. Because all users share one API key, Vitally cannot attribute actions to
  individual FISCAL users; this server's audit trail is the only attribution mechanism, which is
  precisely why its retention matters. Per-user keys via the `secret_ref` claim remain a future
  option that would change this.
- Peering to the connectivity hub (recorded above as preferred long-term hardening, out of scope here).
- The Conditional Access policy (recommended, actioned separately as a tenant-level change).

## Risks

| Risk | Mitigation |
|---|---|
| **Customer emails leaking into telemetry via `url.full`** | Redaction mandatory at OTel registration; gated by a test asserting a known email never appears in emitted attributes |
| Opening public query reverses a hardening item | Ingestion stays private; justification recorded above; Conditional Access recommended; reversible in favour of hub peering |
| Scheduled alert rules may not evaluate under the current scope | Explicitly verified in Phase 1; if broken, Phase 3 alerting is redesigned |
| Retention cost unknown | Phase 1 enables measurement before any retention decision is taken |
| Logs Ingestion API adds a failure mode to the audit path | Sink failures must never fail a tool call; audit write errors are logged through `ILogger` as a fallback so records are degraded, never silently dropped |
| Read coverage increases ingest volume | One record per tool call rather than per upstream call; `IncludeReads` retained for the finer granularity |

## Open inputs

- ~~Whether the Conditional Access companion policy is adopted~~ — **resolved 2026-08-11:
  declined.** Entra authentication plus workspace RBAC is the accepted control; see Phase 1.
- **Whether the workspace transformation DCR is viable** as a substitute for the app-side sink;
  to be confirmed against current documentation during Phase 2 implementation.
- Ingest volume, and therefore the retention cost for operational tables. Answerable only after
  Phase 1.
