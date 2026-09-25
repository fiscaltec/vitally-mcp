# Logging and observability — design (supersedes the 2026-08-11 spec)

**Status:** partly implemented — phases 0, 1, 2a, 3 and 3a are done; 4 onwards are not. The
*Phasing* table at the foot of this document is the current state and the map to the GitHub issues;
it is the section to read first and the section to keep current.

**Supersedes** `2026-08-11-observability-design.md`, which is kept as a dated artefact. That spec's
shape was right in outline and wrong in two load-bearing ways, both found on 2026-09-17 once its own
Phase 1 made the workspace readable for the first time:

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

All of the following was verified rather than inferred, but by two different methods, and the
distinction matters when re-checking it:

- **Measured against production** — the KQL results, the `Usage` breakdown, the absence of
  `MICROSOFT.APP` rows, the live console sample and its category counts, the Azure resource settings,
  and the workspace role-assignment counts.
- **Read from this repository** — the 14-call-site inventory, the absence of `Activity`/`Meter`, the
  `.gitignore`/`.dockerignore` exclusions, and `appsettings.Example.json` being inert.

The second set describes the code as committed, so it holds for any deployment of this revision; the
first describes FISCAL's production estate on that date only.

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
private DNS zones linked to `vitally-prod-vnet-uksouth`.

### Root cause: shared-key authentication is disabled on the workspace

```console
$ az monitor log-analytics workspace show -n vitally-prod-law-uksouth -g vitally-prod-rg-uksouth \
    --query "features.disableLocalAuth"
true
```

**The CAE's `appLogsConfiguration` is a shared-key shipper** — it authenticates with the workspace id
and primary shared key. `disableLocalAuth: true` refuses exactly that. So this path could never have
delivered a row, from the day the workspace was created, and no amount of network configuration would
have changed it.

This explains the whole picture at once:

| Observation | Explanation |
|---|---|
| `ContainerAppConsoleLogs_CL` has 0 rows, ever | shared-key auth refused from day one |
| Key Vault and ACR data arrives normally | diagnostic settings do not use shared keys |
| Enabling `publicNetworkAccessForIngestion` changed nothing in 14 minutes | the wrong control — authentication, not network |

⚠️ **Two earlier hypotheses in this document were wrong**, and are recorded rather than quietly
deleted because each looked convincing:

1. *The `PrivateOnly` posture blocks the Container Apps platform shipper.* Tested by opening public
   ingestion on 2026-09-17. Nothing arrived in 14 minutes of polling. Disproved.
2. *The CAE's stored shared key is stale and needs re-applying.* Would also have failed — the
   workspace refuses shared-key authentication whatever the key's value.

**The diagnostic-setting fix is unaffected**, and this is now a stronger argument for it rather than a
weaker one: diagnostic settings authenticate through the Azure Monitor control plane rather than a
shared key, which is precisely why Key Vault and ACR records arrive into this same workspace today.

It also means `internet_ingestion_enabled = true` was opened to test a hypothesis that turned out to
be wrong and is **not needed by the fix at all** — so the re-lock (#142) can happen immediately
rather than after the diagnostic setting is verified.

**Do not "fix" this by re-enabling local authentication.** Disabling it is a deliberate hardening
control, and turning it on to rescue a log-shipping path that has a better alternative would be
trading a real control for a worse mechanism.

### The signal-to-noise ratio is inverted

202 lines sampled from the live console stream. **89 of them carry a logger category** — the rest are
continuation lines of multi-line messages (`Authorization failed. These requirements were not met:`
and its detail lines, for instance), which belong to the same records and inflate the line count
without adding signal. Counting by category:

| Category | Lines | Share of the 89 |
|---|---|---|
| `Microsoft.AspNetCore.Hosting.Diagnostics` (request start/finish) | 42 | 47% |
| `System.Net.Http.HttpClient.*` (Graph, Vitally, upstream) | 20 | 22% |
| `JwtBearerHandler` + `DefaultAuthorizationService` | 18 | 20% |
| Routing, MCP server | 9 | 10% |
| **audit records** | **0** | **0%** |

So **100% of that categorised sample was framework output and none of it an audit record** — which
was expected, since reads were unaudited until #139.

**Re-measured properly on 2026-09-18, by bytes rather than lines**, once logging was actually flowing:

| | Share of console bytes |
|---|---|
| The four framework noise categories | **67.3%** |
| `System.Net.Http.HttpClient.*` | 19.5% |
| Everything retained | 13.2% |

**Phase 3 removes both of the first two rows — 86.8% together.** The 67.3% figure is the noise filters alone; quote it only when the `HttpClient` filter is excluded.

`Hosting.Diagnostics` alone was 83 of ~150 entries. ⚠️ The earlier "~90% noise" figure counted
**lines in a categorised subset** and conflated the four framework filters with the `HttpClient`
one; 67.3% is the number for the four, and it is the one to quote.

The zero is not an artefact of the window. Reads were unaudited until #139, and the only authenticated
call made during the sample was a `List_organizations` GET, which `LogAction` skipped for exactly that
reason.

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

Tool **arguments** are recorded, including free-text search terms that may contain names or email
addresses. Upstream response **bodies** are not.

**"In full" needs a bound, because a create/update `jsonBody` is unbounded.** Define the overflow
rather than leaving it to the implementation:

- cap each argument value (1 KB is consistent with `VitallyService`'s existing `Truncate(body, 1024)`
  for error snippets) and the whole argument set (4 KB)
- on overflow, truncate the **value** and set a `truncated` marker on the record — never drop the
  argument, because its *presence and name* is part of the audit fact
- **scoping identifiers are never truncated.** `organizationId`, `accountId` and their siblings are
  short and are the fields the acceptance criterion depends on; truncating one would defeat the
  record's purpose to save bytes

So the criterion is met by the *identifiers*, which are always complete, while free-text and write
payloads are best-effort within the cap. A record that says *"alice updated account X, fields
{name, traits…} (truncated)"* still answers who, what and which customer.

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

⚠️ **#143 claimed `System.Net.Http.HttpClient.*` leaks search terms through outbound request URIs.
That premise was wrong and the issue is closed on those grounds.** .NET redacts query **values** by
default: the logged form is `GET .../users/search?*`, where `?*` is the redaction marker rather than
a truncation. Verified 2026-09-18 by a probe carrying a marker through a typed client — absent from
every record — and against live production logs, where every query in the stream is `?*`. Nothing
here disables it.

The filter still lands, as **noise reduction** (19.5% of console bytes) with defence-in-depth as a
footnote. Path segments are not redacted, but they carry record ids, which `AuditLogger` records
deliberately.

**For the record, since two drafts argued about it before the premise collapsed:** the only tools
that put a caller term in an outbound query string are `Search_users` and `Search_admins`, via
`additionalParams` on `GetResourcesAsync`. `nameContains` does **not** — `GetByNameContainsAsync`
pages the list endpoint and filters locally, so the term never leaves the process. Both facts remain
true; neither now matters, because the query values are redacted before they are logged.

## Design — code (what is emitted)

### Audit: the tool call is the record; the upstream call corroborates it

**The tool call is the primary audit record**, carrying arguments and returned record ids. The
upstream record is kept as corroboration, not as the mechanism.

An earlier draft of this design had it the other way round, on the reasoning that only the upstream
path names a customer record. **That reasoning was wrong**, and the correction matters enough to
record — as does its own over-correction. The path names a customer on get-by-id **and on scoped
lists**: `accounts/{accountId}/users` and `organizations/{organizationId}/users` both carry the
parent id, which `ResourcePath` preserves. So the upstream record covers more than a first correction
claimed.

What it does **not** cover is **unscoped list and search**, where the identities exist only in the
response body. Confirmed against today's live log sample —

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

⚠️ **Extract them from the raw upstream response, not from the tool result.**
`GetResourcesAsync` applies `FilterJsonFields` *after* `SendAsync`, so a caller passing
`fields=name` gets results with **no `id` at all** — and the audit record would silently name nothing
on exactly the calls a narrow projection was used for. The extraction point has to sit before field
projection, and it has to cover the paged path (`GetFilteredAsync`) and the raw pass-throughs
(`GetRawAsync`) too, not just the standard envelope. If that proves impractical for a given path,
record the count and mark ids unavailable for it — an explicit gap beats a record that appears
complete and is not.

⚠️ **Cap the id list, and be honest about what the count means.** The bounded auto-pager can fetch ten
pages of a hundred, so an uncapped list is ~1000 ids in one record. Cap it (100 is a reasonable
start), and record the count alongside so a capped record says *"read 640 organisations, first 100
listed"* rather than silently under-reporting.

**The count is records *fetched*, not records *matching*, and the two differ.** `GetFilteredAsync`
stops at `Vitally:MaxAutoPageFetches` and Vitally's envelope exposes only `next` — there is no total.
So when the pager truncates, the true number of matching records is **unknowable without unbounded
paging**, which is precisely what the cap exists to prevent. Requiring a "true count" would force
either an overstatement or that unbounded paging.

Record it as three fields rather than one, mirroring the envelope the tools already return
(`{results, truncated, pagesFetched}`):

| Field | Meaning |
|---|---|
| `recordsFetched` | how many were actually read |
| `idsRecorded` | how many ids the cap allowed into the record |
| `truncated` | `true` when the pager stopped early — the total is **unknown**, not equal to `recordsFetched` |

A record saying *"fetched 1000, ids 100, truncated"* is honest about a bulk read of unknown extent.
One saying *"read 1000"* would not be.

#### The upstream record is kept

Still worth having, correlated to the tool call: it shows what the server actually did, which is what
diagnoses a failure or an unexpected fan-out. It is no longer load-bearing for customer identity, so
if volume ever forces a cut, this is the tier to cut — the reverse of the earlier draft.

#### Also closed by this design

- **sign-in records — deliberately *not* a deliverable of phase 4.** Who authenticated and when is not
  captured, and **this server cannot capture it**: it receives bearer tokens and never observes the
  authentication that produced them. Building an in-process "sign-in audit" would record first *use*
  of a token and mislabel it. Entra sign-in logs already hold the real thing — see *Open questions*:
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

- cuts the framework noise — **measured at 67.3% of console bytes** on 2026-09-18 (300-record live
  sample; `Hosting.Diagnostics` alone was 83 of ~150 entries). ⚠️ This is **not** a cost argument, and
  an earlier draft wrongly made it one: unfiltered the stream runs ~9.1 MB/day, so these filters save
  ~2.24 GB/year, which is single-figure pounds — and per-table retention lets the noise expire at 30
  days regardless. The justification is **readability of the live stream**, which is how a running
  container is debugged and the only way to see startup failures until phase 2b
- **constrains `System.Net.Http.HttpClient.*`**, closing the query-string exposure above
- makes levels reviewable in source rather than implicit in framework defaults

Concretely, so an implementation cannot follow this document and still leave the exposure open:

```csharp
// Outbound request URIs, one pair per call. NOTE: query VALUES are redacted by .NET (`?*`),
// so this is noise reduction — #143 framed it as a PII control and that was wrong.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// Noise. 67.3% of console bytes, measured 2026-09-18. Kept for live-stream readability, not cost.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Authorization", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
```

`Warning` rather than `None` throughout: a failing outbound call or a genuine authentication fault
must still surface. It is the `Information`-level *success* chatter that carries both the volume and
the URIs.

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

| Emitter | Lands in | |
|---|---|---|
| **`ILogger` + the `microsoft.custom_event.name` attribute** (Azure Monitor OTel exporter) | **`AppEvents`** | **chosen — 2026-09-24, #164** |
| `TelemetryClient.TrackEvent` | `AppEvents` | superseded — see below |
| `ILogger` + App Insights provider, no attribute | `AppTraces` | rejected |

⚠️ **Superseded 2026-09-24 (#164). The destination is unchanged — `AppEvents` — but the mechanism is
the Azure Monitor OpenTelemetry exporter, not `TelemetryClient`.** The table above set up a choice
between `TrackEvent` and "`ILogger` + App Insights provider", and missed a third option that gets the
same table: `ILogger` **plus the `microsoft.custom_event.name` attribute**, which the OTel exporter
reads to write an `AppEvents` row. Why the change:

- Microsoft's stated position is *"for new applications, use the Azure Monitor OpenTelemetry
  Distro"*. The classic 2.x SDK is deprecated with retirement on **2027-03-01**, and 3.x is a
  migration bridge documented as **not** to be run alongside the distro.
- Classic 3.x implements `TrackEvent` **by setting that same attribute** on an OTel log record. It is
  a shim over the same exporter, so choosing it would buy a friendlier call and an extra deprecation
  cycle for an identical wire format.
- The stated cost below — `AuditLogger` taking a `TelemetryClient` dependency — is therefore avoided
  entirely; it keeps only its `ILogger`.

⚠️ **The new failure mode, which the old one did not have:** routing is decided by an exact,
case-sensitive attribute match. Miss it and the record becomes an `AppTraces` row **silently** — no
error, no warning — which is the very outcome the row below rejects. A test asserts the exact key on
every emission for that reason.

**The destination is authoritative**, and this is a decision rather than an option, because phases 4
and 7 apply retention and access controls to a named table and cannot do that against an unresolved
choice. Reasons: `AppEvents` is a dedicated table, so per-table retention and table-level RBAC apply
to the audit trail *and nothing else*; typed properties survive as queryable dimensions rather than
being formatted into a message; and it does not share a table with ordinary trace output, which
`AppTraces` would — putting the PII-bearing records back in with general diagnostics, which is the
separation this design exists to create.

~~Cost, stated so it is not a surprise: `AuditLogger` takes a `TelemetryClient` dependency alongside
its `ILogger`. That is the trade for the table boundary.~~ **No longer applies** — see the supersession
above. `AuditLogger` keeps only its `ILogger`; the attribute does the routing.

**2. Suppression from the console provider**, which is the part that actually protects the table:

```csharp
builder.Logging.AddFilter<ConsoleLoggerProvider>("VitallyMcp.AuditLogger", LogLevel.None);
```

Provider-specific, so audit records reach App Insights and **not** stdout. Without it, **phase 2b**
exports them to `ContainerAppConsoleLogs` regardless of where else they go — short retention, broad
access, and a table documented as customer-data-free while carrying names and search terms. This
suppression is precisely why 2b is gated on phase 4 rather than shipping with 2a.

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

⚠️ **A diagnostic setting alone does nothing. Two things must change together.**

The environment's `logs_destination` must be **`azure-monitor`**. With the default `log-analytics`
the CAE writes directly to the workspace using its **shared key**, ignores diagnostic settings
entirely, and — because `local_authentication_enabled = false` — is refused. That is the root cause
in #142, and an earlier version of this phase omitted it: the setting was created on 2026-09-17,
nothing arrived for 18 minutes, and the destination was why.

Microsoft documents the constraint directly, and it is not a workaround but the supported path:

> **Private link**: Sending logs directly to a Log Analytics Workspace through Private Link isn't
> supported. However, you can use Azure Monitor and send your logs to the same Log Analytics
> Workspace. **This indirection is required to prevent system log data loss.**

So the original configuration was never going to work here — not drift, not a mistake made later.
Note the failure is silent in **both** directions: `azure-monitor` with no diagnostic setting also
discards logs quietly.

1. Set the environment's `logs_destination` to `azure-monitor`, then add a diagnostic setting on the
   CAE, in **two steps**:

   - **2a, immediately: `ContainerAppSystemLogs`.** Platform events — crashes, OOM kills, scaling,
     revision changes. No customer data, no dependency on a code change, and it covers the most
     acute gap: today the app can die leaving no record anywhere. Low volume.
   - **2b, gated on phases 3 and 4: `ContainerAppConsoleLogs`.** Only once `AuditLogger` has moved
     to `TrackEvent` and console suppression is in place, or this exports the customer identifiers
     the data map says this table must not hold.

   Note that neither alone covers everything: a `StartupGuards` failure throws and writes to
   *stdout*, so it lands in **console** logs, while a crash or OOM is a **platform** event. Until 2b
   lands, read startup failures from the live stream, which is independent of the export path:

   ```bash
   az containerapp logs show -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
     --type console --tail 100
   ```
2. ✅ **Verified 2026-09-17: records arrive.** First rows landed at `18:25:09`, seconds after the
   destination change, from **both** apps — `ContainerAppReady`, `ContainerAppUpdate`,
   `RevisionUpdate`, `RevisionDeactivating` on production; `ContainerStarted`, `ContainerTerminated`,
   `KEDAScaleTargetDeactivated` on staging. This is the first telemetry this server has ever
   delivered to Log Analytics.

   Two things that cost time here, both worth carrying forward:

   - **Read configuration back from ARM; the write response lies.** Both
     `--export-to-resource-specific true` and an explicit PUT carrying
     `"logAnalyticsDestinationType": "Dedicated"` **return it and store `null`** for this resource
     type. Resource-specific export turns out to be **implicit** — confirmed by the live schema
     (typed `ContainerAppName`/`Reason`/`RevisionName` columns, no `_s` suffixes), so per-table
     retention *is* available to phase 7 despite the property reading null.
   - **Beware `has` in the verification query.** A poll filtering
     `where Type has 'ContainerApp'` returned nothing for 20 minutes *while records were arriving*,
     because KQL `has` matches whole terms and `'ContainerAppSystemLogs' has 'ContainerApp'` is
     false. Use `startswith`/`contains`, or query the table directly. A verification that fails
     closed on its own bug is worse than none: it nearly produced a report that the fix had failed.
3. ✅ **Done 2026-09-17: `publicNetworkAccessForIngestion` re-locked to `Disabled`**, restoring the
   hardening opened earlier that day while this was being diagnosed. It was never needed — the cause
   was authentication and destination, not network.
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

**Reviewed 2026-09-21 (#146, phase 3a).** The figures below replace the 2026-09-17 estimate, which
was taken from a role-name summary and was wrong in both directions — it counted a group and an
external principal as users, and it counted role *assignments* rather than distinct principals.

Method, so it can be repeated rather than re-guessed: enumerate `az role assignment list --scope
<workspace> --include-inherited --include-groups`, then keep only the roles that actually grant
`Microsoft.OperationalInsights/workspaces/query/read` — the eight with a blanket `*` or `*/read`
(`Owner`, `Contributor`, `Reader`, `Log Analytics Contributor`, `Monitoring Contributor`,
`Resource Policy Contributor`, `Role Based Access Control Administrator`, `User Access
Administrator`), none of whose `notActions` touch it. Roles that merely *name* OperationalInsights
are not among them: `Defender Containers Sensor` and `Defender Kubernetes Agent Operator` hold
`workspaces/read` and `workspaces/sharedkeys/*`, which is workspace metadata and the shared key —
**not** log data, and the shared key is inert here because `local_authentication_enabled = false`.

| | 2026-09-17 estimate | Measured 2026-09-21 |
|---|---|---|
| Role assignments **at the workspace** | 0 | **0** — confirmed |
| Named **FISCAL humans** who can read | "5 users" | **4** |
| **Break-glass** emergency accounts | counted among the users | **2**, permanent `Owner`, by design |
| **External** principals | not identified | **1** — an MSP with `Owner` via delegated administration |
| **Service principals** with a read-capable role | 28 | **25** at review → **22** after cleanup |
| **Orphaned principals** (deleted from the directory) | not identified | **4**, holding **14** assignments — **13 removed 2026-09-21**, 1 held |

**The human side meets the condition, and more strongly than the estimate suggested.** The four are
`dsearle.adm`, `jpobgee.adm`, `lnewton.adm` and `etomblin.adm` — the IT administrators — and their
elevated access is **PIM-gated, not permanent**: `Owner` and `User Access Administrator` are
*eligible* via `Azure - Global Administrators` and `Contributor` via `Azure - IT Administrators`,
so they are activated and time-bound (one such activation was live during the review, expiring the
same day). The one standing human grant is `etomblin.adm`'s permanent `Reader`; the
`Azure - Infrastructure Administrators` `Reader` is itself time-bound. The two break-glass accounts
(`Charles Ponzi`/`Frank Abagnale (Break Glass)`, titled *Emergency Access Account*) hold permanent
`Owner` deliberately — that is what a break-glass account is for, and they are **not** a finding.

**The machine side is where the condition is unmet**, and it splits into two very different classes:

| Class | Count | Assessment |
|---|---|---|
| Microsoft platform automation — Defender/ASC provisioning, `MS-PIM`, SQL/Arc protection, Defender for Storage operator | ~13 | Expected. These provision and scan; none of them runs KQL against a table. Broad scope is how Defender works |
| **FISCAL-controlled** — `sp-terraform-deploy-itproduction` (Contributor + RBAC Admin), `sp-terraform-policy-tenant`, `fiscaltecvsts-ITTeam-*` and `fiscaltecvsts-Infrastructure-*` (both **Owner**), `MI-UA-ComplianceManager`, `Power Automate`, `Tenable - Azure Cloud Connector`, `Testing Dan Dan Dan` | **8** | The real surface. Two Azure DevOps service connections hold permanent `Owner`; one entry is a **test application** with `Reader` on the production subscription |
| **Orphaned** — principals no longer in the directory | **4**, holding **14** assignments | Dead: nothing can authenticate as a deleted principal, and all four are *permanently* gone rather than soft-deleted (checked against `directory/deletedItems`, so none is restorable). Two of them carried `Contributor` + `Log Analytics Contributor` + `Monitoring Contributor` + `User Access Administrator`, which reads alarmingly and grants nothing. **Removed** — see below |

⚠️ **Correction to this document's own earlier suggestion: table-level RBAC cannot restrict any of
the above.** Azure RBAC is **allow-only** — there is no deny. A table-level role grants
`workspaces/query/<table>/read` to someone who had nothing; it does not subtract from a principal
already holding `*/read`. So table-level RBAC is a tool for *adding a narrow reader later*, never
for fencing the audit table off from the inherited grants. The previous wording ("only bites once
the broad roles are narrowed") was directionally right and read as though the order was the only
obstacle. It is not: at the point the broad roles are narrowed, table-level RBAC has nothing left
to do.

⚠️ **The rejection of a separate workspace rested on a premise the measurement disproves.** It was
rejected as "disproportionate to moving five users and reviewing a service-principal list" — but
the humans turn out to be fine, and the part that is not fine is precisely the part that cannot be
moved from this repo: an **external MSP holding `Owner`** through delegated administration
(membership invisible in our tenant) and two Azure DevOps service connections holding `Owner` at
subscription scope. Narrowing those is an IT-wide decision about the production subscription, not
an audit-trail change. A workspace in a *different subscription* is the only measure that creates
an actual boundary. That is a genuine re-opening, not a re-litigation — recorded so the decision is
made on the corrected facts.

#### Disposition

These are **subscription** RBAC, not repository changes. What was actually done, and what was
deliberately not:

| | Status |
|---|---|
| **Remove the orphaned assignments** | ✅ **Done 2026-09-21.** 13 of the 14 deleted (authorised: dsearle). Effective assignments at the workspace **80 → 67**; read-capable service principals **25 → 22**. The records were captured to JSON before deletion — though note that a role assignment for a permanently-deleted principal cannot be meaningfully restored, so the backup is an audit artefact, not a rollback |
| ⏸ **One orphan held** | The 4th principal's only remaining assignment is `Contributor` at **management-group** scope (`internal-fiscal`), which governs more than IT-Production. Removing it changes no effective access — the principal is gone — but the blast radius is wider than the workspace review that authorised the rest, so it was left for a separate decision |
| ⏸ **`Testing Dan Dan Dan`'s `Reader`** | **Left in place, deliberately** (decision: dsearle, 2026-09-21). Despite the name it is not to be removed. Recorded here so a later reviewer does not "tidy" it, and so the read surface is counted honestly with it included |
| ⏳ **The two `fiscaltecvsts-*` `Owner` grants**, and `Power Automate` / `Tenable` | Not actioned. An Azure DevOps service connection rarely needs `Owner`, but narrowing it is an IT-wide decision about the production subscription rather than an audit-trail change |
| ⏳ **The external MSP `Owner` grant** | Not actioned. If it is contractual it should be recorded as accepted risk rather than left implicit — it is the single widest read path to this data and nothing in this repo constrains it |

The cleanup that was done removes noise rather than exposure: nothing could authenticate as those
principals. Its value is that the next reviewer reads a set that someone chose, which is the
difference between *"only readable by certain people"* and an inherited accident.

### Retention

Deferred until volume is measurable, which needs the above. Decide per tier, on evidence, against the
stated one-year requirement — currently 30 days workspace-wide. Do not set retention before the audit
tiers and the noise reduction land, or the number will be measured against the wrong traffic.

Retention on the audit table is now an **obligation** rather than a preference, because it holds
personal data — see the policy section.

## Phasing

Rows are in execution order, which is not numeric order. The issue column is the index: phase numbers
here and issue numbers in GitHub are **different schemes**, and reading one as the other is what made
this section hard to follow — #93 was titled "Phase 2" while being phase 7 of this document.

⚠️ **Trust this column, not the issue title.** The *open* issues were retitled on 2026-09-22 to drop
phase numbers, with the phase stated in the body instead — but **closed issues keep their original
titles**, and one of them collides with this very table: **#92 is titled "Observability Phase 1" and
is phase 0 here**, while phase 1 is #139. Retitling a closed issue would rewrite the record of what
was actually done, so the collision is left in place and flagged rather than tidied away.

| | Work | Issue | State |
|---|---|---|---|
| 0 | query path open | #92 | ✅ **done** 2026-09-17 |
| 1 | audit reads by default | #139 / PR #140 | ✅ **done** 2026-09-17 |
| 3 | logging configuration: noise + `HttpClient` PII | #143 | ✅ **done** 2026-09-21 |
| **2a** | diagnostic setting for **`ContainerAppSystemLogs` only**; verify arrival; re-lock ingestion | #142 | ✅ **done** 2026-09-17 — and **still delivering**: 868 rows spanning 2026-09-17T18:25:09Z → 2026-09-22T11:38:18Z, re-checked 2026-09-22 |
| **2b** | add **`ContainerAppConsoleLogs`** to that setting | #142 | **ready** — 3 ✅ and 4 ✅ as of 2026-09-25. Not enabled yet, and NOT per-target: the setting is on the CAE both apps share, so a staging spin-up without `ApplicationInsights__ConnectionString` would export its unsuppressed console |
| **3a** | access review — a gate rather than a task | #146 | ✅ **done** 2026-09-21 — see the summary below and *Who can read this* |
| 4 | audit tiers: tool-call record, arguments, returned ids, result count, correlation id, **effective permission tier**, **MCP client** | #147 | ✅ **done** 2026-09-25 — switched on and verified by reading `VitallyToolCall` rows back out of `AppEvents` on **both** targets |
| 5 | failure logging | #94 | **ready** — 3 done |
| 6 | performance: durations, counters, tracing | #94 | **ready** — 3 done |
| 7 | routing and retention per tier | #93 | blocked on **2b**, **4**, measured volume (2a ✅) |
| 8 | dashboards and alerts | #159 | blocked on 4, 5, 6 |

**3a in one line:** humans pass — 4 named IT administrators with elevated access PIM-gated, plus 2
by-design break-glass accounts. The residual is machine-side: 8 FISCAL-controlled service principals
(2 with `Owner`, 1 a test app) and an external MSP with `Owner`, none of them actioned. **4 orphaned
principals** were also found, holding **14** assignments between them — **13 removed on 2026-09-21**,
the 14th held because it sits at management-group scope; that one is a pending decision, not an open
exposure, since nothing can authenticate as a deleted principal. Table-level RBAC was found
**unable** to help — Azure RBAC is allow-only and cannot subtract from an inherited `*/read`.

**Phase 8 is not designed in this document**, and that is the one gap in it. The Workbook layout and
the seven alert rules live only in #159, which was split out of #94 on 2026-09-22 for that reason. If
this document is ever treated as complete, start there.

**Two fields were added to phase 4 on 2026-09-22** — `effectivePermissionTier` and `mcpClient` —
recovered from #93 before its rescope. The first is the load-bearing one: `LiveGroupCheck` resolves
entitlement from live Entra group membership, so entitlement at a past moment **cannot be
reconstructed**, and a record written without it can never answer *"was this person entitled to do
that at the time?"*. It is cheap while the record is being written and impossible afterwards — which
is why it is called out here rather than left in an issue. Record alongside it whether the tier was
served **stale** — `GraphGroupPermissionResolver` serves a retained set for up to
`LiveGroupStaleSeconds` when Graph fails, so a stale tier is a weaker claim than a fresh one and a
record that cannot tell them apart overstates its own confidence.

⚠️ **Console export is split out as 2b and gated, because the console stream carries customer
identifiers until the audit records are rerouted off it.** Two earlier drafts got this wrong in
succession: the first had 2 and 3 independent; the second gated 2 on 3, which is still not enough,
because phase 3 is noise and `HttpClient` filtering only — `AuditLogger` keeps writing object ids and
resource paths to stdout until **phase 4** moves it to `TrackEvent` and adds the
`ConsoleLoggerProvider` suppression.

So exporting console logs any earlier puts customer identifiers into
`ContainerAppConsoleLogs` — the table with the **shortest** retention and the **broadest** access, and
the one the data map declares customer-data-free. That is the opposite of where the policy reversal
deliberately placed that data.

**2a is not gated, and that is the point of splitting it.** `ContainerAppSystemLogs` carries platform
events — crashes, OOM kills, scaling, revision changes — with no customer data and no dependency on
any code change. It delivers the "the app died and we have no record" coverage immediately, which is
the most acute gap, and it lets the ingestion re-lock happen straight away rather than waiting on
phases 3 and 4.

The temptation to do all of 2 at once is real, because it is an Azure setting that takes a minute
while 3 and 4 are code changes needing a deploy. Split it.

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
| Volume and cost rise once records actually flow, with reads now on | **Measured 2026-09-18 and the risk is smaller than assumed**: the unfiltered console stream is ~9.1 MB/day (~3.33 GB/year), of which **phase 3 removes 86.8%** — 67.3% from the four framework noise filters and a further 19.5% from the `System.Net.Http.HttpClient` noise-reduction filter (defence-in-depth only — .NET already redacts query values), which is part of the same phase. (67.3% is the noise filters alone and is the figure quoted where only they are meant.) At Log Analytics rates the whole stream is single-figure pounds a year, so retention should be decided on the compliance requirement rather than on cost. Caveat: sampled over 4.2 quiet minutes, so treat it as a floor |
| PII reaching telemetry through a framework category nobody configured | 3 constrains `HttpClient`; `ContainerAppHTTPLogs` evaluated separately before enabling |
| Re-locking ingestion breaks delivery again | verify arrival at step 2 *before* re-locking, and re-verify after |
| Correlation id becomes a per-call-site convention that drifts | carry it through the existing `CallerIdentity`/`AuditLogger` choke points, which already exist for exactly this reason |
| **Personal data sits in a table whose access is inherited, not controlled** | Reviewed 2026-09-21 (#146). Humans are a reviewed set of 4, PIM-gated; the residual is 8 FISCAL-controlled service principals and an external MSP with `Owner`. **Table-level RBAC does not mitigate this** — RBAC is allow-only and cannot subtract from an inherited `*/read`. The remaining levers are narrowing subscription RBAC (an IT-wide decision) or a workspace in a different subscription. This is the condition the policy reversal rests on, so it stays in scope |
| **An erasure request arrives and nobody has done one** | purge is asynchronous and per-table; rehearse once before it is needed, as #138 does for rotation |
| Returned-id capture inflates records on paged reads | cap the ids (100) and record `recordsFetched` / `idsRecorded` / `truncated`, so a capped record reports its real magnitude — and, when the pager stopped early, says the total is unknown rather than implying `recordsFetched` was all of them |
| The withdrawn PII rule is reinstated by a later reader who sees "no PII" as obviously correct | the reversal and its reasoning are recorded in `CLAUDE.md` and here; it was a deliberate trade, not an oversight |

## Decisions taken (2026-09-17, @searledan)

Recorded so they are not re-litigated:

| Question | Decision |
|---|---|
| May the audit trail hold customer personal data? | **Yes** — the previous no-PII rule is withdrawn. Arguments in full; response bodies still excluded |
| What is the acceptance criterion? | *this user* called *this tool* and accessed/modified/deleted data for *these customers* |
| Primary audit mechanism | **Tool call**, with arguments and returned record ids — not the upstream path, which names customers only on get-by-id |
| Bulk reads | Record returned ids, capped, with `recordsFetched` / `idsRecorded` / `truncated`. Not accepted as a gap — but the *matching* total is unknowable once the pager truncates, so the record says so rather than guessing |
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
