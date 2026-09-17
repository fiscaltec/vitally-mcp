# Entra app registration — Vitally MCP (#107)

The app registration that **replaced** the Auth0 client + Resource Server pair at the #108 cutover.
Live on **both** targets — staging since 2026-09-03, production since 2026-09-16. Auth0 is retained as
the rollback only. It is **both** the shared OAuth client and the API resource, because that is what the
proxy's `SharedClientId` / `SharedClientSecret` model expects — which is also why its appId is a
valid `aud` as well as the `client_id`.

Provisioned 2026-09-02 via `az` / Microsoft Graph; captured as-built in `infra/terraform/entra.tf`.
Serving staging since 2026-09-03 and **production since 2026-09-16** — both targets now sign in through this registration. The cutover code was merged and deployed first, and ran from the moment it shipped — OIDC discovery, the proxy and the `resource` validation were all live on production before the flip. What stayed inert was the Entra **posture**: with `OAuth__UpstreamResourceScope` empty the proxy relayed `resource` exactly as it always had. Setting that and the other four `OAuth__*` variables is what the flip did.

| | |
|---|---|
| Display name | `Vitally MCP` |
| appId (→ `OAuth:SharedClientId`) | `c3812e7d-a413-4169-b57e-803326611ba3` |
| App objectId | `568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4` |
| SP objectId | `7904188d-4b34-4651-bf0f-6941fbcf6a8b` |
| App ID URI (→ `OAuth:Audience`) | `https://vitally.fiscaltec.com` — **no trailing slash** |
| Exposed scope | `mcp.access` (`fbdb4f49-d2f6-43b3-91a6-475117ab874b`) |
| Redirect URIs | `https://vitally.fiscaltec.com/oauth/callback`, `https://vitally-staging.fiscaltec.com/oauth/callback` |
| Token version | `2` |
| Sign-in gate | `appRoleAssignmentRequired = true` + nine department groups, assigned **directly** |
| Client secret | `entra-mcp-client-secret` in `vitally-prod-kv-uksouth`, expires 2027-03-01 |

**`OAuth:Audience` and `OAuth:Resource` must NOT match under Entra.** `Audience` is the App ID URI
above (no slash, because Entra refuses to register one); `Resource` stays
`https://vitally.fiscaltec.com/` (with the slash, because that is what Claude Code normalises to and
publishes back). Anyone "tidying" these into agreement breaks either token validation or the RFC 9728
document. `OAuthOptions.IsResourceIndicatorAllowed` tolerates exactly one slash of difference, which
is what lets both forms name one resource.

## Two ordering constraints

Both were hit for real; each fails the *whole* write atomically, so a failure leaves nothing
half-applied.

1. **`requestedAccessTokenVersion = 2` before `identifierUris`.** The tenant's
   `defaultAppManagementPolicy` enables `identifierUris.uriAdditionWithoutUniqueTenantIdentifier`,
   which would force the `https://fiscaltec.com/{guid}` shape; the app is exempt only via
   `excludeAppsReceivingV2Tokens`. Create the app with the version set, *then* PATCH the URI.
2. **The scope must exist before it can be pre-authorised.** Referencing a scope id in
   `api.preAuthorizedApplications` in the same PATCH that creates the scope fails with
   `InvalidValue … has a Permission Id that cannot be found in the AppPermissions sets`. Two PATCHes.

## Gate 1 — sign-in assignment

`appRoleAssignmentRequired = true` restricts sign-in to assigned principals, exactly as
`FISCAL IT Auth0` does. The same **nine** department groups are assigned:

Product · IT & Security · Development · Data Science · Project Management · Customer Operations ·
Executive Leadership Team · Customer Account Management · Service Delivery

> ⚠️ **This list has drifted from `FISCAL IT Auth0`'s twice, and the second time is the one that
> matters.**
>
> | Found | Missing from the Entra app | Consequence at cutover |
> |---|---|---|
> | 2026-09-03 | `Development Department` (15 members) | `AADSTS50105` — total loss of access |
> | 2026-09-15 | `Data Science Department` (2 members) | the same |
>
> Both groups had working access at the time, via **both** mechanisms — assigned directly to
> `FISCAL IT Auth0` for Gate 1, *and* nested in `sg-vitally-readers` for Gate 2. Those are separate
> and the distinction matters here: the nesting is what gave them a tier, the direct assignment is
> what let them sign in at all, and it is only the second that the Entra app was missing.
>
> The first occurrence was fixed by correcting this list *and* this warning — **and it happened again
> twelve days later anyway.** The reason is not forgetfulness: `ACCESS.md` told admins to assign a
> department to `FISCAL IT Auth0`, and named no other app, so both departments were onboarded exactly
> as documented. That procedure is corrected in the same change as this note; #134 tracks a check so
> the next divergence is caught by something other than a document.
>
> **Derive this list from the live assignments, never from a document**, and compare the two apps
> immediately before any cutover *or rollback* — parity matters in both directions while both exist:
>
> ```bash
> export MSYS_NO_PATHCONV=1
> set -o pipefail   # without this a failed `az rest` is masked by `sort` and the check reports OK
> AUTH0=3dff0dcd-ebe1-496e-b47f-e5e4e736a548; ENTRA=7904188d-4b34-4651-bf0f-6941fbcf6a8b
> page() { az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$1/appRoleAssignedTo?\$top=999" -o json; }
> fetch() { local j; j=$(page "$1") || return 1; [ "$(echo "$j" | jq -r '."@odata.nextLink" // ""')" = "" ] || { echo "PAGINATED — this check does not follow @odata.nextLink" >&2; return 1; }; echo "$j" | jq -r '.value[]|[.principalType,.principalId,.principalDisplayName,.id]|@tsv' | sort > "$2"; }
> rc=0
> if ! fetch "$AUTH0" gate-auth0.txt || ! fetch "$ENTRA" gate-entra.txt || [ ! -s gate-auth0.txt ] || [ ! -s gate-entra.txt ]; then
>   echo "NOT ASSESSED — a lookup failed or returned nothing"; rc=1
> else
>   if diff <(cut -f1,2 gate-auth0.txt | sort) <(cut -f1,2 gate-entra.txt | sort); then echo "PARITY OK"; else echo "DRIFT — the ids above differ; grep them in gate-*.txt for names"; rc=1; fi
>   # Parity is agreement, NOT correctness: the same unintended group added to both apps diffs
>   # clean. Assert the live set against the expected one recorded in Terraform. Run from the
>   # repo root. The sed anchors on `^variable` deliberately: an unanchored pattern also matches
>   # the `for_each = var.entra_gate1_group_object_ids` line further down and reopens the range
>   # over the `gate1` resource, pulling in its all-zero `app_role_id` as a tenth id — which
>   # would report a healthy nine-group gate as UNEXPECTED MEMBERSHIP.
>   sed -n '/^variable "entra_gate1_group_object_ids"/,/^}/p' infra/terraform/entra.tf | grep -ioE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}' | sort > gate-expected.txt
>   if [ ! -s gate-expected.txt ]; then echo "EXPECTED SET NOT READ — run from the repo root; not asserting membership"; rc=1
>   elif diff gate-expected.txt <(cut -f2 gate-entra.txt | sort); then echo "MEMBERSHIP OK — matches infra/terraform/entra.tf"
>   else echo "UNEXPECTED MEMBERSHIP — the live gate differs from entra.tf (< expected, > live). Either a group was added outside the process, or entra.tf was not updated when one was onboarded."; rc=1; fi
>   if [ "$(awk -F'	' '$1!="Group"{n++} END{print n+0}' gate-auth0.txt gate-entra.txt)" != "0" ]; then
>     echo "NON-GROUP ASSIGNMENT PRESENT — Gate 1 must stay group-driven. Delete each with the command printed for it:"
>     for pair in "gate-auth0.txt:$AUTH0" "gate-entra.txt:$ENTRA"; do awk -F'	' -v sp="${pair##*:}" '$1!="Group"{print "  "$1" "$3":"; print "    az rest --method delete --url \"https://graph.microsoft.com/v1.0/servicePrincipals/"sp"/appRoleAssignedTo/"$4"\""}' "${pair%%:*}"; done
>     rc=1
>   else
>     echo "every assignment is a Group — Gate 1 is group-driven"
>   fi
> fi
> [ "$rc" -eq 0 ]   # final status: 0 only if parity held AND every row was a Group
> ```
>
> Three things in there are deliberate, and each replaces a version of this snippet that looked like
> a check and was not:
>
> - **`set -o pipefail`, plus the `-s` emptiness guards.** Without them a failed `az rest` is masked
>   by `sort`'s exit status, both files come out empty, and `diff` of two empty files succeeds — so
>   the check reports `PARITY OK` during exactly the outage or credential failure in which it cannot
>   assess anything. It now says **NOT ASSESSED**, which is the only honest answer.
> - **It refuses to answer off a truncated page.** `appRoleAssignedTo` is a paginated collection and
>   `az rest` does not follow `@odata.nextLink`, so a future estate with more assignments than fit in
>   one page would compare partial lists and could report parity while missing a group — or a `User`
>   row. `$top=999` makes that unreachable in practice; the explicit `nextLink` check makes it
>   impossible rather than unlikely, which is the standard the rest of this snippet has had to be
>   held to four times now.
> - **Parity and correctness are two questions, and it now asks both.** Comparing the apps to each
>   other catches the drift that has happened twice, but it passes happily when the *same* wrong
>   group sits on both — which is what onboarding a department to the wrong tier looks like, and
>   the diff would call it healthy. So the live set is also asserted against
>   `entra_gate1_group_object_ids` in `infra/terraform/entra.tf`, which is the recorded expected
>   nine. That makes the Terraform capture load-bearing for this check rather than decorative:
>   onboarding a department means updating it, which the runbook already tells you to do.
> - **It tests the invariant, not the one violation that has occurred.** Gate 1 is meant to hold
>   *nine Group rows and nothing else*, so the check rejects every row whose `principalType` is
>   not `Group` — not just `User`. The `User` row this runbook records really happened (admin
>   consent created one), but a `ServicePrincipal` assignment would grant an application
>   sign-in and would have been reported as "no user assignments", passing. Checking for the
>   failure you have seen rather than the property you require is how the next one gets through.
> - **It exits non-zero on every bad outcome**, including a non-`Group` row, and **accumulates**
>   that across both checks. Three separate ways this went wrong while being written, all of which
>   reported health while finding a problem: `… || echo "DRIFT"` succeeds whatever it found;
>   `grep -c '^User' # must be 0` is inverted, because `grep` exits **0 when it finds** a match, so
>   the unsafe result was the successful one; and a second `if` after the first silently overwrites
>   `$?`, so a real DRIFT followed by a clean user check exits 0. **And it prints a whole delete
>   command per row rather than a bare assignment id**, because the two files come from two
>   different service principals: `grep -h` discards which file a row came from, so a reader
>   pasting the id into the delete command further down this runbook — which hardcodes the
>   `Vitally MCP` SP — would target the wrong app for anything found in `gate-auth0.txt`, and
>   leave the `User` row in place having been told it was removed. Hence the `rc` accumulator and the
>   closing `[ "$rc" -eq 0 ]`, which sets the status without exiting an interactive shell.
> - **It re-sorts after projecting.** Strictly redundant — a whole-line sort is already dominated by
>   type and id, which precede the name — but it makes rename-safety a local property of the
>   comparison rather than something a reader has to derive from field order, and it survives someone
>   later reordering the `jq` projection.
> - **It compares object ids only** (`cut -f1,2` — type and id), keeping the display name *and the
>   assignment id* in the files for reading but out of the comparison. The assignment id is what the
>   deletion command below needs, so a `User` finding is actionable without a second Graph query. Two reasons: Entra display names are not unique, so
>   a name-based comparison reads as parity while Gate 1 points at a different group entirely; and
>   Graph snapshots `principalDisplayName` onto the assignment when it is created, so renaming a
>   department makes the two apps disagree on the name while the same principal is assigned to both —
>   a false DRIFT that could block a legitimate cutover.
> - **It runs an actual `diff`.** The first version printed two sorted lists consecutively for a
>   human to eyeball, in the document whose entire subject is that this difference gets missed.
>
> Once Auth0 is retired that cross-check disappears, so the list here becomes the only record —
> another reason not to retire it early.

**Assign groups directly — never the `sg-vitally-*` tier groups.** The Entra app-assignment gate
honours only *direct* members of an assigned group; nesting does not grant sign-in. Assigning
`sg-vitally-*` here would admit only their two direct members. This is why the department groups are
both assigned here **and** nested inside `sg-vitally-*`.

The two gates are separate mechanisms and should stay that way:

| | Question it answers | Mechanism |
|---|---|---|
| Gate 1 | may this person sign in at all? | direct department assignment on this app |
| Gate 2 | which tier of tools do they get? | `sg-vitally-*` membership, resolved **transitively** by `GraphGroupPermissionResolver` via Graph using the caller's object id — `oid` when present, else the trailing GUID of an Auth0-shaped `sub`, retained for the rollback window |

Gate 2 is IdP-independent — it survives the cutover untouched.

### Onboarding a new department

```bash
export MSYS_NO_PATHCONV=1   # Git Bash mangles the URL path otherwise
ENTRA_SP=7904188d-4b34-4651-bf0f-6941fbcf6a8b   # Vitally MCP
AUTH0_SP=3dff0dcd-ebe1-496e-b47f-e5e4e736a548   # FISCAL IT Auth0
GROUP=<new-group-object-id>

# PHASE 1 — survey BOTH apps before touching either.
# Surveying and mutating in one pass is how a half-applied onboarding happens: if the first lookup
# fails and the loop carries on, the group gets assigned to the second app only, which is precisely
# the one-sided drift this procedure exists to prevent.
# `assignments` holds the same no-truncation standard as the parity check above: `az rest` does not
# follow `@odata.nextLink`, and a truncated page would read as "not assigned" — so this would POST a
# duplicate, or leave the apps inconsistent, in the one state it cannot actually assess.
assignments() { local j; j=$(az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$1/appRoleAssignedTo?\$top=999" -o json) || return 1; [ "$(echo "$j" | jq -r '."@odata.nextLink" // ""')" = "" ] || { echo "PAGINATED on $1 — this snippet does not follow @odata.nextLink" >&2; return 1; }; echo "$j"; }
# Returns the existing assignment id, or the string `none`. Empty means the LOOKUP failed, which is a
# different answer and must not be read as absence. The id comes from this same survey rather than a
# second call, so what phase 2 prints is what phase 1 actually saw.
id_for() { local j; j=$(assignments "$1") || return 1; echo "$j" | jq -r --arg g "$GROUP" '[.value[]|select(.principalId==$g)|.id]|.[0] // "none"'; }
entra_id=$(id_for "$ENTRA_SP") || entra_id=""
auth0_id=$(id_for "$AUTH0_SP") || auth0_id=""
if [ -z "$entra_id" ] || [ -z "$auth0_id" ]; then
  echo "LOOKUP FAILED — NEITHER app has been changed. Fix access and re-run."
  false
else
  # PHASE 2 — both states known, so a failure here is a real failure rather than an unknown.
  rc=0
  for pair in "$ENTRA_SP:$entra_id" "$AUTH0_SP:$auth0_id"; do
    SP=${pair%:*}; existing=${pair##*:}
    if [ "$existing" != "none" ]; then
      # Surface the existing id: the Terraform import step below needs it, and the common reason to
      # re-run this is repairing drift, where at least one app is already assigned.
      echo "already assigned on $SP — id: $existing"
    else
      echo "{\"principalId\":\"$GROUP\",\"resourceId\":\"$SP\",\"appRoleId\":\"00000000-0000-0000-0000-000000000000\"}" > body.json
      if az rest --method post --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignedTo" --headers "Content-Type=application/json" --body @body.json --query id -o tsv; then
        echo "  ^ assignment id on $SP — needed for the import block below"
      else
        echo "FAILED on $SP — the apps are now OUT OF PARITY; fix before stopping"; rc=1
      fi
    fi
  done
  rm -f body.json
  [ "$rc" -eq 0 ]
fi
```

**It is idempotent on purpose**, so it doubles as the drift repair: a group already assigned to one
app is skipped rather than re-POSTed, because Graph refuses a duplicate assignment and a naive loop
would report the apps out of parity in the very state it had just fixed.

**And it covers both apps on purpose — do not reduce it to one.** `Vitally MCP` gates **both** targets since
the 2026-09-16 flip. `FISCAL IT Auth0` gates nothing for this server any more — it is retained purely as
the rollback, and its assignments matter only because a rollback would start using them again. Omitting either locks the
new department out of that one, silently, until it is the app being used — which is exactly how
Development and Data Science were missed, both times by following a procedure that named one app.

Then, **in the same change**:

1. Add the group to `entra_gate1_group_object_ids` in `infra/terraform/entra.tf`.
2. Add a matching `import` block to `infra/terraform/imports.tf`, using **the id printed for
   `$ENTRA_SP`** — the loop prints one per app, and the Auth0 one does not belong to
   `azuread_app_role_assignment.gate1`, which models only the Entra registration — the `gate1` resource is `for_each` over that map, so a map entry without an
   import reads as unmanaged and a plan would propose creating an assignment that already exists:

   ```hcl
   import {
     to = azuread_app_role_assignment.gate1["<Department name>"]
     id = "7904188d-4b34-4651-bf0f-6941fbcf6a8b/appRoleAssignment/<assignment-id>"
   }
   ```

   (If you lost the id, re-read it — keyed on the group's **object id**, not its display name:
   Graph snapshots `principalDisplayName` when the assignment is created, so a renamed department
   returns nothing and duplicate names return the wrong row. The service-principal id is spelled out
   because this command is meant to work pasted on its own:

   ```bash
   export MSYS_NO_PATHCONV=1
   GROUP=<the department group's object id>
   if j=$(az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/7904188d-4b34-4651-bf0f-6941fbcf6a8b/appRoleAssignedTo?\$top=999" -o json) \
      && [ "$(echo "$j" | jq -r '."@odata.nextLink" // ""')" = "" ]; then
     echo "$j" | jq -r --arg g "$GROUP" '[.value[]|select(.principalId==$g)|.id]|.[0] // "NOT ASSIGNED"'
   else
     echo "NOT ASSESSED — the lookup failed or the collection is paginated; this is not 'no assignment'" >&2
     false
   fi
   ```
   )
3. Run the parity check above.

Verify at any time:

```bash
export MSYS_NO_PATHCONV=1
if j=$(az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/7904188d-4b34-4651-bf0f-6941fbcf6a8b/appRoleAssignedTo?\$top=999" -o json) \
   && [ "$(echo "$j" | jq -r '."@odata.nextLink" // ""')" = "" ]; then
  echo "$j" | jq -r '.value[]|[.principalDisplayName,.principalType]|@tsv'
else
  echo "NOT ASSESSED — the lookup failed or the collection is paginated" >&2
  false
fi
```

The result should be **nine Group rows and nothing else**. A `User` row is drift — see below.

**Every read of `appRoleAssignedTo` in this runbook guards `@odata.nextLink`, and that is a rule
rather than a flourish.** `az rest` does not follow it, so a truncated page is indistinguishable from
a short one: this survey would silently under-report the gate, and the onboarding loop above would
read a group on a later page as unassigned. Counting nine rows here does not protect you — the count
is the thing that would be wrong. `$top=999` makes truncation unreachable at nine groups; the guard
is what keeps the answer honest if that ever stops being true.

## Admin consent

```bash
az ad app permission admin-consent --id c3812e7d-a413-4169-b57e-803326611ba3
```

Grants tenant-wide (`AllPrincipals`) consent for Graph `openid profile email offline_access` plus the
app's own `mcp.access`, so users see no consent screen. Together with
`api.preAuthorizedApplications` naming the app itself, this is the equivalent of Auth0's
`skip_consent_for_verifiable_first_party_clients`.

> ⚠️ **This command also assigns the consenting user to the app.** With
> `appRoleAssignmentRequired = true`, running it created an individual `User` assignment for
> `dsearle.adm` that nobody asked for. It was removed, because Gate 1 must be group-driven or
> "who can sign in?" stops being answerable from group membership. **Re-check the assignment list
> after every re-consent** and delete any `User` row:
>
> ```bash
> az rest --method delete --url "https://graph.microsoft.com/v1.0/servicePrincipals/7904188d-4b34-4651-bf0f-6941fbcf6a8b/appRoleAssignedTo/<assignment-id>"
> ```

Doing consent via Graph directly (`POST /oauth2PermissionGrants`) avoids the side effect, but that
call is blocked by the Claude Code auto-mode classifier, so `az ad app permission admin-consent` is
the practical route.

## Client secret

Stored as **`entra-mcp-client-secret`** in `vitally-prod-kv-uksouth`, referenced by the Container App
through the user-assigned managed identity (`Key Vault Secrets User`) — the same pattern as
`vitally-shared`.

| | |
|---|---|
| Created | 2026-09-02 |
| Entra credential `keyId` | `e17e0e9e-d4c7-46b4-87c1-afe98a5bc111` |
| Expires | **2027-03-01T13:18:59Z** — 180 days (both the Entra credential and the Key Vault secret) |
| Key Vault secret | `entra-mcp-client-secret`, tagged `purpose=OAuth:SharedClientSecret`, `appId`, `issue=107` |

**180 days is the convention** — note *convention*, not an enforced rule. `infra/terraform/scan/run.py`
warns when an **enabled Key Vault secret that has an expiry** comes within **30 days** of it, and its
Teams card repeats the wording ("rotate per the 180-day standard"). Both qualifiers are load-bearing:
`run.py` filters on `attributes.enabled` *and* on `exp` being present, so a **secret with no expiry
set is not covered at all** — it can never come within 30 days of a date it does not have. The
scanner also knows nothing about Entra, so the app registration credential's own `endDateTime` is
outside its scope entirely; that is why the expiry is set on the Key Vault secret as well. Nothing in the vault followed it until 2026-09-02, when both secrets were
brought into line: `vitally-shared` was moved from 2027-08-31 to **2027-02-14** (180 days from its
own creation on 2026-08-18, not from the day it was changed), and this secret was **reissued** at
180 days.

> **The Entra credential's `endDateTime` is immutable.** Shortening it is not an edit — it is a new
> credential plus a delete of the old one. Add the replacement, store it, confirm it, and only then
> `az ad app credential delete` the superseded `keyId`; the reverse order is a self-inflicted outage.
> The first credential (`a7d71deb-…`, 12 months) was created and then replaced this way, which is why
> the `keyId` above is not the one in the earlier commit message.

> ⚠️ **An expired Key Vault secret cannot be read at all** — Key Vault refuses `GET` once `exp`
> passes, it does not merely warn. So each expiry date above is a hard outage date: on 2027-02-14
> the server stops being able to fetch the Vitally API key, and on 2027-03-01 the token exchange
> stops working. The scanner's 30-day warning is the whole safety margin.

> ⚠️ **Set the expiry on the Key Vault secret, not only on the Entra credential.** The scheduled
> scanner (`infra/terraform/scan/run.py`, a Container Apps Job) alerts on the **Key Vault secret's**
> `attributes.exp` and knows nothing about Entra. A secret stored without one is invisible to it, so
> the rotation deadline passes in silence and the server starts failing token exchanges. This secret
> was initially stored without an expiry for exactly that reason; it now carries one matching the
> credential, so the alert fires 30 days out.
>
> ```bash
> az keyvault secret set-attributes --vault-name vitally-prod-kv-uksouth \
>   --name entra-mcp-client-secret --expires "2027-03-01T13:18:59Z"
> ```

> **Resolve the egress IP with `curl -4`.** This workstation egresses over IPv6 by default now, and
> Key Vault network ACLs accept IPv4 only — an unqualified `curl https://ifconfig.me` returns an IPv6
> address and `az keyvault network-rule add` fails outright with *"Invalid IPv4 address"*. That one is
> at least loud; the quiet failure is the stale-address case below.
>
> **The vault's data plane is private-endpoint only** (`publicNetworkAccess: Disabled`,
> `networkAcls.defaultAction: Deny`, no IP or VNet rules). A `secret set` from a workstation fails
> with `ForbiddenByConnection` — a *network* denial, independent of RBAC, so Global Administrator
> and `Key Vault Secrets Officer` both make no difference.
>
> **An IP rule on its own does NOT open it.** `publicNetworkAccess: Disabled` short-circuits the ACL
> entirely — you still get *"Public network access is disabled and request is not from a trusted
> service nor via an approved private link"*. Reaching the data plane from outside the VNet needs
> **both** switches: add the IP rule *and* flip `publicNetworkAccess` to `Enabled` while keeping
> `defaultAction: Deny`, so the endpoint resolves publicly but refuses every address but yours.
> Revert both afterwards. Policy on this vault is audit-only, so nothing blocks or auto-reverts it,
> but it does register in the compliance audit.
>
> Always drive it from a script with a cleanup `trap`, so the window closes even if a step in the
> middle fails. The alternative that weakens nothing is a host inside the VNet, or a VPN with
> private-DNS resolution to the private endpoint.
>
> **Resolve the egress IP inside that same script — never reuse one from earlier in the session.**
> It is a dynamic ISP address and it rotated mid-session here (`86.179.212.113` → `51.148.41.71`),
> so a second window opened for the stale address and the data plane stayed unreachable. That
> failure is silent in the sense that it looks exactly like slow propagation, so the abort guard
> below matters: it stops before creating a credential it cannot store.

```bash
VAULT=vitally-prod-kv-uksouth
MYIP=$(curl -4 -s https://ifconfig.me)   # inside the script, every time; -4 because KV ACLs are IPv4-only
...
az keyvault secret list --vault-name "$VAULT" -o none 2>/dev/null \
  || { echo "unreachable — aborting before creating anything"; exit 1; }
```

**Create and store in one go, so the value is never printed.** Entra shows a secret value once;
capturing it into a variable that is then echoed puts a live credential into terminal scrollback and
into any session transcript.

```bash
export MSYS_NO_PATHCONV=1
APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4
END=$(date -u -d '+180 days' '+%Y-%m-%dT%H:%M:%SZ')   # 180-day standard

# 1. create — write the value straight to a restricted temp file, never to stdout
umask 077
cat > pw.json <<EOF
{"passwordCredential":{"displayName":"vitally-mcp container app ($(date -u +%Y-%m)) — expires $END","endDateTime":"$END"}}
EOF
az rest --method post --url "https://graph.microsoft.com/v1.0/applications/$APP/addPassword" \
  --headers "Content-Type=application/json" --body @pw.json \
  --query secretText -o tsv > secret.txt

# 2. store
az keyvault secret set --vault-name vitally-prod-kv-uksouth \
  --name entra-mcp-client-secret --file secret.txt --output none

# 3. set the KV secret expiry to match — the scanner keys off THIS, not the Entra credential
az keyvault secret set-attributes --vault-name vitally-prod-kv-uksouth \
  --name entra-mcp-client-secret --expires "$END" --output none

# 4. destroy the local copies
rm -f secret.txt pw.json
```

### Rotation

Same four steps, then **delete the superseded credential** by `keyId` once the Container App has
picked up the new value (it caches Key Vault reads for `Vitally:SecretCacheDuration`, default 5
minutes):

```bash
APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4   # Vitally MCP application objectId
az ad app credential list --id $APP --query "[].{keyId:keyId,name:displayName,expires:endDateTime}" -o table
az ad app credential delete --id $APP --key-id <old-keyId>
```

Overlap the two rather than deleting first — Entra allows multiple secrets, and a delete-then-create
sequence is a self-inflicted outage.

### Consider retiring the secret entirely

The Container App already has a user-assigned managed identity. A **federated identity credential**
naming that identity would remove the secret, and with it the rotation commitment. It needs
`/oauth/token` to send `client_assertion` instead of `client_secret`, so it is a code change, not
configuration — worth raising after #108 rather than during it.

## What this app does *not* have, deliberately

- **No app roles and no groups claim.** Entitlement is the live Graph lookup (Gate 2), which needs
  only `oid`. App roles would be a second, direct-membership-only mechanism that silently disagrees
  with the transitive one.
- **No application (app-only) Graph permissions.** The server never calls Graph as the app through
  this registration; `GroupMember.Read.All` belongs to the Container App's managed identity, which is
  a separate object in `identity.tf`.
- **No implicit grant.** Authorization code + PKCE only.

## The cutover (#108) — code deployed 2026-09-03, flip complete 2026-09-16

Config-only, as designed, and now **fully applied**. **Staging** was flipped on 2026-09-03; **production**
followed on 2026-09-16 once the five `OAuth__*` variables were set there. The code had been deployed to both
and running on both throughout — the OIDC discovery, the proxy and the `resource` validation were live
on production before the flip. What the flip changed was the **posture**: with
`OAuth__UpstreamResourceScope` empty the proxy had been relaying `resource` exactly as it always had,
which is why the deploy was a no-op and setting the variables was the whole change. Both targets now
terminate `resource` and name the API by scope; the relay is the rollback posture.

The variable table and the rollback live in **CLAUDE.md**, under *The Auth0 → Entra cutover (#108)
and its rollback*; the per-target values are in `infra/terraform/variables.tf`. What belongs here is
what the cutover **learned about this registration**, since that is what the next person changing it
needs — and those lessons come from the staging flip and the validation against the live tenant, so
they held for the staging flip and still hold now that production has followed.

### `resource` had to be dropped, not reshaped — and the reason recorded earlier was wrong

#105 shipped validation only, relaying the parameter because on Auth0 the relay was the only thing
binding the token audience. The note here predicted the relay would fail under Entra "because Entra
matches `resource` exactly against a registered identifier and will not accept the trailing-slash
form". Driving `/oauth2/v2.0/authorize` against the live tenant on 2026-09-02 showed something
different, and more absolute:

```
error=invalid_target&error_description=AADSTS9010010: The resource parameter provided in the
request doesn't match with the requested scopes.
```

| Request | Result |
|---|---|
| no `resource`, scope `openid profile` | 200, sign-in page |
| `resource=https://vitally.fiscaltec.com/` | **400** |
| `resource=https://vitally.fiscaltec.com` (the exact App ID URI) | **400** |
| `resource=` anything unregistered | **400** |
| `scope=openid mcp.access` (bare, unqualified) | 200, sign-in page |

So the v2 endpoint cross-checks `resource` against `scope` — it is not comparing against
`identifierUris` at all, and **the trailing slash is beside the point**. Dropping the parameter is
required rather than tidier; normalising the slash would not have helped.

The last row is the one worth remembering, because it fails the other way round: a bare `mcp.access`
is *accepted* at `/authorize` and resolved against Microsoft Graph, so the flow completes and hands
back a token for the wrong resource. A scope on a custom API must carry the App ID URI prefix, which
is why both metadata documents advertise `https://vitally.fiscaltec.com/mcp.access` in
`scopes_supported`.

### The proxy names this API by scope now

`OAuth:UpstreamResourceScope = https://vitally.fiscaltec.com/mcp.access`. That single value is what
switches the proxy from relaying `resource` to terminating it — `resource` is still validated, and a
value we never published is still refused with `invalid_target`. It is merged into `scope` on
`/oauth/token` as well as `/oauth/authorize`, which matters on a **refresh**: Entra issues the new
access token for whatever resource `scope` names.

### `aud` is the appId, not the App ID URI

`requestedAccessTokenVersion = 2`, and a v2 access token names the resource application's **appId
GUID**. `OAuthOptions.ValidAudiences` therefore accepts both that and the App ID URI (which is what a
v1 token would carry), so `OAuth:Audience` remaining the URI is correct and not the whole story.

One consequence to be aware of rather than alarmed by: an **ID** token for this app carries the same
`aud`, because one registration is both client and resource — so one presented as a bearer token
passes audience validation. It is not an escalation (same client, same user, and entitlement is still
the live Graph lookup), and narrowing it would mean requiring `scp`, a provider-specific rule in a
deliberately provider-neutral class. Tracked separately rather than bundled into the cutover.

### The Auth0 side is retained, not removed

Its client, both Resource Servers and the `Vitally MCP claims` Action stay in place until production
has soaked on Entra — deleting them early turns a one-command rollback into an outage. The tenant's
*Resource Parameter Compatibility Profile* is now irrelevant to this server and can be left alone.
