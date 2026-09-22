# Entra app registration — Vitally MCP (#107)

The **sole** identity provider for this server. Live on **both** targets — staging since
2026-09-03, production since 2026-09-16; the previous provider's objects were decommissioned by
#156. It is **both** the shared OAuth client and the API resource, because that is what the
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
| Client secret | Entra credential `keyId` `e17e0e9e-…`, expires 2027-03-01. The vault's `entra-mcp-client-secret` is the **record**; what the app sends is a **copy** in each target's Container App secret — see *Client secret* below before rotating anything |

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

`appRoleAssignmentRequired = true` restricts sign-in to assigned principals. **Nine** department
groups are assigned:

Product · IT & Security · Development · Data Science · Project Management · Customer Operations ·
Executive Leadership Team · Customer Account Management · Service Delivery

> ⚠️ **This list is the ONLY record of who can sign in, and nothing detects an omission.**
>
> Until #156 there was a second app whose assignments had to match this one by hand, and they
> drifted twice:
>
> | Found | Missing from the Entra app | Consequence at cutover |
> |---|---|---|
> | 2026-09-03 | `Development Department` (15 members) | `AADSTS50105` — total loss of access |
> | 2026-09-15 | `Data Science Department` (2 members) | the same |
>
> Both groups had working access at the time via **both** mechanisms — assigned directly to the
> other app for Gate 1, *and* nested in `sg-vitally-readers` for Gate 2. Those are separate and the
> distinction matters: the nesting gave them a tier, the direct assignment let them sign in at all,
> and it was only the second the Entra app was missing.
>
> The first occurrence was fixed by correcting this list *and* the warning — **and it happened again
> twelve days later anyway**, because `ACCESS.md` named the wrong app and both departments were
> onboarded exactly as documented.
>
> **That failure class is now gone**: there is one app, `ACCESS.md` names it, and an omission fails
> immediately and visibly at that department's next sign-in rather than lying dormant until a
> cutover. #134, which proposed automating the two-app parity diff, was closed `not planned` on
> 2026-09-21 — correctly, since it only ever made sense while both apps existed.
>
> What still needs checking is **correctness**, not parity: that the live gate holds exactly the
> nine groups recorded in Terraform, and nothing but groups.
>
> **Derive the live set from Graph, never from a document:**
>
> ```bash
> export MSYS_NO_PATHCONV=1
> set -o pipefail   # without this a failed `az rest` is masked by `sort` and the check reports OK
> ENTRA=7904188d-4b34-4651-bf0f-6941fbcf6a8b
> page() { az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$1/appRoleAssignedTo?\$top=999" -o json; }
> fetch() { local j; j=$(page "$1") || return 1; [ "$(echo "$j" | jq -r '."@odata.nextLink" // ""')" = "" ] || { echo "PAGINATED — this check does not follow @odata.nextLink" >&2; return 1; }; echo "$j" | jq -r '.value[]|[.principalType,.principalId,.principalDisplayName,.id]|@tsv' | sort > "$2"; }
> rc=0
> if ! fetch "$ENTRA" gate-entra.txt || [ ! -s gate-entra.txt ]; then
>   echo "NOT ASSESSED — the lookup failed or returned nothing"; rc=1
> else
>   # Assert the live set against the expected one recorded in Terraform. Run from the repo root.
>   # The sed anchors on `^variable` deliberately: an unanchored pattern also matches the
>   # `for_each = var.entra_gate1_group_object_ids` line further down and reopens the range over
>   # the `gate1` resource, pulling in its all-zero `app_role_id` as a tenth id — which would
>   # report a healthy nine-group gate as UNEXPECTED MEMBERSHIP.
>   sed -n '/^variable "entra_gate1_group_object_ids"/,/^}/p' infra/terraform/entra.tf \
>     | grep -oE '"[0-9a-f-]{36}"' | tr -d '"' | sort > gate-expected.txt
>   if [ ! -s gate-expected.txt ]; then echo "EXPECTED SET NOT READ — run from the repo root; not asserting membership"; rc=1
>   elif diff gate-expected.txt <(cut -f2 gate-entra.txt | sort); then echo "MEMBERSHIP OK — matches infra/terraform/entra.tf"
>   else echo "UNEXPECTED MEMBERSHIP — the live gate differs from entra.tf (< expected, > live). Either a group was added outside the process, or entra.tf was not updated when one was onboarded."; rc=1; fi
>   if [ "$(awk -F'\t' '$1!="Group"{n++} END{print n+0}' gate-entra.txt)" != "0" ]; then
>     echo "NON-GROUP ASSIGNMENT PRESENT — Gate 1 must stay group-driven. Delete each with the command printed for it:"
>     awk -F'\t' -v sp="$ENTRA" '$1!="Group"{print "  "$1" "$3":"; print "    az rest --method delete --url \"https://graph.microsoft.com/v1.0/servicePrincipals/"sp"/appRoleAssignedTo/"$4"\""}' gate-entra.txt
>     rc=1
>   else
>     echo "every assignment is a Group — Gate 1 is group-driven"
>   fi
> fi
> [ "$rc" -eq 0 ]   # final status: 0 only if membership matched AND every row was a Group
> ```
>
> Several things in there are deliberate, and each replaces a version of this snippet that looked
> like a check and was not:
>
> - **`set -o pipefail`, plus the `-s` emptiness guards.** Without them a failed `az rest` is masked
>   by `sort`'s exit status, the file comes out empty, and the comparison succeeds — so the check
>   reports health during exactly the outage or credential failure in which it cannot assess
>   anything. It now says **NOT ASSESSED**, which is the only honest answer.
> - **It refuses to answer off a truncated page.** `appRoleAssignedTo` is a paginated collection and
>   `az rest` does not follow `@odata.nextLink`, so a future estate with more assignments than fit in
>   one page would compare a partial list and could miss a group — or a `User` row. `$top=999` makes
>   that unreachable in practice; the explicit `nextLink` check makes it impossible rather than
>   unlikely.
> - **It asserts against the recorded expected set.** That makes the Terraform capture load-bearing
>   for this check rather than decorative: onboarding a department means updating
>   `entra_gate1_group_object_ids`, which the runbook already tells you to do. Since the second app
>   went, this is the *only* thing standing between an unnoticed edit and a wrong gate.
> - **It tests the invariant, not the one violation that has occurred.** Gate 1 is meant to hold
>   *nine Group rows and nothing else*, so the check rejects every row whose `principalType` is
>   not `Group` — not just `User`. The `User` row this runbook records really happened (admin
>   consent created one), but a `ServicePrincipal` assignment would grant an application sign-in and
>   would have been reported as "no user assignments", passing. Checking for the failure you have
>   seen rather than the property you require is how the next one gets through.
> - **It exits non-zero on every bad outcome**, including a non-`Group` row, and **accumulates** that
>   across both checks. Three separate ways this went wrong while being written, all of which
>   reported health while finding a problem: `… || echo "DRIFT"` succeeds whatever it found;
>   `grep -c '^User' # must be 0` is inverted, because `grep` exits **0 when it finds** a match, so
>   the unsafe result was the successful one; and a second `if` after the first silently overwrites
>   `$?`. Hence the `rc` accumulator and the closing `[ "$rc" -eq 0 ]`, which sets the status without
>   exiting an interactive shell.
> - **It runs an actual `diff`.** The first version printed two sorted lists consecutively for a
>   human to eyeball, in the document whose entire subject is that this difference gets missed.

**Assign groups directly — never the `sg-vitally-*` tier groups.** The Entra app-assignment gate
honours only *direct* members of an assigned group; nesting does not grant sign-in. Assigning
`sg-vitally-*` here would admit only their two direct members. This is why the department groups are
both assigned here **and** nested inside `sg-vitally-*`.

The two gates are separate mechanisms and should stay that way:

| | Question it answers | Mechanism |
|---|---|---|
| Gate 1 | may this person sign in at all? | direct department assignment on this app |
| Gate 2 | which tier of tools do they get? | `sg-vitally-*` membership, resolved **transitively** by `GraphGroupPermissionResolver` via Graph using the caller's `oid` claim |

Gate 2 reads only Graph and the `oid` claim, so it is independent of how sign-in is configured.

### Onboarding a new department

```bash
export MSYS_NO_PATHCONV=1   # Git Bash mangles the URL path otherwise
ENTRA_SP=7904188d-4b34-4651-bf0f-6941fbcf6a8b   # Vitally MCP
GROUP=<new-group-object-id>

# Survey before mutating, so a failed lookup is not mistaken for "not assigned yet" — which would
# turn a read failure into a duplicate POST and an unhelpful Graph error.
assignments() { az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$1/appRoleAssignedTo?\$top=999" -o json; }
existing=$(assignments "$ENTRA_SP" | jq -r --arg g "$GROUP" '[.value[]|select(.principalId==$g)|.id]|.[0] // "none"')

if [ -z "$existing" ]; then
  echo "LOOKUP FAILED — nothing has been changed. Fix access and re-run."
  false
elif [ "$existing" != "none" ]; then
  # Surface the existing id: the Terraform import step below needs it, and the common reason to
  # re-run this is repairing drift, where the group may already be assigned.
  echo "already assigned — id: $existing"
else
  echo "{\"principalId\":\"$GROUP\",\"resourceId\":\"$ENTRA_SP\",\"appRoleId\":\"00000000-0000-0000-0000-000000000000\"}" > body.json
  az rest --method post --url "https://graph.microsoft.com/v1.0/servicePrincipals/$ENTRA_SP/appRoleAssignedTo" \
    --headers "Content-Type=application/json" --body @body.json --query id -o tsv
  echo "  ^ assignment id — needed for the import block below"
  rm -f body.json
fi
```

**It is idempotent on purpose**, so it doubles as the drift repair: a group already assigned is
skipped rather than re-POSTed, because Graph refuses a duplicate assignment.

**One app, and that is the whole point.** This used to loop over two apps that had to stay
identical, and omitting either locked the new department out of that one silently until it was the
app being used — which is exactly how Development and Data Science were missed, both times by
following a procedure that named one app. #156 removed the second app, so there is one place to
assign and the failure is immediate rather than latent.

Then, **in the same change**:

Then, **in the same change**:

1. Add the group to `entra_gate1_group_object_ids` in `infra/terraform/entra.tf`.
2. Add a matching `import` block to `infra/terraform/imports.tf`, using the assignment id printed
   above. The `gate1` resource is `for_each` over that map, so a map entry without an import reads
   as unmanaged and a plan would propose creating an assignment that already exists:

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
`api.preAuthorizedApplications` naming the app itself, this is what suppresses the per-user consent
screen. Note it is **not** what authenticates the token exchange — that is the client secret the
proxy injects — and conflating the two invites removing the wrong setting.

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

Stored as **`entra-mcp-client-secret`** in `vitally-prod-kv-uksouth` — as the **record of the value**.

⚠️ **It is NOT the `vitally-shared` pattern, and the difference decides how rotation works.**
`vitally-shared` really is fetched from Key Vault at runtime by the managed identity, through
`VitallyApiKeyProvider`. This secret is not fetched by the app at all. It was **copied** into a
Container App secret at the flip — `entra-oauth-client-secret` on production,
`oauth-shared-client-secret` on staging — and `OAuth__SharedClientSecret` is a `secretRef` to that
copy. Verified 2026-09-17: `properties.configuration.secrets[].keyVaultUrl` is empty on both apps,
so neither is a Key Vault reference.

```bash
# what the app actually reads — a secretRef, not a vault URI
az containerapp show -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
  --query "properties.configuration.secrets[].{name:name,keyVaultUrl:keyVaultUrl}" -o table
```

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

> ⚠️ **Both dates are hard outage dates, but by two different mechanisms — do not renew the wrong
> object.**
>
> | | 2027-02-14 — `vitally-shared` | 2027-03-01 — the OAuth client secret |
> |---|---|---|
> | What fails | the server cannot fetch the Vitally API key | `/oauth/token` returns `invalid_client`; every sign-in fails |
> | Why | Key Vault refuses `GET` once `exp` passes — it does not merely warn | **Entra** rejects its own expired credential. Key Vault is not in this path at all |
> | Renew | the Key Vault secret | the **Entra credential**, then the Container App copy on every target (see *Rotation*) |
>
> Letting the vault's `entra-mcp-client-secret` expire is therefore not itself an outage — but keep
> its expiry in step anyway, because that is the only thing the scanner can see. The scanner's
> 30-day warning is the whole safety margin for both.

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

⚠️ **This procedure was wrong as previously written, and following it would cause an outage at
the last step.** No rotation has been performed yet — the secret was created 2026-09-02 and has not
been due. It said to wait for the Container App to "pick up" a new Key Vault value within
`Vitally:SecretCacheDuration` and then delete the old credential. Neither half holds: the app never
reads Key Vault for this secret (see *Client secret* above), and `Vitally:SecretCacheDuration`
governs the **Vitally API key** cache in `VitallyApiKeyProvider`, nothing here. Updating Key Vault
alone changes nothing the app sends, so the wait achieves nothing and the delete removes the
credential still in live use — every token exchange then fails `invalid_client`.

#### ⚠️ There is no such thing as a staging-only rehearsal of this — and staging will tell you there is

**One app registration serves both targets.** So the final step — deleting the superseded credential
— *cannot* be rehearsed against staging while production still presents that credential: the delete
is global to the app, and production starts failing `invalid_client` immediately. #138 asked for a
dry-run "end to end, including the delete"; that is not achievable in isolation, and the procedure
below is shaped around the fact rather than pretending otherwise. **Staging is not a rehearsal of
the rotation — it is the first half of it.**

**Worse, a staging rehearsal would silently pass the one step most likely to be got wrong.**
Measured 2026-09-21:

| | `minReplicas` | replicas when idle | What a changed secret does |
|---|---|---|---|
| `vitally-staging-ca-uksouth` | **0** | **0** | The next request cold-starts a replica, which reads the **current** secret. Looks like it rotated with no roll |
| `vitally-prod-ca-uksouth` | **1** | **1** | The warm replica keeps the **old** value in its environment indefinitely. Nothing rotates until it is rolled |

So step 4 below is invisible on staging and mandatory on production — the exact shape of defect that
put the previous version of this section in the repo. Do not conclude from a green staging run that
the roll is optional.

#### The verified mechanics

Checked against the live apps on 2026-09-21 with a throwaway secret (`rotation-probe-138`, added,
updated and removed on staging; no live secret touched), so these are observations rather than
readings of the documentation:

- **`az containerapp secret set` does not create a revision.** Verified for both *adding* a new
  secret and *updating* an existing one: `vitally-staging-ca-uksouth--0000014` was the
  traffic-bearing revision before and after both calls, with an unchanged `createdTime`.
- **`az containerapp revision restart` is the cheapest roll** and is available in the installed CLI.
  Restarting the traffic-bearing revision is enough; a full `az containerapp update` is not needed.
- **Both identifiers for this app are valid and are not interchangeable typos.** `az ad app` accepts
  either, and they resolve to the same registration — objectId `568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4`
  (used below) and appId `c3812e7d-a413-4169-b57e-803326611ba3` (the `OAuth:SharedClientId` in
  `CLAUDE.md`). Don't "align" them; each is correct where it appears.
- **One credential exists today** — `keyId e17e0e9e-d4c7-46b4-87c1-afe98a5bc111`, expiring
  `2027-03-01T13:18:59Z` — and the app has **no federated identity credentials**.

#### The procedure

Ordered so that the irreversible step is last and every target has been proven before it. The
**secret names differ per target** (production `entra-oauth-client-secret`, staging
`oauth-shared-client-secret`) — see *Client secret* above for why; using the wrong one adds a second
unused secret and rotates nothing.

```bash
APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4   # Vitally MCP application objectId
RG=vitally-prod-rg-uksouth
```

#### ⚠️ A successful sign-in does NOT prove the rotation worked

The most dangerous property of this procedure is that **both credentials are valid throughout it**.
That overlap is what makes it safe to abort — and it is also what makes the obvious verification
worthless: if `secret set` silently failed, or a warm revision did not roll, the app happily
authenticates **with the old credential** and the sign-in succeeds. You then reach the delete step
and remove the only credential actually in use, which is precisely the outage the overlap exists to
prevent.

So a `tools/list` proves *some* credential works, never *which*. Two non-destructive checks do
discriminate, and both are needed. Record `NEWHASH` and `STAMP` from script A and compare against
them on each target; only when **A and B both pass on both targets** is the delete safe.

#### The helpers are a checked-in file, not a snippet to retype

**[`docs/runbooks/rotate-helpers.sh`](rotate-helpers.sh)** — `source` it, do not execute it:

```bash
. docs/runbooks/rotate-helpers.sh     # sets APP and RG; defines roll, stored_hash, replicas_are_fresh
```

It is a file rather than a block in this page because the roll and the two checks run *after* a
browser sign-in and often in a fresh shell. Anything defined only inside a runbook code block is
not there when it is needed, and that failure is silent in the worst direction: `secret set`
succeeds, `roll` is undefined, and the target quietly stays on the superseded credential until the
delete takes it down. Sourcing it also re-establishes `APP` and `RG`, which script A set in a shell
that has since exited.

| | What it does |
|---|---|
| `roll <app>` | Restarts **every** traffic-bearing revision; fails closed on a failed *or empty* listing |
| ✅ `verify_target <app> <secret-name> <hash> <stamp> <min-replicas>` | **Use this.** Runs both checks, *compares* the hash, and returns one exit code |
| `stored_hash <app> <secret-name>` | Check A alone — prints the SHA-256 (first 16 chars) of the stored secret. Printing is not comparing |
| `replicas_are_fresh <app> <stamp> <min-replicas>` | Check B alone — every running replica started after `<stamp>`, and at least `<min-replicas>` are running |

⚠️ **Call `verify_target`, not the two checks by hand**, and note the arguments the last two rows
make it easy to get wrong:

- **`<min-replicas>` is safety-critical and defaults to `0`.** Staging takes `0` — scale-to-zero is
  its steady state. **Production takes `1`**: zero running replicas there is never steady state,
  only a check made before `revision restart` finished, and omitting the argument would let that
  pass while proving nothing.
- **`<stamp>` is per-target.** Script A prints `STAMP_STAGING` and `STAMP_PROD` separately, each
  taken *after* its own target's `secret set`. Passing one stamp to both reintroduces a window in
  which a replica that loaded the **old** credential still looks fresh.
- `stored_hash` *prints* a hash; it does not check it. `verify_target` is what compares.

All three **fail closed**: every Azure call's exit status is checked explicitly, because in each
case a failed listing otherwise yields no rows and reads exactly like a clean pass. Invoke by path;
the repo is authored on Windows with `core.filemode=false`, so the executable bit is not relied on
(same convention as `.github/scripts/verify-oauth-metadata.sh`).

#### Script A — create, record, stage (steps 1–3)

Run as a script, not pasted line by line. `set -euo pipefail` and the `trap` are load-bearing:
`az rest … > secret.txt` **creates or truncates the file before `az` runs**, so a failed call leaves
an empty `secret.txt`, and an unguarded `$(cat secret.txt)` would then write an *empty* secret to a
Container App — an outage manufactured by the rotation itself.

```bash
#!/usr/bin/env bash
set -euo pipefail
export MSYS_NO_PATHCONV=1

APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4   # Vitally MCP application objectId
RG=vitally-prod-rg-uksouth
VAULT=vitally-prod-kv-uksouth
END=$(date -u -d '+180 days' '+%Y-%m-%dT%H:%M:%SZ')   # 180-day standard, NOT --years 1

umask 077
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"; unset SECRET' EXIT INT TERM HUP

# 1. create the new credential — appended, never printed to stdout.
#    addPassword APPENDS, which is the overlap this whole procedure depends on.
#    (`az ad app credential reset` without --append REPLACES every credential, and prints the
#     new one to stdout. Do not substitute it.)
cat > "$WORK/pw.json" <<EOF
{"passwordCredential":{"displayName":"vitally-mcp container app ($(date -u +%Y-%m)) — expires $END","endDateTime":"$END"}}
EOF
az rest --method post --url "https://graph.microsoft.com/v1.0/applications/$APP/addPassword" \
  --headers "Content-Type=application/json" --body @"$WORK/pw.json" \
  --query secretText -o tsv > "$WORK/secret.txt"

# GUARD: a truncated/empty file here is the empty-secret outage. Stop before anything consumes it.
[ -s "$WORK/secret.txt" ] || { echo "empty secret — addPassword failed; aborting"; exit 1; }
SECRET=$(tr -d '\r\n' < "$WORK/secret.txt")
NEWHASH=$(printf '%s' "$SECRET" | sha256sum | cut -c1-16)
echo "NEWHASH=$NEWHASH   # record this — it is how you verify each target"

# 2. Key Vault is a RECORD, not a source. Nothing the app sends changes here — but the scanner
#    keys off attributes.exp here and nowhere else, so skipping it means the next deadline
#    passes in silence. Needs the two-switch window above, opened with its own trap (see #154).
az keyvault secret set --vault-name "$VAULT" \
  --name entra-mcp-client-secret --file "$WORK/secret.txt" --output none
# separate call: `secret set` writes a new VERSION and the expiry does not carry over reliably
az keyvault secret set-attributes --vault-name "$VAULT" \
  --name entra-mcp-client-secret --expires "$END" --output none

# 3. set the Container App secret on BOTH targets, but roll ONLY staging.
#    Setting production's secret now is safe and deliberate: its warm replica keeps serving the
#    old value until step 4 rolls it, so nothing changes for users — and it means this script is
#    the only place the secret value is ever needed, so it need not survive into a second shell.
#    ⚠️ The secret NAMES differ per target; using the wrong one adds an unused secret and
#    rotates nothing. See *Client secret* above for why they differ.
#    ⚠️ ONE STAMP PER TARGET, taken AFTER that target's own `secret set` returns. A single
#       stamp taken before both would admit a replica created in the gap between the stamp and
#       its target's update: it loaded the OLD credential, but started "after STAMP", so it
#       would pass Check B — and the delete would then remove the credential it is running on.
#       The window is small and entirely real: staging is scale-to-zero and cold-starts on any
#       request, and production can replace a replica at any time.
az containerapp secret set -n vitally-staging-ca-uksouth -g "$RG" \
  --secrets "oauth-shared-client-secret=$SECRET"
STAMP_STAGING=$(date -u +%s)

az containerapp secret set -n vitally-prod-ca-uksouth -g "$RG" \
  --secrets "entra-oauth-client-secret=$SECRET"
STAMP_PROD=$(date -u +%s)

echo "STAMP_STAGING=$STAMP_STAGING   # record both — each target is checked against its own"
echo "STAMP_PROD=$STAMP_PROD"
```

⚠️ **One residue this does not remove.** `az containerapp secret set` has no `--file` equivalent, so
the value passes as a command-line argument and is briefly readable from the process table on that
host. Acceptable on an operator workstation, not on a shared one. The temp file removes the stdout
and scrollback exposure, which is the larger and longer-lived one — this is smaller, not nothing.

#### Roll and verify staging

**Save this to a file and run it — do not paste it.** The helpers all return non-zero on a failed
listing, a hash mismatch or a stale replica, but a bare sequence of commands *prints* those and
carries on, walking a failed roll straight through the sign-in and into the irreversible delete.
`set -euo pipefail` is what turns those return codes into a stop.

⚠️ **And the wrapper only fires when it is run as a script.** Verified 2026-09-21: from a file,
a failing `verify_target` exits the subshell `rc=1` and the following step never runs; the same
text fed inline to `bash -c` printed the mismatch and **carried on to the next step with `rc=0`**,
because bash's final-command optimisation changes `set -e` semantics there. The protection is real
but it is not in the characters — it is in how you invoke them. Same reasoning as the staging
rollback subshell in `CLAUDE.md`.

```bash
(
  set -euo pipefail
  . docs/runbooks/rotate-helpers.sh
  STAMP_STAGING=<the value script A printed>   # if this is a fresh shell
  NEWHASH=<the value script A printed>

  roll vitally-staging-ca-uksouth
  # 0 = staging is minReplicas 0, so no running replica is its steady state
  verify_target vitally-staging-ca-uksouth oauth-shared-client-secret "$NEWHASH" "$STAMP_STAGING" 0
  echo "STAGING VERIFIED"
)
```

`verify_target` does both checks and the hash *comparison* under one exit code, so nothing depends
on an operator noticing that two printed strings differ.

Then sign in for real against `https://vitally-staging.fiscaltec.com/mcp` from an MCP client.
`/health` and the 401 challenge both pass on the *old* credential, so only `/oauth/token` — reached
by a real authorisation code, hence a browser — exercises this at all. **Read the result together
with the two checks above**: a successful `tools/list` alone would also be produced by the old
credential still being in use.

Anything failing here is recoverable, because the old credential is still live — **stop and fix it
at this point**. That is the entire reason staging goes first.

#### Roll and verify production

```bash
(
  set -euo pipefail
  . docs/runbooks/rotate-helpers.sh           # again if this is a fresh shell
  STAMP_PROD=<the value script A printed>
  NEWHASH=<the value script A printed>

  roll vitally-prod-ca-uksouth
  # 1, NOT 0 — production is minReplicas 1, so zero running replicas is never a valid steady
  # state, only a check made too early. `revision restart` can return before the replacements are
  # running, so this waits (up to ~2 min) rather than passing on an empty listing.
  verify_target vitally-prod-ca-uksouth entra-oauth-client-secret "$NEWHASH" "$STAMP_PROD" 1
  echo "PRODUCTION VERIFIED"
)
```

Then a real sign-in against `https://vitally.fiscaltec.com/mcp`. **This is the step the staging run
could not prove** — see the replica table above.

#### Delete the superseded credential — the point of no return

Only once **both** targets pass both checks *and* a real sign-in. Leave a soak between this and the
production roll rather than running them together:

**List first, in its own fail-fast script.** `$APP` has to be re-established: script A ran in a
shell that has since exited, and the browser sign-ins make a fresh terminal near-certain.

```bash
#!/usr/bin/env bash
set -euo pipefail
. docs/runbooks/rotate-helpers.sh
[ -n "${APP:-}" ] || { echo "APP unset — the helpers did not load; stop"; exit 1; }

az ad app credential list --id "$APP" \
  --query "[].{keyId:keyId,name:displayName,expires:endDateTime}" -o table
```

**Now read that table** and confirm both facts before going on: the keyId you are about to delete
is the **old** one, and the **new** credential is present and unexpired. Deleting the wrong row is
the same outage by a different route, and no script can make that judgement for you — which is why
the delete is deliberately a separate step rather than piped from the listing.

```bash
#!/usr/bin/env bash
set -euo pipefail
. docs/runbooks/rotate-helpers.sh
[ -n "${APP:-}" ] || { echo "APP unset — the helpers did not load; stop"; exit 1; }

OLD_KEY_ID=<the old keyId you just read>          # paste it; do not re-derive it
[ -n "$OLD_KEY_ID" ] || { echo "OLD_KEY_ID unset; stop"; exit 1; }

az ad app credential delete --id "$APP" --key-id "$OLD_KEY_ID"
```

⚠️ **`exit 1`, not `return 1`, and `set -euo pipefail` around both.** `return` outside a function
is an error at the top level of a script, so a guard written that way does not reliably stop
anything — it would fall through to the delete, which is the one step that cannot be undone. An
empty `--id` also does not fail usefully. Both blocks are scripts for the same reason as the
verification blocks above: pasted inline, the wrapper does not fire.

**Recovery paths, and note that they are not symmetric:**

| When | Recovery |
|---|---|
| Before the delete | Both credentials are live. Fix and re-roll, or revert a target by re-setting its secret and rolling again. Cheap |
| After the delete | The old credential is **gone** — it cannot be restored. Recovery is to run this procedure again from step 1, creating a *third* credential. Users are signed out until it completes |

That asymmetry is why the two discriminating checks exist: the delete is the one step whose mistake
cannot be undone, and a sign-in test alone cannot tell you it is safe to take.

⚠️ **One residue the file pattern does not remove.** `az containerapp secret set` has no `--file`
equivalent, so the value passes as a command-line argument and is briefly visible to anything that
can read the process table on that host. That is acceptable on an operator workstation and is not on
a shared or multi-tenant one — the stdout and scrollback exposure is what the temp file removes, and
this is a smaller, shorter-lived residue rather than none.

**Rolling back mid-rotation** (before step 6): put the previous value back with the same
`secret set` + `roll` pair on the affected target. The old credential is still live, so this is a
two-command recovery — exactly what deleting early would destroy.

### Should the Container App copy exist at all? — recorded so it is not re-litigated

#138 asked this once the corrected procedure existed. Three options were considered; the recommended
one is third, and **none of them is implemented** — today's layout is the inline copy the procedure
above rotates.

| Option | Effect | Assessment |
|---|---|---|
| **Keep the inline copies** (today) | Vault is a record; each target holds its own copy | Works, and the procedure above is safe. The cost is permanent: two copies to keep in step, a vault value that *looks* live and is not, and a rotation that must touch every target. This is the shape that produced the original defect |
| **Container App → Key Vault reference** (`keyVaultUrl` + `identityref`) | Vault becomes the live source, so the old wording would finally be true | Viable — the CAE is VNet-injected so it reaches the private endpoint. But it **does not remove the roll**: the env var is still injected at replica start, so step 4 stays. It removes the drift, not the deadline. A middling win |
| ✅ **Federated identity credential** (recommended) | The secret, its expiry and the rotation commitment all disappear | The Container App already has a user-assigned managed identity. Registering it as a federated credential on this app lets `/oauth/token` present a `client_assertion` obtained from that identity (`api://AzureADTokenExchange`) instead of `client_secret`. **No secret, no 180-day clock, no 2027-03-01 outage date, no per-target copy** |

**Why the third and not the second.** The second option improves the *mechanics* of a rotation that
should not need to exist; the third deletes the obligation. The distinction matters here because the
failure mode this runbook documents is not "rotation is awkward" but "rotation was documented wrongly
and nobody noticed for months" — and a procedure nobody has to run cannot rot.

**The cost, stated honestly:** it is a code change to the OAuth proxy's token forwarding in
`Program.cs`, not configuration, so it cannot be reverted by an environment variable the way the
a configuration change can. That sequencing constraint is now moot — #156 abandoned the provider
rollback, so there is no window in which two identity paths are half-true. It still needs the app to
have **no** usable password credential left, or the old path stays silently available.

**Not scheduled by #138**, which covers the procedure. Raised separately so the deadline and the
redesign do not block each other — the rotation above is safe to run on its own, and remains the
fallback if the federated route is not taken in time.

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
terminate `resource` and name the API by scope; the relay is the RFC 8707 default this server keeps
for providers that expect it.

The variable table lives in **CLAUDE.md**; the per-target values are in
`infra/terraform/variables.tf`. What belongs here is
what the cutover **learned about this registration**, since that is what the next person changing it
needs — and those lessons come from the staging flip and the validation against the live tenant, so
they held for the staging flip and still hold now that production has followed.

### `resource` had to be dropped, not reshaped — and the reason recorded earlier was wrong

#105 shipped validation only, relaying the parameter because under the previous provider the relay
was the only thing binding the token audience. The note here predicted the relay would fail under
Entra "because Entra
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

### The previous provider is gone

Its client, both API registrations and its post-login hook were retained through the soak as a
rollback path, and deleted by **#156** once that rollback was abandoned. There is no longer a second
identity path for this server, which is what removes the two-app parity class of failure recorded
under *Gate 1*.
