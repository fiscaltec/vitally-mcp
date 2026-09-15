# Read-only deployments & per-user RBAC rollout

> **Status, 2026-09-15.** The per-user RBAC rollout below is **complete and live**:
> `Authorization__LiveGroupCheck=true` with all three group ids set, on production and staging.
> Steps 1–3 are a record of how it was done, not work outstanding.
>
> `Authorization__ReadOnly` is **`true` on staging and unset on production**. Staging's is also in
> `containerapps-staging.tf`, so a recreate brings it up guarded rather than open; unset it only for
> the tier-enforcement test and put it back after.

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

**It is set in `infra/terraform/containerapps-staging.tf`, not applied by hand.** Staging is
recreated from that capture, so a guard that lived only in someone's memory would be absent from
every fresh spin-up — which is exactly when nobody is thinking about it. Unsetting it for a tier test
is the deliberate act; having it on is the default the recipe gives you.

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
3. **Auth0 token claim — retained configuration, NOT a fallback.** The Auth0 post-login Action
   `Vitally MCP claims` maps Entra group membership to the `vitally:*` permissions and writes them
   to the namespaced `Authorization:CustomPermissionsClaim`. **Nothing consults that claim on any
   deployed target, and nothing has since #125 deployed.** With `LiveGroupCheck=true` — set
   everywhere — the order is **fresh Graph → stale Graph → deny**, and `ToolAuthorizer` has no
   route from the live mode to the claim mode, including when the resolver is absent, which
   denies. So during a Graph outage this claim authorises nobody. What covers an outage is
   `LiveGroupStaleSeconds` (default 1 h) serving each caller's last known-good tier; past that,
   calls are denied.

   It is kept solely so an Auth0 rollback restores a working *sign-in* path, and it goes with the
   rest of the Auth0 configuration when that is retired. Do not reinstate it as a safety net —
   a fall-through that can only ever deny reads like a working fallback and behaves like a silent
   denial, which is why #108 removed it.

   > **If it is ever revived, it has a nested-group defect to fix first.** The Action reads
   > `event.user.group_ids` / `event.user.groups` from the Auth0 Entra (waad) connection, and those
   > are **direct** memberships. Every tier but `sg-vitally-admins` is granted by nesting, so the
   > claim would under-grant almost everyone. Either emit transitive security-group memberships on
   > the Entra app registration feeding the waad connection, or have the Action resolve
   > `transitiveMembers` via Graph — which is exactly what step 2's live check already does.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log — keyed by the caller's Entra **object id** (the `oid` claim, not
   `sub`; see `CallerIdentity` and #127). Expect **`LogToolCallDenied`**, not `LogDenied`: the SDK
   authorisation filter rejects an out-of-tier call at the per-tool `[Authorize]` checkpoint, before
   `VitallyService.SendAsync` runs, and `LogDenied` is only reached from inside `SendAsync`. Looking
   for the wrong event is indistinguishable from the denial not being audited at all.
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.
