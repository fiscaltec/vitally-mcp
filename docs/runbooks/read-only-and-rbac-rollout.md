# Read-only deployments & per-user RBAC rollout

> **Status, 2026-09-15.** The per-user RBAC rollout below is **complete and live**:
> `Authorization__LiveGroupCheck=true` with all three group ids set, on production and staging.
> Steps 1–3 are a record of how it was done, not work outstanding.
>
> `Authorization__ReadOnly` is **unset on both targets**. That is correct for production and correct
> for staging *while a tier test is running* — see below for when staging should have it on.

## `Authorization__ReadOnly` — what it is for now

Set `Authorization__ReadOnly=true` on the Container App revision. Effect:
- All create/update/delete tool calls are denied (`ToolAuthorizer`, before the RBAC/NoAuth gate),
  audited via `LogDenied`.
- `tools/list` advertises only read tools (no `Create_*`/`Update_*`/`Delete_*`).
- Independent of `Authorization:Enabled` and of any Entra-group setup — a guaranteed lock that does
  not consult Microsoft Graph, a token, or group membership.

> **It is deployment-wide and overrides per-caller filtering.** `tools/list` is *also* filtered per
> caller by tier (56 read / 81 editor / 93 admin). With `ReadOnly` on, readers, editors and admins
> all see the same 56 read tools. That is the switch working, not per-caller filtering breaking.

### Its one live use: guarding staging

**Staging reads the production `vitally-shared` key.** There is one Vitally tenant, and although
Vitally does allow additional API keys to be created, nothing in its REST API documentation offers a
**read-scoped** key — so a second key would be revocable and separately attributable but would carry
the same write access. Confirmed 2026-09-15; revisit if Vitally ever ships scoped keys, because a
read-only key at the boundary would be strictly better than this switch.

Until then, staging's write and delete tools mutate **real customer data**, and the only thing
standing between that and an accident is `Authorization__ReadOnly`.

| Staging is… | `Authorization__ReadOnly` |
|---|---|
| up for validation work generally | **`true`** |
| running the tier-enforcement test specifically | **unset** — the test has to see the write tools to prove a reader is denied one |
| torn down | n/a, and this is the strongest control of the three |

Toggling it rolls a new revision, which also empties the in-process permission cache — harmless on
staging, and worth knowing before doing it anywhere else.

### What it is *not* for

**Not an incident lever during a Microsoft Graph outage.** Setting it requires a new revision, a new
revision is a new process, and the permission cache is in-process (`AddMemoryCache`). With Graph
unreachable a fresh process can resolve nobody, so every caller is denied everything — reads
included — regardless of this flag. The restart is a harder outage than the state you were reaching
for. If you want the server to stop, use `az containerapp ingress disable` and mean it.

**Not the pre-RBAC stopgap it was built as.** It was added in June 2026 (PR #53) after CS reported
suspected accidental deletions, when there was no per-user tiering at all and every authenticated
user could do everything. Tiering now covers that: delete is `sg-vitally-admins`, and the CS-facing
departments are editors. Do not reinstate the old "keep CS-facing deployments read-only until RBAC
lands" posture — RBAC landed.

## Per-user RBAC rollout — how it was done

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
   > *authenticating* is gated by an enterprise app with `appRoleAssignmentRequired = true`, which
   > honours only **direct** members of assigned groups — nested groups do **not** grant sign-in. So
   > each department that should have access must be **directly assigned** to that app as well as
   > nested into its `sg-vitally-*` tier.
   >
   > ⚠️ **There are two such apps — `FISCAL IT Auth0` and `Vitally MCP` — and a department must be
   > assigned to BOTH** while the #108 migration keeps one as the rollback path. Naming only the
   > Auth0 app here is what caused two departments to be onboarded one-sidedly; each would have lost
   > access entirely at the cutover. See `ACCESS.md` (canonical) for the full model, and
   > `docs/runbooks/entra-app-registration.md` for the parity check.
3. **Auth0 (alternative/auxiliary — the token-claim fallback):** a post-login Action
   (`Vitally MCP claims`) maps Entra group membership to the `vitally:*` permissions, written to the
   namespaced `Authorization:CustomPermissionsClaim`. This path runs only when the live Graph lookup
   is unavailable. **Nested-group caveat:** the Action maps `event.user.group_ids` /
   `event.user.groups` supplied by the Auth0 Entra (waad) connection; those are **direct** memberships
   unless Entra is configured to emit transitive security-group memberships in the token. So for the
   fallback path to honour nested groups too, either enable the transitive/"all (security) groups"
   groups claim on the Entra app registration feeding the waad connection, or have the Action resolve
   transitive membership via Graph. The **live check (step 2) already handles nesting** and is the
   production path.

   > ⚠️ **This is no longer a fallback, and has not been since #125 deployed.** With
   > `LiveGroupCheck=true` — set on every deployed target — the order is **fresh Graph → stale Graph
   > → deny**. `ToolAuthorizer` never consults a token claim in that mode, so the Action described in
   > this step cannot authorise anyone during a Graph outage. What covers an outage is
   > `LiveGroupStaleSeconds` (default 1 h) serving each caller's last known-good tier; past that the
   > call is denied. Read the rest of this step as a description of configuration that still exists
   > for the rollback window, not as a path that runs.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log (`LogDenied`, keyed by the caller's Entra **object id** — the
   `oid` claim, not `sub`; see `CallerIdentity` and #127).
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.
