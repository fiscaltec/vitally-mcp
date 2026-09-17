# Logging and observability — design (supersedes the 2026-08-11 spec)

**Status:** proposed. **Supersedes** `2026-08-11-observability-design.md`, which is kept as a dated
artefact. That spec's shape was right in outline and wrong in two load-bearing ways, both found on
2026-09-17 once its own Phase 1 made the workspace readable for the first time:

- it treated the telemetry pipeline as *working but unreadable*. **Nothing from this server has ever
  reached Log Analytics.**
- it scoped the work as observability improvement. It is a compliance gap, and precisely: **reads
  were never emitted** (`IncludeReads` was `false` on every target, #139), while **mutations and
  denials were emitted and then never ingested**. Two independent failures with the same effect —
  no answer to "who accessed which customer record" — but different causes and different fixes.

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

**No logging configuration reaches the running app.** `appsettings.Example.json` *does* carry a
`Logging` section (`Default: Information`, `Microsoft.AspNetCore: Warning`) — but it is a template
that ASP.NET Core never loads, there is no `appsettings.json`, and no `Logging__*` variable is set on
the Container App. So every category runs at the framework default of `Information`, and the sample
above is what that produces. The Example file being *almost* right is the trap: it reads like
configured behaviour and is inert.

### The whole logging surface is 14 call sites, and 11 of them are warnings

| File | Sites |
|---|---|
| `AuditLogger` | 3 (1 Information, 2 Warning) |
| `ToolAuthorizer` | 3 (2 Warning, **1 Error**) |
| `VitallyRateLimitHandler` | 3 (Warning) |
| `GraphGroupPermissionResolver` | 2 (Warning) |
| `UpstreamOidcMetadata` | 1 (Warning) |
| `VitallyApiKeyProvider` | 1 (Debug) |
| `Program.cs` | 1 (Warning) |

By level: **11 `LogWarning`, 1 `LogInformation`, 1 `LogError`, 1 `LogDebug`.**

⚠️ **Two earlier drafts of this section said "seven" and then "eight", and both were wrong** — the
survey behind them used a regex requiring `_logger.`, which cannot match `_logger?.LogWarning`. The
null-conditional call sites were invisible to it, which is most of `ToolAuthorizer` and
`VitallyRateLimitHandler`, plus `Program.cs`'s `app.Logger`. Recorded because the same mistake
silently under-reports any future audit of this surface: match `[A-Za-z_]+\??\.Log[A-Z]`.

The shape of the finding survives the correction and is arguably starker: **one Error-level call site
in fourteen**, on a service whose failures are otherwise handed to the client and forgotten.

No `Stopwatch`, `Activity`, `ActivitySource`, `Meter`, counter or histogram anywhere — verified with
a pattern wide enough to catch all of them.

## Policy: the audit trail may contain customer personal data — reversed 2026-09-17

**The previous "no customer PII in telemetry" rule is withdrawn** (decision: @searledan, 2026-09-17).
It was written when nothing was ingested, and it made the trail unable to answer the question the
trail exists for. The replacement policy is stated as an outcome rather than a field list, because
maintaining a list of permitted fields is the complexity this decision was taken to avoid:

> **The audit trail must show that *this user* called *this tool* and accessed, modified or deleted
> data for *these customers*.** Log whatever is needed to establish that, in the simplest way that
> works.

### The one boundary retained: arguments yes, response bodies no

Tool **arguments** are recorded in full, including free-text search terms that may contain names or
email addresses. Response **bodies** are not.

This is not a re-introduction of the old rule by the back door. Vitally holds meeting **transcripts**
and arbitrary customer **traits**, so logging bodies would put entire meeting recordings and whatever
a CSM has typed into a custom field into telemetry — a second copy of the customer database under
weaker access control, which is a different thing from an audit trail. Arguments plus returned record
ids satisfy the requirement above; bodies add only content the requirement does not ask for.

### What this obliges

Recorded so a future reader does not mistake these for oversights:

- **Retention becomes an obligation, not a preference.** Personal data needs a defined period that is
  then honoured.
- **Erasure requests reach the logs.** Azure supports purge, but it is asynchronous — know this
  before being asked, not during.
- **Access control is now a control, not hygiene** — see *Who can read this* below. This is the
  condition attached to the decision: the data is acceptable to store *because* it is restricted.

### The framework leak still has to be closed

`System.Net.Http.HttpClient.*` logs outbound request URIs *including query strings* at `Information`,
for every Vitally and Graph call (#143). That is **not** made acceptable by this policy change: the
policy permits deliberate, structured, access-controlled audit records, not the same data scattered
through diagnostic categories nobody configured, in a table with different retention and broader
access. Close it as planned.

**Which tools actually expose a term, corrected.** Only `Search_users` and `Search_admins` — they
call `GetResourcesAsync("users/search" | "admins/search", …, additionalParams, …)`, and
`additionalParams` becomes the query string. **`nameContains` does not**: `GetByNameContainsAsync`
pages the list endpoint and applies the predicate *locally*, because Vitally has no name filter, so
the term never leaves the process. An earlier draft attributed the exposure to it; that was wrong and
would have sent whoever fixed this to the wrong call path.

## Design — code (what is emitted)

### Audit: the tool call is the record; the upstream call corroborates it

**The tool call is the primary audit record**, carrying arguments and returned record ids. The
upstream record is kept as corroboration, not as the mechanism.

An earlier draft of this design had it the other way round, on the reasoning that only the upstream
path names a customer record. **That reasoning was wrong**, and the correction matters enough to
record: the path names a customer only on get-by-id. Confirmed against today's live log sample —

```
https://rest.vitally-eu.io/resources/organizations?<query>
```

— a list call returning twenty customers, naming none of them, because the identities are in the
response body. `Search_users` is the same. List and search are the majority of reads, so the upstream
path answers the customer-access question for a minority of traffic.

It is also the wrong layer on principle: it records what the *server happened to call*, so the trail
is coupled to implementation detail (`Get_organization_summary`'s fan-out is four calls today), it
captures incidental reads as audit events (that tool lists the whole custom-object catalogue to
resolve names to ids), and it is derived by string-parsing a URL.

#### The record

One record per tool call, satisfying *user → tool → customers* directly:

| Field | Source | Why |
|---|---|---|
| caller | `CallerIdentity` object id | the same identity the authorisation decision used |
| tool name | SDK | captured only on denial today |
| **arguments** | tool invocation, **in full** | the scope the user asked for — `organizationId`, `nameContains`, search terms |
| **returned record ids** | response, ids only | names the customers a **list or search** touched |
| result count | response | magnitude; and the fallback when ids are capped |
| outcome, duration | filter | success/failure and performance in one place |
| correlation id | generated per call | ties the upstream records below to this one |

**Returned record ids close the bulk-read gap.** Without them, `List_organizations(limit=100)`
records that a hundred customers were read and names none — which fails the stated requirement.
Ids are identifiers, not bodies, so this stays the right side of the boundary above.

⚠️ **Cap the id list.** The bounded auto-pager can fetch ten pages of a hundred, so an uncapped list
is ~1000 ids in one record. Cap it (100 is a reasonable start), and **always** record the true count
alongside, so a capped record still says *"read 640 organisations, first 100 listed"* rather than
silently under-reporting. Beyond that threshold the meaningful audit fact is the bulk read itself.

#### The upstream record is kept

Still worth having, correlated to the tool call: it shows what the server actually did, which is what
diagnoses a failure or an unexpected fan-out. It is no longer load-bearing for customer identity, so
if volume ever forces a cut, this is the tier to cut — the reverse of the earlier draft.

#### Also closed by this design

- **sign-in records** — who authenticated and when is not captured at all. See *Open questions*:
  Entra sign-in logs may be the better source than anything built here.

### System failures

- **The CallTool filter swallows errors.** `Program.cs` catches every surfaceable exception and
  returns it to the client without logging, so **Vitally upstream failures and `ArgumentException`
  validation failures leave no server-side trace**. Log at `Error` before returning.

  Correcting an earlier draft: **RBAC denials are not in this gap.** They are already recorded —
  `VitallyService.SendAsync` calls `LogDenied`, and the SDK authorisation checkpoint calls
  `LogToolCallDenied`, which exists precisely because that checkpoint rejects before `SendAsync`
  runs. Denials are the best-covered path here, not the worst.
- **`VitallyService.SendAsync` non-2xx is *partly* covered, and an earlier draft overstated this.**
  `_audit.LogAction(method, url, (int)response.StatusCode)` runs **before** the throw, for every
  response — so the status code *is* recorded. What is missing is an **Error-level record carrying
  the reason**: the body snippet goes into the exception message for the client and is never logged.
  Log the status and resource at `Error` — **never the body**, which can carry customer PII, which is
  presumably why it was left out and why the failure became invisible.

  Note the interaction with #139: before `IncludeReads` defaulted true, a failed **GET** produced no
  record at all, because `LogAction` returns early for GETs. That is now covered.
- **Rate-limit exhaustion is already logged**, correcting an earlier draft — `VitallyRateLimitHandler`
  warns *"retries exhausted, returning 429 to caller"*. The gap is that it is a log line rather than a
  **counter**, so pressure against the 1000 req/min budget cannot be trended or alerted on.
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

### Logging configuration — in code, not `appsettings.json`

⚠️ **`appsettings.json` cannot carry this, and the obvious fix silently does nothing.** Both
`.gitignore` (line 111) and `.dockerignore` (line 18) exclude `appsettings.json` and
`appsettings.*.json`, carving out only `!appsettings.Example.json`. So a file added there would not
be committed, would not enter the build context, and would never reach the image — while working
perfectly on a developer machine. That exclusion is deliberate and worth keeping: it is what stops a
real `appsettings.json` with secrets being committed.

Configure the levels **in `Program.cs`** via `builder.Logging.AddFilter(...)` instead. Same reasoning
as `IncludeReads` in #139: it ships with the image, cannot drift per deployment, and is reviewable in
the diff. Environment variables are explicitly rejected for the same reason they were there — a
Container App recreate does not inherit them, so the constraint would lapse silently.

It does three jobs at once:

- cuts the framework noise that is ~90% of volume, which is what makes retaining the audit tiers
  affordable
- **constrains `System.Net.Http.HttpClient.*`**, closing the query-string exposure above
- makes levels reviewable in source rather than implicit in framework defaults

Keep `appsettings.Example.json`'s `Logging` section in step, or delete it — it currently documents
behaviour that is not in force, which is how it misled this design's first draft.

### Routing the audit records — a category is not a route

⚠️ **An earlier draft said "give audit records a stable category so they can be routed", citing
`ILogger<AuditLogger>`'s `VitallyMcp.AuditLogger`. That does not work, and taken literally it would
put customer data in the wrong table.** `AddFilter` sets *levels per provider*; it does not send
records to different destinations. `AuditLogger` writes through `ILogger`, so its records go wherever
the registered providers go — today the console, and therefore `ContainerAppConsoleLogs`, which the
data map says must **not** hold customer data.

Two things have to be specified, not one:

**1. The emitter, which decides the table.**

| Emitter | Lands in | Notes |
|---|---|---|
| `TelemetryClient.TrackEvent` | `AppEvents` | structured name + properties; what the data map assumes |
| `ILogger` + App Insights provider | `AppTraces` | no new dependency in `AuditLogger`, but shares a table with ordinary trace output |

Either is defensible; **pick one and make the data map match it.** The map currently says `AppEvents`,
so `TrackEvent` is the default reading — but a design that keeps `AuditLogger` on `ILogger` must say
`AppTraces` instead. What is not acceptable is naming a destination no emitter populates, which is
what the draft did: retention and access controls would have been applied to an empty table while the
records accumulated somewhere else.

**2. Suppression from the console provider**, which is the part that actually protects the table:

```csharp
builder.Logging.AddFilter<ConsoleLoggerProvider>("VitallyMcp.AuditLogger", LogLevel.None);
```

Provider-specific, so audit records reach App Insights and **not** stdout. Without it, phase 2 exports
them to `ContainerAppConsoleLogs` regardless of where else they go — short retention, broad access,
and a table documented as customer-data-free while carrying names and search terms.

Verify it by sampling the console stream after deploy and confirming no `Vitally audit:` line appears,
rather than by reading the configuration.

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

1. Add a diagnostic setting on the CAE for **`ContainerAppConsoleLogs` and `ContainerAppSystemLogs`**,
   exported **resource-specific** (a real table, not `_CL`, which is also what makes per-table
   retention possible).

   **Both categories, because the justification needs both.** A `StartupGuards` failure throws and
   writes to *stdout* → console logs; container crashes, OOM kills and scaling events are platform
   events → system logs. An earlier draft enabled console only while citing crashes and OOM as the
   reason to keep this setting at all, which would not have delivered the coverage it claimed.
   System logs are low volume.
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

### One workspace, not two destinations

Worth stating plainly, because "App Insights **and** Log Analytics" reads like a split and is not:
**workspace-based Application Insights *is* the Log Analytics workspace.** `vitally-prod-appi-uksouth`
has `workspaceResourceId` → `vitally-prod-law-uksouth` (verified), so the SDK writes into that same
workspace under `App*` tables. It is an SDK and a query experience over the workspace, not a second
store. Everything below lives in **one** workspace, separated by table so retention and access can
differ per tier.

### Where the data lives

The map, so a future incident does not start with "where would that even be":

| Data | Table | Written by | Contains customer data | Retention |
|---|---|---|---|---|
| **Audit trail** | `AppEvents` | App Insights SDK, in-process | **yes** — arguments and record ids | long, deliberate |
| Failures, warnings | `AppTraces` | App Insights SDK | incidental only | medium |
| Upstream call detail | `AppDependencies` | SDK auto-collection, **sanitised** | ids in paths | medium |
| Performance counters | `AppMetrics` | `Meter` via SDK | no | short |
| Container stdout / **app failed to start** | `ContainerAppConsoleLogs` | CAE diagnostic setting (#142) | should not — #143 | short |
| Platform events, scaling | `ContainerAppSystemLogs` | CAE diagnostic setting | no | short |
| Key Vault access | `AzureDiagnostics` | Key Vault diagnostic setting | no | as-is |
| ACR pulls, pushes | `ContainerRegistry*` | ACR diagnostic setting | no | as-is |

**Why the CAE diagnostic setting is kept even though the SDK covers the app.** It catches what
in-process telemetry cannot: `StartupGuards` throwing on an unreachable OIDC document — a live
failure mode, the app refuses to start — plus container crashes and OOM kills. The SDK never
initialises in exactly the scenario where the log matters most.

⚠️ **`AppDependencies` needs a sanitising telemetry processor from the outset.** SDK auto-collection
records outbound HTTP URLs, which reintroduces #143's exposure through a different door and into a
table with different retention from the audit one. Strip query strings there; the audit record is
where arguments belong.

### Who can read this

The policy reversal above is conditional on this, so it is a design element rather than an operational
afterthought: *the data is acceptable to store because it is restricted.*

Measured 2026-09-17 on `vitally-prod-law-uksouth`:

| | Count |
|---|---|
| Role assignments **at the workspace** | **0** |
| Distinct **users** with read-capable roles | 5 (4 Owner, 1 Reader) |
| **Service principals** with read-capable roles | 28 (11 Contributor, 9 Log Analytics Contributor, 3 Reader, 3 Monitoring Contributor, 2 Owner) |

Five people is a defensible set. Two things are not:

1. **Nothing is assigned at the workspace**, so access is entirely inherited from the subscription and
   management group. It is not controlled here and will drift whenever subscription RBAC changes —
   nobody editing subscription roles is thinking about this table.
2. **28 automation principals**, several holding broad `Contributor`, is a wide surface for a store
   that now holds customer personal data.

Proportionate response, deliberately not a re-platform:

- **Review the 28 service principals** and confirm each needs workspace read. Several are Defender and
  platform automation and probably do.
- **Consider table-level RBAC** on the audit table. Log Analytics supports per-table access, so the
  audit table can be restricted while diagnostics stay broadly readable. Note the limit honestly: an
  inherited subscription `Contributor` still reads everything, so this only bites once the broad roles
  are narrowed — it is worth doing in that order, not instead of it.
- **Do not build a separate workspace for audit.** It would give the cleanest boundary and costs a
  second ingestion path, DNS, private endpoint and query surface — disproportionate to moving five
  users and reviewing a service-principal list.

### Retention

Deferred until volume is measurable, which needs the above. Decide per tier, on evidence, against the
stated one-year requirement — currently 30 days workspace-wide. Do not set retention before the audit
tiers and the noise reduction land, or the number will be measured against the wrong traffic.

Retention on the audit table is now an **obligation** rather than a preference, because it holds
personal data — see the policy section.

## Phasing

| | Work | Depends on |
|---|---|---|
| 0 | query path open | **done** 2026-09-17 |
| 1 | audit reads by default | **done** — #139 / PR #140 |
| 3 | logging configuration: noise + `HttpClient` PII | — |
| 2 | diagnostic setting; verify arrival; re-lock ingestion | **3** |
| **3a** | **access review — a gate, not a task**: confirm the 5 users are appropriate, review the 28 service principals, decide on table-level RBAC | — |
| 4 | audit tiers: tool-call record, correlation id, sign-in, result count | 3, **3a** |
| 5 | failure logging | 3 |
| 6 | performance: durations, counters, tracing | 3 |
| 7 | routing and retention per tier | 2, 4, measured volume |

⚠️ **3 must come before 2, and an earlier draft had them independent — which was wrong.** Phase 2
turns on export of the *whole* console stream, and that stream today carries the `HttpClient` URLs
with search terms (#143) plus `AuditLogger`'s object ids and resource paths. Enabling export first
would ingest exactly the customer identifiers the data map says `ContainerAppConsoleLogs` must not
hold, into the table with the **shortest** retention and the **broadest** access — the opposite of
where the policy reversal put that data deliberately.

The temptation is real, because 2 is an Azure setting that takes a minute and 3 is a code change
needing a deploy. Do them in the order that does not contaminate the table.

3 also comes before 4–6, so new records are not added to an unfiltered stream.

⚠️ **3a gates 4, and that ordering is the whole point.** Phase 4 is what starts writing customer
personal data, and the policy reversal permitting it was taken *on the condition* that the store is
restricted. Building 4 first would leave the condition unmet while the data it authorises accumulates
— which is the failure mode of every "we'll tighten access later" plan. If 3a turns out to be
harder than expected, that is a reason to re-open the policy decision, not a reason to proceed past
it.

## Risks

| Risk | Mitigation |
|---|---|
| Volume and cost rise once records actually flow, with reads now on | 3 removes ~90% noise first; retention decided per tier on measured volume, not guessed |
| PII reaching telemetry through a framework category nobody configured | 3 constrains `HttpClient`; `ContainerAppHTTPLogs` evaluated separately before enabling |
| Re-locking ingestion breaks delivery again | verify arrival at step 2 *before* re-locking, and re-verify after |
| Correlation id becomes a per-call-site convention that drifts | carry it through the existing `CallerIdentity`/`AuditLogger` choke points, which already exist for exactly this reason |
| **Personal data sits in a table whose access is inherited, not controlled** | review the 28 service principals; table-level RBAC once the broad roles are narrowed. This is the condition the policy reversal rests on — treat it as in scope, not follow-up |
| **An erasure request arrives and nobody has done one** | purge is asynchronous and per-table; rehearse once before it is needed, as #138 does for rotation |
| Returned-id capture inflates records on paged reads | cap the list (100) and always record the true count, so a capped record still reports the real magnitude rather than under-reporting silently |
| The withdrawn PII rule is reinstated by a later reader who sees "no PII" as obviously correct | the reversal and its reasoning are recorded in `CLAUDE.md` and here; it was a deliberate trade, not an oversight |

## Decisions taken (2026-09-17, @searledan)

Recorded so they are not re-litigated:

| Question | Decision |
|---|---|
| May the audit trail hold customer personal data? | **Yes** — the previous no-PII rule is withdrawn. Arguments in full; response bodies still excluded |
| What is the acceptance criterion? | *this user* called *this tool* and accessed/modified/deleted data for *these customers* |
| Primary audit mechanism | **Tool call**, with arguments and returned record ids — not the upstream path, which names customers only on get-by-id |
| Bulk reads | Record returned ids, capped, always with the true count. Not accepted as a gap |
| Destination | One workspace, separated by table. App Insights SDK for app telemetry, ingesting over the private endpoint |
| Why Log Analytics remains | It is the same store — workspace-based App Insights writes into it |
| Access control | A condition of the policy reversal, not a follow-up |

## Open questions

- **Sign-in records** — the server sees tokens, not sign-ins, so it cannot record an authentication
  it never observes. Entra sign-in logs already hold this and are the likely answer; confirm they are
  retained long enough to pair with this trail before building anything here.
- **Table-level RBAC sequencing** — worth confirming against the live tenant that restricting the
  audit table behaves as expected once the inherited roles are narrowed, rather than assuming it from
  the documentation.
- **Erasure mechanics** — Azure Monitor purge is asynchronous and per-table. Worth rehearsing once
  before it is needed against a real request, in the same spirit as #138's rotation dry-run.
