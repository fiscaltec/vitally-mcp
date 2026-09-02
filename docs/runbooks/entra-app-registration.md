# Entra app registration — Vitally MCP (#107)

The app registration that replaces the Auth0 client + Resource Server pair at the #108 cutover. It is
**both** the shared OAuth client and the API resource, because that is what the proxy's
`SharedClientId` / `SharedClientSecret` model expects.

Provisioned 2026-09-02 via `az` / Microsoft Graph; captured as-built in `infra/terraform/entra.tf`.

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
| Client secret | `entra-mcp-client-secret` in `vitally-prod-kv-uksouth` |

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

Add it to `entra_gate1_group_object_ids` in `infra/terraform/entra.tf` in the same change, and do the
equivalent on `FISCAL IT Auth0` until the cutover completes — until then, Auth0 is still the live
sign-in path and this app gates nothing.

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
`vitally-shared`. **Twelve-month lifetime**, so unlike the Auth0 secret (which never expires) this is
a standing rotation commitment. Record the expiry when creating it.

> **The vault's data plane is private-endpoint only** (`publicNetworkAccess: Disabled`,
> `networkAcls.defaultAction: Deny`, no IP or VNet rules). A `secret set` from a workstation fails
> with `ForbiddenByConnection` — a *network* denial, independent of RBAC, so Global Administrator
> does not help. Writing this secret needs one of:
>
> - a temporary IP rule (`az keyvault network-rule add --ip-address <egress-ip>`), removed
>   immediately afterwards — a brief, audited weakening of a deliberate control; or
> - a host inside the VNet, or a VPN with private-DNS resolution to the private endpoint.

**Create and store in one go, so the value is never printed.** Entra shows a secret value once;
capturing it into a variable that is then echoed puts a live credential into terminal scrollback and
into any session transcript.

```bash
export MSYS_NO_PATHCONV=1
APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4
END=$(date -u -d '+12 months' '+%Y-%m-%dT%H:%M:%SZ')

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

# 3. destroy the local copies
rm -f secret.txt pw.json
```

### Rotation

Same three steps, then **delete the superseded credential** by `keyId` once the Container App has
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

## At the cutover (#108)

This app is inert until then — nothing about the live Auth0 path changes by its existence. The
cutover is config-only:

| Setting | Auth0 (today) | Entra |
|---|---|---|
| `OAuth__Authority` | `https://fiscal-it.uk.auth0.com/` | `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0` |
| `OAuth__Audience` | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com` (**no slash**) |
| `OAuth__Resource` | `https://vitally.fiscaltec.com/` | unchanged — **still with the slash** |
| `OAuth__SharedClientId` | Auth0 client | `c3812e7d-a413-4169-b57e-803326611ba3` |
| `OAuth__SharedClientSecret` | `auth0-shared-client-secret` | `entra-mcp-client-secret` |

Staging flips first. Note that staging's token `aud` will be *production's* App ID URI, since one
registration serves both origins — that is expected, and is why `Audience` and `Resource` diverge by
host as well as by slash there.

**#108 also has to stop forwarding the RFC 8707 `resource` parameter.** #105 shipped validation only:
the proxy checks `resource` and still relays it, because on Auth0 that relay is the only thing binding
the token audience. Under Entra the relay *fails* — Entra matches `resource` exactly against a
registered identifier and will not accept the trailing-slash form clients send (`AADSTS9010010`). So
"the proxy consumes `resource` locally" becomes true only when #108 lands, and the Auth0 tenant's
*Resource Parameter Compatibility Profile* must stay enabled until it does.
