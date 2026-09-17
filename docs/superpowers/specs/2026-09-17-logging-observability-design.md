# Logging and observability — design (supersedes the 2026-08-11 spec)

**Status:** proposed. **Supersedes** `2026-08-11-observability-design.md`, which is kept as a dated
artefact. That spec's shape was right in outline and wrong in two load-bearing ways, both found on
2026-09-17 once its own Phase 1 made the workspace readable for the first time:

- it treated the telemetry pipeline as *working but unreadable*. **Nothing from this server has ever
  reached Log Analytics.**
- it scoped the work as observability improvement. Read auditing was off, so **no access to customer
  data had ever been recorded** — a compliance gap, not an improvement.

It also planned around a query/ingestion trade-off that does not exist in the form assumed; see
*Pipeline* below.

## Purpose

Logging here serves three distinct consumers, and the current implementation serves the first
partially and the other two barely at all:

| Purpose | Question it answers | State |
|---|---|---|
| **Audit** | who accessed which customer record, and what did they do | partial |
| **System failures** | what is failing, how often, and why | weak |
| **Performance** | what is slow, and which part of it | absent |

## Verified current state (2026-09-17)

All of the following was measured against production, not inferred.

### Nothing from the application arrives

| Query against `vitally-prod-law-uksouth` | Result |
|---|---|
| `ContainerAppConsoleLogs_CL \| summarize count(), min(TimeGenerated), max(TimeGenerated)` | **0 rows, ever** — min/max `null` |
| `ContainerAppSystemLogs_CL` | 0 rows |
| anything with `_ResourceId has '/microsoft.app/'`, 30 days | no rows |
| `Usage \| where TimeGenerated > ago(30d) \| summarize by DataType` | only Key Vault and ACR types |

The workspace is **not** empty — it carries Key Vault `AuditEvent` (174 rows/7d) and ACR metrics and
events. Those arrive through **diagnostic settings on those resources**. The Container App has no
diagnostic setting; the CAE's `appLogsConfiguration` shared-key shipper is the only configured path
and has never delivered a row.

The wiring is not the fault: `appLogsConfiguration.destination = log-analytics` with `customerId`
matching this workspace exactly, the AMPLS private endpoint present, and all five Azure Monitor
private DNS zones linked to `vitally-prod-vnet-uksouth`. Enabling `publicNetworkAccessForIngestion`
did **not** restore shipping within 14 minutes of polling.

### The signal-to-noise ratio is inverted

202 lines sampled from the live console stream:

| Category | Lines |
|---|---|
| `Microsoft.AspNetCore.Hosting.Diagnostics` (request start/finish) | 42 |
| `JwtBearerHandler` + `DefaultAuthorizationService` | 18 |
| `System.Net.Http.HttpClient.*` (Graph, Vitally, upstream) | 20 |
| Routing, MCP server | 9 |
| **audit records** | **0** |

There is **no `appsettings.json`** in `VitallyMcp/` — only `appsettings.Example.json`, which is a
template and is not loaded. No `Logging` section exists anywhere and no `Logging__*` variable is set
on the Container App, so every category runs at the framework default of `Information`.

### The whole logging surface is seven call sites

`AuditLogger` ×3, `GraphGroupPermissionResolver` ×2, `UpstreamOidcMetadata` ×1,
`VitallyRateLimitHandler` ×1, `VitallyApiKeyProvider` ×1 (Debug). Plus exactly **one `LogError` in
the entire application** (`ToolAuthorizer.cs`). No `Stopwatch`, `Activity`, `Meter` or counter
anywhere.

## Constraint: no customer PII in telemetry

Carried forward from the superseded spec and reaffirmed. `AuditLogger` honours it deliberately —
object id not email, resource path with the **query string stripped**, never request or response
bodies.

⚠️ **One path does not honour it.** `System.Net.Http.HttpClient.*` logs outbound request URIs
*including query strings*, at `Information`, for every Vitally and Graph call. In the sample taken,
the parameters were benign (`$select`, `limit`, `fields`). But `Search_users`, `Search_admins` and the
`nameContains` filters put **search terms** in the query string — potentially names or email
addresses. This is a latent leak of exactly the data `AuditLogger` strips, through a category nobody
configured. Not observed firing; structurally present. Closing it is part of this work.

## Design — code (what is emitted)

### Audit: two tiers, correlated

Both tiers are kept. They answer different questions and neither substitutes for the other.

| Tier | Records | Answers |
|---|---|---|
| **Tool call** (new) | caller, tool name, outcome, duration, correlation id | who used which capability, and did it work |
| **Upstream call** (exists, extend) | caller, verb, resource path, status, duration, correlation id | **which customer record was touched** |

**The upstream tier is the compliance-critical one, and that is counter-intuitive.** Tool arguments
are deliberately never logged, so a tool-call record says *alice ran `Get_organization_summary`* but
not **which** organisation. Only the upstream path — `GET /resources/organizations/{id}` — names the
record accessed. "Did anyone access this customer's data" is answerable from the upstream tier alone.

So the tiers must not be traded against each other for volume. Where cost forces a choice, it is a
**retention** decision made per tier on evidence, and the upstream tier is not the cheap one to drop.

**Correlation id is the missing piece, not either record.** `Get_organization_summary` makes four
upstream calls and the bounded auto-pager can make ten; today those are orphans. One id per tool call,
carried onto every upstream record it causes, is what turns two streams into a trail.

Gaps closed by this tier design:

- **tool name on success** — currently captured only on denial
- **no sign-in record** — who authenticated and when is not captured at all; add one
- **no result magnitude** — reading 1 record and 250 are indistinguishable; record the count

### System failures

- **The CallTool filter swallows errors.** `Program.cs` catches every surfaceable exception and
  returns it to the client without logging. Vitally 500s, validation failures and RBAC denials leave
  no server-side trace. Log at `Error` (or `Warning` for expected denials) before returning.
- **`VitallyService.SendAsync`** throws on non-2xx with the response body; surfaced to the client,
  never logged. Log the status and resource — **never the body**, which can carry customer PII.
- **Rate-limit exhaustion** — only the "nearing" threshold warns today; retries-exhausted is silent.
- **Key Vault fetch failure** throws unlogged.

### Performance

Nothing exists. Add, in order of value:

1. **Duration on both audit tiers** — near-free, and immediately answers "which tool is slow".
2. **Counters** for 429 retries, page-cap truncations (`truncated: true`), and the three cache
   hit/miss rates (API key, group membership, OIDC discovery).
3. **Tracing** — retain the superseded spec's reasoning: metrics can say `Get_organization_summary`
   has a slow p95 but not which of its four upstream calls dominated.

Prefer `System.Diagnostics.Metrics` / OpenTelemetry over ad-hoc logging so these are dimensions, not
lines to grep.

Note for whoever picks this up: the earlier "slow requests" investigation concluded model inference
rather than the server. That was reasoned, not measured — because there is nothing to measure with.
This work makes that conclusion checkable.

### Logging configuration

Add an `appsettings.json` with an explicit `Logging` section. It does three jobs at once:

- cuts the framework noise that is ~90% of volume, which is what makes retaining the audit tiers
  affordable
- **constrains `System.Net.Http.HttpClient.*`**, closing the query-string PII exposure above
- makes levels reviewable in the repo rather than implicit in framework defaults

Give audit records a stable category or event name so they can be **routed**, not pattern-matched.
`ILogger<AuditLogger>` already yields `VitallyMcp.AuditLogger`, which is a usable discriminator.

## Design — pipeline (where it goes)

### Use a diagnostic setting, not the shared-key shipper

The CAE exposes diagnostic-setting categories — `ContainerAppConsoleLogs`, `ContainerAppSystemLogs`,
`ContainerAppHTTPLogs`, `AllMetrics` — and **none is configured**.

Microsoft's private-link documentation is explicit:

> **Diagnostic logs.** Logs and metrics sent to a workspace from a diagnostic setting use a secure
> private Microsoft channel and aren't controlled by these settings.

That is exactly why Key Vault and ACR data arrives while the CAE's shipper does not, and it means a
diagnostic setting **works with public ingestion disabled**. The same page notes Log Analytics
ingestion uses resource-specific endpoints and so does not adhere to AMPLS access modes either — so
the AMPLS was never the gate.

Consequences, in order:

1. Add a diagnostic setting on the CAE for `ContainerAppConsoleLogs`, exported **resource-specific**
   (a real table, not `_CL`, which is also what makes per-table retention possible).
2. Verify records arrive.
3. **Re-lock `publicNetworkAccessForIngestion`** to `Disabled`, restoring the hardening opened on
   2026-09-17 while this was being diagnosed.
4. Evaluate `ContainerAppHTTPLogs` separately — it carries request URLs and needs the same PII
   scrutiny as the `HttpClient` category above.

### Query access

`queryAccessMode: Open` and `publicNetworkAccessForQuery: Enabled` were set on 2026-09-17 and remain
the right call for now: query is not an anonymous surface (Entra auth plus workspace RBAC), and
Azure Monitor supports no IP allowlist. The superseded spec's preferred end state — peering to the
FISCAL hub so query returns to private — still stands.

⚠️ Correcting that spec: peering would fix **query** only. It would not have fixed ingestion, because
the CAE's shipper never ran inside this VNet. The diagnostic setting is the ingestion answer.

### Retention

Deferred until volume is measurable, which needs the above. Decide per tier, on evidence, against the
stated one-year requirement — currently 30 days workspace-wide. Do not set retention before the audit
tiers and the noise reduction land, or the number will be measured against the wrong traffic.

## Phasing

| | Work | Depends on |
|---|---|---|
| 0 | query path open | **done** 2026-09-17 |
| 1 | audit reads by default | **done** — #139 / PR #140 |
| 2 | diagnostic setting; verify arrival; re-lock ingestion | — |
| 3 | logging configuration: noise + `HttpClient` PII | — |
| 4 | audit tiers: tool-call record, correlation id, sign-in, result count | 3 |
| 5 | failure logging | 3 |
| 6 | performance: durations, counters, tracing | 3 |
| 7 | routing and retention per tier | 2, 4, measured volume |

2 and 3 are independent and both unblock the rest. 3 is worth doing before 4–6 so new records are not
added to an unfiltered stream.

## Risks

| Risk | Mitigation |
|---|---|
| Volume and cost rise once records actually flow, with reads now on | 3 removes ~90% noise first; retention decided per tier on measured volume, not guessed |
| PII reaching telemetry through a framework category nobody configured | 3 constrains `HttpClient`; `ContainerAppHTTPLogs` evaluated separately before enabling |
| Re-locking ingestion breaks delivery again | verify arrival at step 2 *before* re-locking, and re-verify after |
| Correlation id becomes a per-call-site convention that drifts | carry it through the existing `CallerIdentity`/`AuditLogger` choke points, which already exist for exactly this reason |

## Open questions

- **Destination for the audit tiers.** Console logs via diagnostic setting, or App Insights custom
  events from inside the app (which ingests over the private endpoint, since the app's own traffic
  *is* in the VNet)? The latter separates audit from diagnostics natively and gives #93 its dedicated
  table without parsing — but nothing sends to App Insights today, so it is new wiring.
- **Sign-in records** — the server sees tokens, not sign-ins. Entra sign-in logs may be the better
  source; check before building one.
- **Result magnitude** — a count is cheap and useful; confirm it cannot become a data-volume oracle
  over customer records in a way that matters.
