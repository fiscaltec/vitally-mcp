# Nested (transitive) Entra group support for Vitally RBAC — design

**Date:** 2026-07-08
**Status:** Approved

## Problem

Users whose Vitally access comes via a **department group nested inside**
`sg-vitally-{readers,editors,admins}` are denied access, even though the department group is
a member of the relevant `sg-vitally-*` group.

Root cause: `GraphGroupPermissionResolver.ResolveMemberGroupsAsync` checks membership with the
Microsoft Graph **`/groups/{id}/members`** relationship, which returns **direct members only**.
A user who belongs to a nested department group is a *transitive* member of `sg-vitally-*`, not a
direct one, so the `$filter=id eq '{oid}'` query matches nothing, no permission tier is granted,
and the call is denied.

Production runs `Authorization__LiveGroupCheck=true` (verified on
`vitally-prod-ca-uksouth` in `vitally-prod-rg-uksouth`), so the live Graph path is the effective
authorisation path and the fix in this repo resolves the issue for production. The Auth0
post-login Action is the fallback path (used only when the live Graph lookup fails); it must be
made transitive too so both paths agree.

## Scope

Both authorisation paths, plus all live documentation.

## Part 1 — Repo change (primary fix)

`VitallyMcp/GraphGroupPermissionResolver.cs`:

- In `ResolveMemberGroupsAsync`, change the Graph relationship from `/members` to
  **`/transitiveMembers`** (line ~104). Everything else is unchanged:
  - Same application permission: `GroupMember.Read.All` (no new Graph grant required —
    `/transitiveMembers` is covered by the same permission as `/members`).
  - Same advanced-query shape: `$count=true` + `$select=id` + `$filter=id eq '{oid}'` +
    `ConsistencyLevel: eventual` (all supported by `/transitiveMembers`).
- Fully backward-compatible: direct members are still returned; nested members now match too.
- Update the comments that assert direct-membership is sufficient:
  - Class summary (currently describes the `/members` vs `checkMemberGroups` trade-off).
  - The `ResolveMemberGroupsAsync` method comment ("Direct membership is sufficient here
    because the sg-vitally-* groups are assigned to users directly") — replace with the
    transitive-resolution rationale.

No change to tiering (cumulative `admin ⊇ editor ⊇ reader` stays), configuration, or any other
call path — the change is localised to one URL and its comments.

## Part 2 — Test (new)

`VitallyMcp.Tests/GraphGroupPermissionResolverTests.cs` (no resolver test exists today). Use a
mocked `HttpMessageHandler` and a stub `TokenCredential`. Cover:

- **URL regression guard:** the request path targets `/transitiveMembers`, not `/members`.
- **Tier mapping via a nested match:** a member hit on the editor group yields `{read, write}`;
  admin yields `{read, write, delete}`; no hit yields an empty set (deny).
- **Fail-degraded:** a non-success Graph response causes the resolver to return `null` (so
  `ToolAuthorizer` falls back to the token claim rather than locking everyone out).
- **Cache:** a second call for the same user within the TTL does not re-hit Graph.

## Part 3 — Auth0 Action (fallback path)

The token-claim path runs when the live Graph lookup is unavailable. To make it honour nesting,
the Auth0 post-login Action must resolve **transitive** membership of the three group ids
(e.g. via Graph `checkMemberGroups` / `transitiveMemberOf`) rather than reading the direct
`groups` claim, then map matches to the cumulative `vitally:*` permissions written to the
namespaced permissions claim.

This change is executed in the Auth0 tenant via the **Auth0 MCP** (being initialised), not merged
into this repo. It will be captured in the RBAC runbook as the authoritative description.

## Part 4 — Documentation

Live docs to update (historical `docs/superpowers/specs|plans/*` are point-in-time records and
are **not** rewritten):

- **`CLAUDE.md`** — correct the `ToolAuthorizationOptions` / live-group-check description: it
  currently says the resolver uses Graph `checkMemberGroups`; the code lists group members and
  will now use `/transitiveMembers`. State that membership is evaluated **transitively** (nested
  groups honoured).
- **`ACCESS.md`** — add an admin-facing note that a user can be granted a tier either by direct
  membership of `sg-vitally-*` or via a group nested inside it (department groups work).
- **`README.md`** — clarify the `LiveGroupCheck` / `*GroupId` rows: membership is evaluated
  transitively (nested groups supported).
- **`docs/runbooks/read-only-and-rbac-rollout.md`** — note nested-group support in the live-check
  step, and record the Auth0 Action's transitive-membership requirement (Part 3) as the
  authoritative fallback-path description.

## Out of scope (YAGNI)

- No change to permission tiers, config surface, or caching semantics.
- No broader refactor of `ToolAuthorizer` or the claim-fallback logic.

## Verification

- `dotnet test VitallyMcp.sln` green, including the new resolver tests.
- Manual: a user in a department group nested inside `sg-vitally-editors` can read and write;
  removing the nesting revokes within the cache window.
