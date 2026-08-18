# Read-only deployments & per-user RBAC rollout

## Deploy read-only (immediate safety net)

Set `Authorization__ReadOnly=true` on the Container App revision. Effect:
- All create/update/delete tool calls are denied (`ToolAuthorizer`, before the RBAC/NoAuth gate),
  audited via `LogDenied`.
- `tools/list` advertises only read tools (no `Create_*`/`Update_*`/`Delete_*`).
- Independent of `Authorization:Enabled` and of any Entra-group/Auth0 setup — a guaranteed lock.

> **This is deployment-wide, and it overrides per-caller filtering.** Since the SDK 2.1.0 adoption,
> `tools/list` is *also* filtered per caller by permission tier (56 read / 81 editor / 93 admin).
> `Authorization__ReadOnly=true` strips every destructive tool for **everyone regardless of tier**,
> so with it on, readers, editors and admins all see the same 56 read tools. Do not read that as
> per-caller filtering being broken — it is the read-only switch doing its job.

Use this for CS-facing deployments until per-user RBAC (below) is rolled out and verified.

## Per-user RBAC rollout (finer-grained; out of the application repo)

The server-side RBAC backstop already exists (`ToolAuthorizer` maps HTTP verb → `vitally:read` /
`vitally:write` / `vitally:delete`). To grant tiers per user via Entra group membership:

1. **Entra:** create/confirm three security groups (Reader, Editor, Admin); collect their object ids.
2. **App config:** set `Authorization__ReaderGroupId` / `EditorGroupId` / `AdminGroupId` to those ids;
   set `Authorization__LiveGroupCheck=true` (resolves live membership via Microsoft Graph, so
   revocations take effect within the cache window). Requires the managed identity to hold Graph
   `GroupMember.Read.All`. Membership is evaluated **transitively** (Graph `transitiveMembers`), so a
   user who inherits a tier via a **nested/department group** inside an `sg-vitally-*` group is
   authorised — you can assign tiers by nesting groups, not only by adding users directly.
   > **Sign-in gate is separate and direct-only.** Authorising a tier (above) is transitive, but
   > *authenticating* is gated by the **FISCAL IT Auth0** enterprise app, which has
   > `appRoleAssignmentRequired = true` and honours only **direct** members of assigned groups —
   > nested groups do **not** grant sign-in. So each department that should have access must be
   > **directly assigned** to *FISCAL IT Auth0* as well as nested into its `sg-vitally-*` tier.
   > See `ACCESS.md` (canonical) for the full model.
3. **Auth0 (alternative/auxiliary — the token-claim fallback):** a post-login Action
   (`Vitally MCP claims`) maps Entra group membership to the `vitally:*` permissions, written to the
   namespaced `Authorization:CustomPermissionsClaim`. This path runs only when the live Graph lookup
   is unavailable. **Nested-group caveat:** the Action maps `event.user.group_ids` /
   `event.user.groups` supplied by the Auth0 Entra (waad) connection; those are **direct** memberships
   unless Entra is configured to emit transitive security-group memberships in the token. So for the
   fallback path to honour nested groups too, either enable the transitive/"all (security) groups"
   groups claim on the Entra app registration feeding the waad connection, or have the Action resolve
   transitive membership via Graph. The **live check (step 2) already handles nesting** and is the
   production path; the fallback only applies during a Graph outage.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log (`LogDenied`, by `sub`).
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.

## Data-classification gate

Wider rollout remains gated on the pending data-classification review (customer data exposure).
Keep deployments read-only by default until that clears.
