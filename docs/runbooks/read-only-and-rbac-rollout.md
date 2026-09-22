# Read-only deployments & per-user RBAC rollout

> **Status, 2026-09-15.** The per-user RBAC rollout below is **complete and live**:
> `Authorization__LiveGroupCheck=true` with all three group ids set, on production and staging.
> Steps 1–3 are a record of how it was done, not work outstanding.
>
> `Authorization__ReadOnly` is **`true` on staging and unset on production**. Unset it only for the
> tier-enforcement test, and put it back after.
>
> ⚠️ **A recreate does NOT bring staging up guarded.** `containerapps-staging.tf` records the
> setting but nothing applies it — `terraform apply` is never run here — so a fresh app starts on
> the application default `false`, writable against the shared production Vitally tenant. Set it and
> verify it after every recreate; see the verification command below.

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

**It is recorded in `infra/terraform/containerapps-staging.tf`** so the guard lives somewhere other
than one person's memory — which is exactly what is absent at a fresh spin-up, when nobody is
thinking about it. Unsetting it for a tier test is the deliberate act; having it on is the default
the recipe describes.

⚠️ **The file sets it; nothing applies the file.** `containerapps-staging.tf` does carry an
`Authorization__ReadOnly = "true"` env block (grep for it; line numbers move) — but `infra/terraform/` is a back-filled
as-built capture and **`terraform apply` is never run here**; staging is stood up through
`deploy.yml` plus the `az containerapp` commands in CLAUDE.md. So it is a recipe to follow and
keep in step, not a mechanism that enforces anything, and a fresh app comes up on the application
default of `false`. **After any recreate, set the variable and then verify it:**

```bash
CA=vitally-staging-ca-uksouth; RG=vitally-prod-rg-uksouth
# EVERY revision taking traffic, not just the newest: a single unguarded one is enough for
# requests to reach it. `for REV in $(az …)` on its own is NOT this check — a failed or empty
# listing runs the body zero times and exits 0, so an Azure outage or a missing role would
# print nothing and read exactly like the "unguarded" case the text below describes.
if ! REVS=$(az containerapp revision list -n $CA -g $RG \
     --query '[?properties.trafficWeight > `0`].name' -o tsv) || [ -z "$REVS" ]; then
  echo "NOT ASSESSED — could not list traffic-bearing revisions"; false
else
  rc=0
  for REV in $REVS; do
    if V=$(az containerapp revision show -n $CA -g $RG --revision "$REV" \
         --query "properties.template.containers[0].env[?name=='Authorization__ReadOnly'].value|[0]" -o tsv); then
      printf '%s\t%s\n' "$REV" "${V:-<unset>}"
      [ "$V" = "true" ] || rc=1
    else
      echo "NOT ASSESSED — could not read $REV"; rc=1
    fi
  done
  [ "$rc" -eq 0 ] && echo "GUARDED — every traffic-bearing revision has Authorization__ReadOnly=true"
  [ "$rc" -eq 0 ]
fi
```

Empty output means **unguarded**, not "defaulted to safe" — the application default is `false`.
It reads the revision *serving traffic* rather than the desired template, which would report the
new value while the previous writable revision was still answering requests.

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
   > That app is **`Vitally MCP`**, and since #156 it is the only one — there was briefly a second
   > that had to be kept identical by hand, and naming the wrong one here caused two departments to
   > be onboarded one-sidedly. See `ACCESS.md` (canonical) for the full model.
   >
   > ⚠️ Its assignment list is now the **only** record of who can sign in, and nothing detects an
   > omission. A department left off it cannot reach the server.
3. **There is no token-claim tier, and nothing mints one.** `Authorization:CustomPermissionsClaim`
   still exists as a setting, but with `LiveGroupCheck=true` — set on every deployed target — the
   order is **fresh Graph → stale Graph → deny**, and `ToolAuthorizer` has no route from the live
   mode to the claim mode, including when the resolver is absent, which denies. #108 removed that
   fall-through and #156 removed the provider hook that used to mint the claim, so the tier is gone
   twice over. What covers a Graph outage is `LiveGroupStaleSeconds` (default 1 h) serving each
   caller's last known-good tier; past that, calls are denied.

   Do not reinstate a claim fall-through as a safety net — one that can only ever deny reads like a
   working fallback and behaves like a silent denial, which is why #108 removed it.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log — keyed by the caller's Entra **object id** from the `oid` claim.
   An Entra token always carries one, so a record keyed on anything else means an unexpected token
   shape; only then is the raw subject used. See `CallerIdentity` and #127. Expect **`LogToolCallDenied`**, not `LogDenied`: the SDK
   authorisation filter rejects an out-of-tier call at the per-tool `[Authorize]` checkpoint, before
   `VitallyService.SendAsync` runs, and `LogDenied` is only reached from inside `SendAsync`. Looking
   for the wrong event is indistinguishable from the denial not being audited at all.
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.
