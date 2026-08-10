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

### Prerequisite check (blocking)

Key Vault and ACR both have **public network access disabled** behind private endpoints. Whether
a new Container App can reach them depends on the Container Apps environment being
VNet-injected. **This must be verified before any resource is provisioned.** If the environment
is not VNet-injected, staging cannot fetch the Vitally secret or pull its image, and this layer
must be redesigned — likely via a separate secret with its own access path.

Verify with `az cli` as `dsearle.adm` against `vitally-prod-rg-uksouth`, not via the Azure MCP
connector.

### Resources

Provisioned with **`az cli`, scripted and committed** alongside the existing
`docs/runbooks/`. Terraform in `infra/terraform/` is adopted-but-drifting while the live
resources are managed manually by `az cli`; running `terraform apply` against shared state would
attempt to reconcile production drift at the same time and is therefore excluded from this work
entirely. Staging may be brought into Terraform afterwards via an `import` block, as the other
resources were.

- Container App `vitally-staging-ca-uksouth` in the **existing** Container Apps environment,
  consumption plan, `minReplicas=0`
- Its own **user-assigned managed identity**, granted `AcrPull` on the registry,
  `Key Vault Secrets User` on the vault, and Microsoft Graph `GroupMember.Read.All`
- Reuses the existing ACR, environment, Log Analytics workspace and Key Vault
- **Default ACA FQDN** (`<app>.<region>.azurecontainerapps.io`) — no custom domain, no DNS or
  certificate work. `OAuth:PublicBaseUrl` is set to match.

### Configuration

| Setting | Value | Rationale |
|---|---|---|
| `Authorization__ReadOnly` | `true` | **Hard-wired, not a variable.** Only guard against mutating real customer data. |
| `Authorization__LiveGroupCheck` | `true` | Avoids needing the tenant-wide Auth0 Action; also exercises the Graph path. |
| `OAuth__Authority` | existing Auth0 tenant | Same tenant, different API. |
| `OAuth__Audience` | new staging Resource Server identifier | Isolated from production. |
| `OAuth__SharedClientId` / `SharedClientSecret` | new staging native client | Staging callback only. |
| `OAuth__PublicBaseUrl` | staging ACA FQDN | Metadata documents must match the real origin. |
| `Vitally__Region` | `EU` | Matches production. |
| `Vitally__KeyVaultUri` | existing vault | Subject to the prerequisite check above. |

### Auth0

A **new Resource Server and new native client** in the existing `fiscal-it.uk.auth0.com` tenant,
with the staging `/oauth/callback` as its only allowed callback. No production Auth0 object is
modified.

Critically, **the post-login Action is not touched**. It is tenant-wide and runs on every login
including production, so extending it to cover staging would defeat the isolation. Setting
`LiveGroupCheck=true` removes the need: permissions resolve from current Entra group membership
through Graph rather than from the Action's token claim.

### Sequence

1. Prerequisite check passes.
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
- **Load and concurrency.** The 95-tool `tools/list` payload is measured but not load-tested.

## Risks

| Risk | Mitigation |
|---|---|
| Private-endpoint reachability from a new Container App | Blocking prerequisite check before any provisioning |
| Graph `GroupMember.Read.All` grant needs Global Administrator | PIM activation; the `infra-pims` skill covers this |
| Orphaned staging Auth0 client / managed identity | Teardown step in the committed runbook |
| Staging cost | Consumption plan, `minReplicas=0`, deleted after the soak |
| `tools/list` wire-format assumption | Wire property names confirmed against the SDK before assertions are written |

## Follow-up

`CLAUDE.md` is stale on two points and should be corrected as part of the implementation plan: it
states SDK 1.3.0 GA (actual: 2.0.0) and MCP protocol 2025-06-18 (actual: 2026-07-28 supported).
