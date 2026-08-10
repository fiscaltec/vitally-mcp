# Validating MCP SDK 2.0 / spec 2026-07-28 adoption without affecting production — design

**Date:** 2026-08-10
**Status:** Approved

## Problem

Dependabot PR #80 moved the server to `ModelContextProtocol` **2.0.0**, which implements the
**2026-07-28** MCP specification. The package bump landed, but none of the new capabilities were
adopted, and several are directly relevant to this server. A gap analysis identified six changes
worth making (see [Changes under validation](#changes-under-validation)).

Two of those six sit on the OAuth path — the same path that gates production sign-in for every
FISCAL employee. A mistake there locks all users out simultaneously, because the
`WWW-Authenticate` / protected-resource-metadata pointer is how MCP clients *discover* the
authorisation server in the first place.

There is currently no non-production surface on which to exercise any of this. Production is a
single Container App (`vitally-prod-ca-uksouth`) in single-revision mode at
`https://vitally.fiscaltec.com`, and there is exactly one Vitally tenant holding real customer
data. This design defines how the changes get validated before they reach it.

## Scope

The validation strategy only. The implementation of the six changes is a separate plan; this
document defines the gates that plan must pass through.

### Changes under validation

| # | Change | Risk class |
|---|---|---|
| 1 | `AddAuthorizationFilters()` + per-caller `tools/list` filtering | Auth path |
| 2 | `WWW-Authenticate` `resource_metadata` pointer, path-scoped metadata endpoint, `scopes_supported` | Auth path |
| 3 | `ListToolsResult.TimeToLive` + `CacheScope` | Low |
| 4 | MRTR elicitation (`InputRequiredException`) on destructive tools | Medium |
| 5 | `Idempotent` / `OpenWorld` tool annotations | Low |
| 6 | SDK 2.0.0 → 2.1.0 bump | Low |

## Approach

Three validation layers, cheapest and fastest first. Each layer has an explicit gate; a change
does not progress until its layer's gate passes.

The layers are deliberately unequal. **Layer 1 carries the load** — it is where correctness is
actually established, because it is free, repeatable and runs on every PR. Layer 3 is expensive
and manual, so it exists to confirm only what Layer 1 structurally cannot reach: real Auth0
token issuance, real Entra group resolution through Graph, real Key Vault access via managed
identity, and the behaviour of a real MCP client.

### Sequencing

All six changes land on one feature branch behind Layer 1 tests, then deploy to staging as a
**single push**. Both auth-path changes are covered by distinct Layer 1 tests, so a staging
failure will already have been narrowed by test results before staging is inspected.

**Fallback:** if the staging soak fails in a way the tests did not predict, split into two
pushes — changes 1 and 2 (auth path) separately, then 3–6 together — to recover attribution.

Staging is provisioned and baselined against current `main` **before** the branch is deployed to
it. This is a required step, not an optional one: staging differs from production in its Auth0
objects, managed identity, FQDN and `PublicBaseUrl`, so without a known-good baseline a
connection failure is indistinguishable between "the metadata rework is broken" and "staging's
Auth0 client is misconfigured". Both present identically — a client that will not connect.

## Layer 1 — in-process integration tests

Extends the existing `WebApplicationFactory<Program>` harness in `VitallyMcp.Tests` rather than
introducing a new one. The harness already drives real JSON-RPC over the streamable-HTTP
transport against the real composition root (see `ReadOnlyToolsListTests`,
`OAuthProxyEndpointsTests`).

New test classes:

- **`AuthorizationFilterToolsListTests`** — the load-bearing test. Registers a test
  authentication scheme via `WithWebHostBuilder` so a synthetic reader / editor / admin
  `ClaimsPrincipal` can be injected, then asserts `tools/list` contents by tier: a reader must
  not see `Create_*` / `Update_*` / `Delete_*`; an editor must see create and update; an admin
  must see all. Per-caller filtering is *proven here*, not on staging.
- **`ResourceMetadataDiscoveryTests`** — unauthenticated `POST /mcp` must return **401** and
  carry a `WWW-Authenticate` header containing a `resource_metadata` parameter. Both
  `/.well-known/oauth-protected-resource` and the `/mcp`-suffixed variant must return the same
  document, and it must include `scopes_supported`.
- **`ToolsListCachingTests`** — asserts the **serialised wire property names**, not the CLR
  property names. The exact JSON names must be confirmed against the SDK before the assertions
  are written; do not assume `ttlMs` / `cacheScope` from the article.
- **`ElicitationConfirmationTests`** — a destructive tool returns an input-required result when
  unresolved and proceeds once resolved, against a mocked Vitally upstream.

Existing suites must stay green, in particular `ReadOnlyToolsListTests`, `ToolAuthorizerTests`,
`VitallyServiceAuthorizationTests` and `OAuthProxyEndpointsTests`.

**Harness constraint:** configuration read at composition time in `Program.cs` (`OAuth:NoAuth`,
`Authorization:ReadOnly`) must be supplied via **environment variables**, because
`WebApplicationFactory` intercepts `IHostBuilder` after top-level statements have already run,
making `ConfigureAppConfiguration` too late. This is documented in `ReadOnlyToolsListTests` and
applies to any new factory.

**Gate:** `dotnet test VitallyMcp.sln` green **and zero new build warnings**. The warning check
is what catches an accidental dependency on a deprecated API (`MCP9004` legacy SSE, `MCP9005`
stateless elicit/sample/roots, `MCP9006` stateful-only options). The build is currently clean at
zero warnings, so any new warning is attributable to this work.

## Layer 2 — local container

`docker build` from the existing `Dockerfile`, run with `OAuth__NoAuth=true`,
`Vitally__DevelopmentApiKey` and `Authorization__ReadOnly=true`. Driven first by MCP Inspector,
then by Claude Code pointed at `http://localhost:5099/mcp`.

Confirms what only a real client can show:

- the negotiated protocol version is genuinely `2026-07-28`
- `Mcp-Method` / `Mcp-Name` are present on requests
- the 95-tool `tools/list` carries its TTL and cache scope
- the elicitation prompt actually renders in a client, rather than merely being emitted

**Gate:** protocol version `2026-07-28` negotiated; `tools/list` returns the expected tool count;
one read tool round-trips against real Vitally; the elicitation prompt appears on a destructive
tool.

Validates nothing about authentication, by construction — `NoAuth=true` bypasses it, and
`StartupGuards.EnsureSafeAuthConfig` refuses `NoAuth` alongside a Key Vault URI, so this layer
uses `DevelopmentApiKey`.

## Layer 3 — staging Container App

### Prerequisite check — verified PASSED (2026-08-10)

Key Vault and ACR both have **public network access disabled** (confirmed:
`publicNetworkAccess: Disabled` on both) behind private endpoints, so a new Container App can
only reach them from inside the VNet. Verified with `az cli` as `dsearle.adm` against
subscription `IT-Production` (`282207c6-4107-47fa-9d4e-b2fa9b3066cb`):

- `vitally-prod-cae-uksouth` **is VNet-injected** — `vnetConfiguration.infrastructureSubnetId`
  resolves to the `snet-app` subnet (`10.80.0.64/27`) of `vitally-prod-vnet-uksouth`.
  `internal: false`, so ingress stays public while egress traverses the VNet.
- Both `privatelink.vaultcore.azure.net` and `privatelink.azurecr.io` have VNet links
  (`link-vault`, `link-acr`) to **that same VNet**.

A new app in this environment therefore resolves the vault and registry to their private
endpoint addresses, and can fetch the Vitally secret and pull its image. **Layer 3 is viable as
designed** — no separate secret or access path is required.

### Resources

Provisioned with **`az cli`, scripted and committed** alongside the existing
`docs/runbooks/`. Terraform in `infra/terraform/` is adopted-but-drifting while the live
resources are managed manually by `az cli`; running `terraform apply` against shared state would
attempt to reconcile production drift at the same time and is therefore excluded from this work
entirely. Staging may be brought into Terraform afterwards via an `import` block, as the other
resources were.

- Container App `vitally-staging-ca-uksouth` in the **existing** Container Apps environment
  `vitally-prod-cae-uksouth`, Consumption workload profile, `minReplicas=0`
- Its own **user-assigned managed identity** `vitally-staging-id-uksouth`, granted `AcrPull` on
  the registry, `Key Vault Secrets User` on the vault, and Microsoft Graph
  `GroupMember.Read.All`
- Reuses the existing environment, ACR (`vitallyproducruksouth`), Log Analytics workspace
  (`vitally-prod-law-uksouth`) and Key Vault (`vitally-prod-kv-uksouth`)
- **Default ACA FQDN** (`<app>.<region>.azurecontainerapps.io`) — no custom domain, no DNS or
  certificate work. `OAuth:PublicBaseUrl` is set to match.

Verified production resource names (`az resource list -g vitally-prod-rg-uksouth`, 2026-08-10):
Container App `vitally-prod-ca-uksouth`, environment `vitally-prod-cae-uksouth`, identity
`vitally-prod-id-uksouth`, ACR `vitallyproducruksouth`, Key Vault `vitally-prod-kv-uksouth`,
VNet `vitally-prod-vnet-uksouth`. Production runs image tag `v4.1.10` in **Single** revision
mode with `minReplicas=1`, `maxReplicas=3`.

### Configuration

Mirrors the verified production environment variables except where noted. Production values were
read from `vitally-prod-ca-uksouth` on 2026-08-10.

| Setting | Value | Rationale |
|---|---|---|
| `Authorization__ReadOnly` | `true` | **Hard-wired, not a variable.** Deliberate deviation — production does not set this (defaults `false`). Only guard against mutating real customer data. |
| `Authorization__LiveGroupCheck` | `true` | **Matches production**, which runs `true` (verified). Same permission-resolution path, so no fidelity gap. |
| `Authorization__ReaderGroupId` / `EditorGroupId` / `AdminGroupId` | same three Entra group ids as production | Permission tiers must resolve identically to live. |
| `OAuth__Authority` | `https://fiscal-it.uk.auth0.com/` | Same tenant as production, different API. |
| `OAuth__Audience` | new staging Resource Server identifier | Isolated from production's `https://vitally.fiscaltec.com/`. |
| `OAuth__Resource` | same as staging `Audience` | Production sets this explicitly; staging must too. |
| `OAuth__SharedClientId` / `SharedClientSecret` | new staging native client; secret via a Container App secret ref | Staging callback only. Production stores its secret as secret ref `oauth-shared-client-secret`. |
| `OAuth__PublicBaseUrl` | staging ACA FQDN | Metadata documents must match the real origin. |
| `OAuth__NoAuth` | `false` | Matches production. Auth is the point of this layer. |
| `Vitally__Region` | `EU` | Matches production. |
| `Vitally__KeyVaultUri` | `https://vitally-prod-kv-uksouth.vault.azure.net/` | Same vault; reachable per the verified prerequisite. |
| `AZURE_CLIENT_ID` | staging identity's client id | Selects the user-assigned identity for `DefaultAzureCredential`. |

### Deviations from production

Stated explicitly so the fidelity of the soak is known rather than assumed:

| Deviation | Why | Consequence |
|---|---|---|
| `Authorization__ReadOnly=true` | One live Vitally tenant; no sandbox | Write paths unexercised on staging (see [Not covered](#not-covered)) |
| `minReplicas=0` vs production `1` | Cost | Staging cold-starts; irrelevant to correctness, but do not read staging latency as representative |
| Separate Auth0 API + client | Isolation | Token `aud` differs; the Action's namespaced claim may be absent |
| Default ACA FQDN vs custom domain | Avoids DNS/cert work | `PublicBaseUrl` differs, which is itself worth exercising since the metadata documents are built from it |

Everything else — region, vault, authority, group ids, `LiveGroupCheck`, `NoAuth` — matches
production.

### Auth0

A **new Resource Server and new native client** in the existing `fiscal-it.uk.auth0.com` tenant,
with the staging `/oauth/callback` as its only allowed callback. No production Auth0 object is
modified.

Critically, **the post-login Action is not touched** — and this costs nothing, because production
runs `LiveGroupCheck=true`, meaning permissions already resolve from current Entra group
membership through Graph rather than from the Action's token claim. Staging using the same
setting is therefore *more* faithful to live, not less.

The Action's claim is production's **fallback**, used only when the Graph lookup fails. Extending
the Action to cover the staging audience would buy fallback-path fidelity in exchange for editing
a tenant-wide object that runs on every production login — a poor trade for a path that only
engages on Graph failure. It is therefore explicitly out of scope (see
[Not covered](#not-covered)).

Worth confirming in Auth0 while provisioning: if the Action is not audience-gated it will run for
staging logins anyway and emit the `https://vitally.fiscaltec.com/permissions` claim regardless,
in which case the fallback path is incidentally covered too. Either outcome is acceptable; no
edit is made in response.

### Sequence

1. Prerequisite check — **already complete**, passed 2026-08-10 (see above).
2. Provision staging app, identity, role grants and Auth0 objects.
3. **Baseline** — deploy the current `main` image. Gate: `/health` 200; unauthenticated `/mcp`
   401; a real MCP client completes the OAuth flow and lists tools.
4. Deploy the feature-branch image. Gate: all of the above, plus `tools/list` differs by
   caller tier, the elicitation prompt appears on a destructive tool, and the TTL is present.
5. Teardown — `az containerapp delete`, remove the identity and role grants, delete the Auth0
   Resource Server and client.

Teardown is recorded in the runbook rather than left to memory, since an orphaned Auth0 client
and an unused managed identity are both standing security debt.

## Blast radius

- The production Container App is **never modified** — no image swap, no revision-mode change,
  no environment-variable edit. Single-revision mode is left alone.
- **No `terraform apply` runs** at any point.
- Staging cannot mutate Vitally data: `ToolAuthorizer` checks `ReadOnly` *before* the
  `Enabled` / `NoAuth` gate, so the denial holds even with RBAC disabled.
- Production deployment happens only after the staging soak passes, through the existing
  `deploy.yml`, which retains its health/smoke check and automatic rollback.

**`deploy.yml` needs no change.** Its smoke test asserts unauthenticated `/mcp == 401`; change 2
keeps the status at 401 and only adds a header, so the assertion stays valid.

## Not covered

Stated explicitly so these are accepted rather than assumed:

- **Vitally write paths** beyond mocked-HTTP unit tests. There is one live tenant and staging is
  pinned read-only, so create/update/delete are never exercised against the real API in any
  layer.
- **Entra revocation timing** under the 60-second `LiveGroupCacheSeconds` window. Testable on
  staging by removing oneself from a group, but slow; a manual spot-check, not a gate.
- **The Auth0 Action claim fallback path.** Production uses it only when the Graph lookup fails.
  Covering it would require editing a tenant-wide Action on the live login path; the trade is not
  worth it. The primary Graph path is fully covered.
- **Load and concurrency.** The 95-tool `tools/list` payload is measured but not load-tested.
  Note staging runs `minReplicas=0` against production's `1`, so it is not a valid latency
  comparison in any case.

## Risks

| Risk | Mitigation |
|---|---|
| ~~Private-endpoint reachability from a new Container App~~ | **Retired** — verified 2026-08-10, the environment is VNet-injected and both private DNS zones are linked to it |
| Graph `GroupMember.Read.All` grant needs Global Administrator | PIM activation; the `infra-pims` skill covers this |
| Orphaned staging Auth0 client / managed identity | Teardown step in the committed runbook |
| Staging cost | Consumption plan, `minReplicas=0`, deleted after the soak |
| `tools/list` wire-format assumption | Wire property names confirmed against the SDK before assertions are written |

## Follow-up

`CLAUDE.md` is stale on two points and should be corrected as part of the implementation plan: it
states SDK 1.3.0 GA (actual: 2.0.0) and MCP protocol 2025-06-18 (actual: 2026-07-28 supported).
