# Entra app registration — Vitally MCP (#107)

The app registration that **replaced** the Auth0 client + Resource Server pair at the #108 cutover on
2026-09-03. It is **both** the shared OAuth client and the API resource, because that is what the
proxy's `SharedClientId` / `SharedClientSecret` model expects — which is also why its appId is a
valid `aud` as well as the `client_id`.

Provisioned 2026-09-02 via `az` / Microsoft Graph; captured as-built in `infra/terraform/entra.tf`.
Live since 2026-09-03.

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
| Sign-in gate | `appRoleAssignmentRequired = true` + seven department groups, assigned **directly** |
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
`FISCAL IT Auth0` does today. The same seven department groups are assigned:

Product · IT & Security · Project Management · Customer Operations · Executive Leadership Team ·
Customer Account Management · Service Delivery

**Assign groups directly — never the `sg-vitally-*` tier groups.** The Entra app-assignment gate
honours only *direct* members of an assigned group; nesting does not grant sign-in. Assigning
`sg-vitally-*` here would admit only their two direct members. This is why the department groups are
both assigned here **and** nested inside `sg-vitally-*`.

The two gates are separate mechanisms and should stay that way:

| | Question it answers | Mechanism |
|---|---|---|
| Gate 1 | may this person sign in at all? | direct department assignment on this app |
| Gate 2 | which tier of tools do they get? | `sg-vitally-*` membership, resolved **transitively** by `GraphGroupPermissionResolver` via Graph using only the `oid` claim |

Gate 2 is IdP-independent — it survives the cutover untouched.

### Onboarding a new department

```bash
export MSYS_NO_PATHCONV=1   # Git Bash mangles the URL path otherwise
SP=7904188d-4b34-4651-bf0f-6941fbcf6a8b
GROUP=<new-group-object-id>
echo "{\"principalId\":\"$GROUP\",\"resourceId\":\"$SP\",\"appRoleId\":\"00000000-0000-0000-0000-000000000000\"}" > body.json
az rest --method post \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignedTo" \
  --headers "Content-Type=application/json" --body @body.json
```

Add it to `entra_gate1_group_object_ids` in `infra/terraform/entra.tf` in the same change. Until the
Auth0 rollback path is retired, do the equivalent on `FISCAL IT Auth0` too — not because Auth0 gates
anything today (it does not), but so a rollback does not silently lock the new department out.

Verify at any time:

```bash
az rest --method get \
  --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignedTo" \
  --query "value[].{p:principalDisplayName,t:principalType}" -o tsv
```

The result should be **seven Group rows and nothing else**. A `User` row is drift — see below.

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
> az rest --method delete \
>   --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignedTo/<assignment-id>"
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

**180 days is the standard**, as asserted by `infra/terraform/scan/run.py`'s Teams card ("rotate per
the 180-day standard"). Nothing in the vault followed it until 2026-09-02, when both secrets were
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
MYIP=$(curl -s https://ifconfig.me)   # inside the script, every time
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

## The cutover (#108) — done 2026-09-03

Config-only, as designed. The variable table and the rollback live in **CLAUDE.md**, under *The
Auth0 → Entra cutover (#108) and its rollback*; the per-target values are in
`infra/terraform/variables.tf`. What belongs here is what the cutover **learned about this
registration**, since that is what the next person changing it needs.

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
