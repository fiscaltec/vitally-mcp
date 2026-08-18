# Runbook: MCP SDK 2.0 staging validation

**Purpose:** execute Layers 2 and 3 of the validation design for the MCP SDK 2.0 / spec
2026-07-28 adoption (`docs/superpowers/specs/2026-08-10-mcp-sdk2-validation-design.md`), and tear
staging down afterwards. Layer 1 (in-process integration tests) is automated and already covered
by the test suite — this runbook is for the two layers that are not.

> **Executed end to end on 2026-08-12** for the SDK 2.1.0 branch, and corrected from what that run
> found. Both gates passed: the baseline (production image) showed a bare `Bearer` challenge and a 404
> on the `/mcp`-suffixed metadata path, and the branch image showed the `resource_metadata` pointer and
> 200 on both paths — with the status staying exactly 401 throughout. A real MCP client completed the
> Auth0→Entra flow, listed 56 tools, and successfully invoked a read tool that fetched the Vitally key
> from Key Vault via managed identity. Teardown completed and production verified unaffected.

> **Read this in full before running anything.** Section 3 provisions a real Container App
> against production's Key Vault and ACR. Section 5 (teardown) is not optional — skipping it
> leaves an orphaned Auth0 client and an unused managed identity as standing security debt.

## Prerequisite check — already passed

The private-networking prerequisite for placing a new Container App in the production
environment was verified on 2026-08-10 against subscription `IT-Production`
(`282207c6-4107-47fa-9d4e-b2fa9b3066cb`) and does not need repeating:

- `vitally-prod-cae-uksouth` is VNet-injected — `vnetConfiguration.infrastructureSubnetId`
  resolves to subnet `snet-app` (`10.80.0.64/27`) of `vitally-prod-vnet-uksouth`, with
  `internal: false`.
- Both `privatelink.vaultcore.azure.net` (link `link-vault`) and `privatelink.azurecr.io` (link
  `link-acr`) are linked to that same VNet.
- Both `vitally-prod-kv-uksouth` and `vitallyproducruksouth` report `publicNetworkAccess:
  Disabled`.

A new Container App placed in `vitally-prod-cae-uksouth` therefore reaches Key Vault and ACR over
their private endpoints with no extra networking work.

## 1. Layer 2 — local container

Confirms what only a real client can show: the negotiated protocol version, tool count, whether
`ttlMs` appears on `tools/list`, and that a read tool round-trips against real Vitally.

```powershell
docker build -t vitally-mcp:local .
docker run --rm -p 5099:8080 `
  -e OAuth__NoAuth=true `
  -e Authorization__ReadOnly=true `
  -e Vitally__Region=EU `
  -e Vitally__DevelopmentApiKey=$env:VITALLY_DEV_KEY `
  vitally-mcp:local
```

Point MCP Inspector, then Claude Code, at `http://localhost:5099/mcp`. Record:

- the negotiated protocol version
- the tool count returned by `tools/list`
- whether `ttlMs` appears on the `tools/list` response
- that a read tool round-trips successfully

**Behavioural note:** once any tool carries an `[Authorize]` attribute, MCP SDK 2.1.0 requires
`AddAuthorization()` and `AddAuthorizationFilters()` to be registered in **every** deployment
posture, including local dev with `OAuth__NoAuth=true` — it fails closed and throws at startup
otherwise. Local dev stays unfiltered not because registration is skipped, but because the
permission handler short-circuits when authorisation is bypassed.

**Gate:** protocol version negotiated and tool count as expected; `ttlMs` present; one read tool
round-trips against real Vitally.

## 2. Layer 3 — staging provisioning

> **`terraform apply` must never be run as part of this work.** Terraform at
> `infra/terraform/` is kept back-filled as documentation of record, but the live resources are
> managed manually with `az cli`. Running a plan against shared state would try to reconcile
> production drift as a side effect. Provision staging with `az cli` below and, if desired,
> back-fill Terraform afterwards with an `import` block, as the other resources were.

> **`Authorization__ReadOnly=true` is hard-wired for staging, not a variable.** There is exactly
> one live Vitally tenant holding real customer data — there is no sandbox — so this flag is the
> only thing preventing a validation run from mutating real customer records. Do not remove it
> from any staging deployment for any reason.

Run all of this as `dsearle.adm`, with PIM activated for Global Administrator (needed for the
Graph grant in step 2.3).

> **Git Bash on Windows mangles resource ids.** Any `az` command taking a `/subscriptions/...`
> resource id needs `export MSYS_NO_PATHCONV=1` first, or MSYS rewrites the leading slash and the
> path arrives as `C:/Program Files/Git/subscriptions/...`. Azure then returns the misleading
> `MissingSubscription: The request did not have a subscription or a valid tenant level resource
> provider`. This silently cost two role-assignment grants on the first run.

**Each block below redeclares the shell variables it needs**, rather than relying on variables
set by an earlier block. Creating the Auth0 client (step 2.5) happens in the portal, between
blocks — treat every block as if it is starting a fresh shell session, because in practice it
will be.

### 2.1 Read the production baseline image tag

Needed before step 2.4, since the Container App is created with an explicit image tag rather
than `:latest`.

```bash
RG=vitally-prod-rg-uksouth
az containerapp show -g "$RG" -n vitally-prod-ca-uksouth \
  --query "properties.template.containers[0].image" -o tsv
```

Note the returned tag — you will substitute it for `PLACEHOLDER_BASELINE_TAG` in step 2.4.

### 2.2 Create the identity and role grants

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
ACR=vitallyproducruksouth
KV=vitally-prod-kv-uksouth
ID=vitally-staging-id-uksouth
SUB=$(az account show --query id -o tsv)

az identity create -g "$RG" -n "$ID" -l uksouth
ID_PRINCIPAL=$(az identity show -g "$RG" -n "$ID" --query principalId -o tsv)

# Role grants — pull images, read the Vitally secret
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.ContainerRegistry/registries/$ACR"
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"

echo "Identity principal id (needed for step 2.3): $ID_PRINCIPAL"
```

### 2.3 Grant Microsoft Graph `GroupMember.Read.All` to the identity

This is an **application permission (app role) on the managed identity's service principal**,
not a delegated permission grant on an app registration. `az ad app permission grant` is the wrong
shape and will not work. Requires **Global Administrator** via PIM, since app-role grants need admin
consent.

The CLI path below was verified end-to-end on 2026-08-12. Look the app role up and confirm it before
assigning — do not paste the id on trust:

```bash
export MSYS_NO_PATHCONV=1
SP=$(az identity show -g vitally-prod-rg-uksouth -n vitally-staging-id-uksouth --query principalId -o tsv)

# Confirm the app role. Expect allowedMemberTypes ["Application"] — that is the correct type for a
# managed identity. As of 2026-08-12 the id is 98830695-27a2-44f7-8c18-0c3ebc9698f6.
az ad sp show --id 00000003-0000-0000-c000-000000000000   --query "appRoles[?value=='GroupMember.Read.All'].{id:id,value:value,allowed:allowedMemberTypes}" -o json

GRAPH_SP=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
ROLE=98830695-27a2-44f7-8c18-0c3ebc9698f6

# NOTE: az rest does NOT accept --body "@file" (that is curl syntax); pass the JSON inline.
az rest --method POST   --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignments"   --headers "Content-Type=application/json"   --body "{\"principalId\":\"$SP\",\"resourceId\":\"$GRAPH_SP\",\"appRoleId\":\"$ROLE\"}"

# Verify
az rest --method GET   --url "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignments"   --query "value[].{resource:resourceDisplayName,appRoleId:appRoleId}" -o json
```

The portal equivalent: Microsoft Entra admin centre → **Enterprise applications** → search
`vitally-staging-id-uksouth` (managed identities appear here as service principals) → **Permissions**
→ **Grant admin consent**, adding Microsoft Graph `GroupMember.Read.All`.

Confirm the grant before continuing. Until it exists, `ToolAuthorizer`'s live Graph lookup fails and
falls back silently to the token claim — a different code path from the one this layer exists to
validate, so the validation would pass while testing the wrong thing.

### 2.4 Create the Container App

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
CAE=vitally-prod-cae-uksouth
ACR=vitallyproducruksouth
KV=vitally-prod-kv-uksouth
APP=vitally-staging-ca-uksouth
ID=vitally-staging-id-uksouth
ID_CLIENT=$(az identity show -g "$RG" -n "$ID" --query clientId -o tsv)
ID_RESOURCE=$(az identity show -g "$RG" -n "$ID" --query id -o tsv)

# ReadOnly is hard-wired true — this is the only guard against mutating real customer data,
# since there is one live Vitally tenant. Substitute the tag from step 2.1 below.
az containerapp create -g "$RG" -n "$APP" --environment "$CAE" \
  --image "$ACR.azurecr.io/vitally-mcp:PLACEHOLDER_BASELINE_TAG" \
  --registry-server "$ACR.azurecr.io" --registry-identity "$ID_RESOURCE" \
  --user-assigned "$ID_RESOURCE" \
  --ingress external --target-port 8080 --transport http \
  --min-replicas 0 --max-replicas 1 \
  --env-vars \
    "Vitally__Region=EU" \
    "Vitally__KeyVaultUri=https://$KV.vault.azure.net/" \
    "AZURE_CLIENT_ID=$ID_CLIENT" \
    "Authorization__ReadOnly=true" \
    "Authorization__LiveGroupCheck=true" \
    "Authorization__ReaderGroupId=71451cc9-f5df-44ee-8ed1-3acc41a911eb" \
    "Authorization__EditorGroupId=19b9d659-284c-4f93-b1c3-a6354db1027c" \
    "Authorization__AdminGroupId=70b48a20-d4b1-47dc-a132-21bc99272a86" \
    "OAuth__NoAuth=false" \
    "OAuth__Authority=https://fiscal-it.uk.auth0.com/" \
    "OAuth__Audience=https://placeholder.invalid/CORRECTED-IN-STEP-2.5"

FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "Staging FQDN: https://$FQDN"
```

Note the FQDN — it is needed to create the Auth0 objects in step 2.5, and that block re-derives
it independently rather than relying on this shell's `$FQDN` surviving.

> **Why the audience is a deliberate placeholder.** `OAuthOptions.Validate()` throws
> *"OAuth:Audience is required when OAuth:NoAuth is false"*, and `Program.cs` forces options
> resolution immediately after `Build()` — so omitting the audience here makes the container
> crash-loop until step 2.5 supplies it. The real audience is the FQDN, which does not exist until
> after this command runs, hence the chicken-and-egg. Setting `OAuth__NoAuth=true` instead does
> **not** work either: `StartupGuards.EnsureSafeAuthConfig` refuses `NoAuth` alongside a configured
> `Vitally__KeyVaultUri`. A non-empty placeholder is the only option that starts, and `Validate()`
> only checks the audience is non-empty. **Step 2.5 must overwrite it** — if it is left in place,
> every token validation fails on audience mismatch.

### 2.5 Configure Auth0 and finish wiring

Create the Auth0 Resource Server and application, mirroring production — read the production
objects first rather than trusting this list, since Auth0 defaults differ from what the proxy needs.

**Resource Server (API):**

- Identifier: `https://<staging FQDN>/` — **with a trailing slash.** MCP clients request the audience
  in that form, and production's identifier (`https://vitally.fiscaltec.com/`) carries one for exactly
  this reason. Omitting it produces `access_denied: Service not found: https://<FQDN>/` at sign-in, and
  **Auth0 API identifiers are immutable**, so getting it wrong costs a second API. `OAuth__Audience`
  must then match it character-for-character.
- `signing_alg: RS256`, `allow_offline_access: true`, `token_lifetime: 28800`
- `skip_consent_for_verifiable_first_party_clients: true` — this is what suppresses the consent screen
- `enforce_policies: true`, and the four scopes `mcp.access`, `vitally:read`, `vitally:write`,
  `vitally:delete`
- **A client grant is then mandatory**, because `enforce_policies: true` pairs with
  `require_client_grant`. Without it, token requests fail.

**Application — `app_type: regular_web` with `token_endpoint_auth_method: client_secret_post`.**
Not a native app: the proxy injects the client secret server-side, so this must be a *confidential*
client. Auth0 defaults a Native app to `token_endpoint_auth_method: none`, which cannot accept the
injected secret. Also set `oidc_conformant: true`, `is_first_party: true`, grant types
`authorization_code` and `refresh_token`, and the single allowed callback
`https://<staging FQDN>/oauth/callback`.

**Enable the Entra connection on the new application.** Connections are enabled per-application in
Auth0, so a new app does not inherit it. Without this, sign-in fails before ever reaching the server.

Then apply the remaining settings:

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
APP=vitally-staging-ca-uksouth
FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)

az containerapp secret set -g "$RG" -n "$APP" \
  --secrets "oauth-shared-client-secret=<staging client secret>"

az containerapp update -g "$RG" -n "$APP" --set-env-vars \
  "OAuth__Audience=https://$FQDN" \
  "OAuth__Resource=https://$FQDN" \
  "OAuth__PublicBaseUrl=https://$FQDN" \
  "OAuth__SharedClientId=<staging client id>" \
  "OAuth__SharedClientSecret=secretref:oauth-shared-client-secret"
```

`<staging client secret>` and `<staging client id>` come from the Auth0 application created
above. **Never paste the actual secret into a committed file** — supply it at the prompt only.

## 3. Baseline gate

Deploy the current `main` image (the tag already in place from provisioning above, or re-deploy
it explicitly). Verify:

- `GET /health` returns `200`.
- Unauthenticated `POST /mcp` returns `401`.
- A real MCP client completes the OAuth flow end-to-end and lists tools.

Do not proceed to the change gate until all three pass. Without this baseline, a connection
failure on the branch image is indistinguishable between "the change broke it" and "staging's
Auth0 client is misconfigured" — both look identical from the client's side.

## 4. Change gate

### Getting the branch image into the private ACR

`vitallyproducruksouth` has `publicNetworkAccess: Disabled`, so you **cannot** `docker push` to it
from a workstation, and `az acr repository show-tags` will fail too (data-plane call). Use the same
route `deploy.yml` uses: push to GHCR, then `az acr import`, which runs server-side in Azure and can
reach public registries.

`gh auth token` does **not** carry `write:packages` by default — add it once with
`gh auth refresh -h github.com -s write:packages` (interactive, device code).

```bash
export MSYS_NO_PATHCONV=1
SHA=$(git rev-parse --short HEAD); TAG="staging-$SHA"

docker build -t vitally-mcp:branch .
docker tag vitally-mcp:branch "ghcr.io/fiscaltec/vitally-mcp:$TAG"
gh auth token | docker login ghcr.io -u <your-github-user> --password-stdin
docker push "ghcr.io/fiscaltec/vitally-mcp:$TAG"

az acr import --name vitallyproducruksouth   --source "ghcr.io/fiscaltec/vitally-mcp:$TAG"   --image "vitally-mcp:$TAG"   --username <your-github-user> --password "$(gh auth token)" --force

az containerapp update -g vitally-prod-rg-uksouth -n vitally-staging-ca-uksouth   --image "vitallyproducruksouth.azurecr.io/vitally-mcp:$TAG"
```

A successful pull by the new revision is your confirmation the import worked — you cannot list ACR
tags from outside the VNet to check directly.

### Verify

Deploy the feature-branch image to the same staging app. Verify:

- All three baseline checks above still pass.
- `ttlMs` is present on the `tools/list` response.
- **All tiers see the same 56 read-only tools.** Under `Authorization__ReadOnly=true` this is the
  correct expected result — see the note below before concluding that tier filtering is broken.

### Note: do not expect `tools/list` to differ by tier under `ReadOnly=true`

Staging hard-wires `Authorization__ReadOnly=true`, which installs `ReadOnlyToolFilter`. That filter
keeps only tools whose `ReadOnlyHint` is true, so **every destructive tool is stripped for every
caller regardless of tier**. Reader, editor and admin therefore all see the same 56 read tools, and
a per-tier difference cannot appear.

This is expected, not a defect. Per-caller filtering is proven by
`AuthorizationFilterToolsListTests`, which asserts the exact 56 / 81 / 93 split for reader / editor
/ admin. **Do not disable `Authorization__ReadOnly` to make a tier difference appear** — it is the
only control preventing a validation run from mutating real customer records, because there is one
live Vitally tenant and no sandbox.

### Optional: validating the tier split against real Entra groups

Only if you specifically want to see tier filtering working against live group membership rather
than synthetic test principals. `tools/list` makes no Vitally API call at all, so this needs a valid
Auth0 token and group membership — not a working Vitally key.

> **Read this warning before running anything below.** Staging deliberately reuses **production's
> Key Vault** (`vitally-prod-kv-uksouth`, set in step 2.4) and, because
> `Vitally__DefaultSecretRef` is left at its default, **the same `vitally-shared` secret production
> reads**. Two settings are easy to confuse and the consequences differ enormously:
>
> - `Vitally__KeyVaultUri` — the **vault** URI. Not a secret reference.
> - `Vitally__DefaultSecretRef` — the **secret name** (defaults to `vitally-shared`).
>
> **Never modify the value of `vitally-shared`.** It is the key production uses for every Vitally
> call, `SecretCacheDuration` is only 5 minutes, and overwriting it would break the live service for
> every user with no step below to restore it. To use an invalid key, create a *separate* secret and
> point staging at it by name.

The guard for this procedure is a deliberately invalid Vitally key in a **separate secret**, so that
`tools/list` still works while any mutating call fails upstream at Vitally.

1. **Create a new, distinctly named secret** holding a deliberately invalid key. This does not touch
   `vitally-shared`:

```bash
set -euo pipefail
az keyvault secret set \
  --vault-name vitally-prod-kv-uksouth \
  --name vitally-staging-invalid \
  --value "sk_invalid_for_staging_tier_validation_only"
```

2. **Point staging at that secret by name** — `DefaultSecretRef`, *not* `KeyVaultUri` — and relax the
   read-only flag. The vault URI is unchanged:

```bash
set -euo pipefail
az containerapp update -g vitally-prod-rg-uksouth -n vitally-staging-ca-uksouth --set-env-vars \
  "Vitally__DefaultSecretRef=vitally-staging-invalid" \
  "Authorization__ReadOnly=false"
```

3. Connect as a member of each of the three `sg-vitally-*` groups in turn and confirm 56 / 81 / 93.

4. **Restore immediately afterwards** and confirm both took effect before doing anything else:

```bash
set -euo pipefail
az containerapp update -g vitally-prod-rg-uksouth -n vitally-staging-ca-uksouth --set-env-vars \
  "Vitally__DefaultSecretRef=vitally-shared" \
  "Authorization__ReadOnly=true"
```

5. **Confirm production is unaffected** — this is the check that catches an accidental edit to the
   shared secret:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://vitally.fiscaltec.com/health   # expect 200
```

   If production Vitally calls are failing, check whether `vitally-shared` was modified. Restoring it
   requires the correct live key from Vitally — it is **not** recoverable from this runbook.

6. Optionally delete the throwaway secret: `az keyvault secret delete --vault-name
   vitally-prod-kv-uksouth --name vitally-staging-invalid`.

Skip this step unless you need it. The in-process test covers the same invariant with no exposure.

## 5. Teardown

**Mandatory, not optional.** An orphaned Auth0 client and an unused managed identity are both
standing security debt — do not leave this for later.

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
APP=vitally-staging-ca-uksouth
ID=vitally-staging-id-uksouth
ID_PRINCIPAL=$(az identity show -g "$RG" -n "$ID" --query principalId -o tsv)

# Role assignments first — deleting the identity leaves orphaned assignments behind otherwise.
# Delete by --role and --scope, NOT by --ids: on 2026-08-12 the --ids form returned
# "Operation returned an invalid status 'Bad Request'" and left one assignment behind, while
# role+scope succeeded. Always confirm the count reaches 0.
az role assignment list --assignee "$ID_PRINCIPAL" --all \
  --query "[].{role:roleDefinitionName,scope:scope}" -o tsv \
  | while IFS=$'\t' read -r role scope; do
      az role assignment delete --assignee "$ID_PRINCIPAL" --role "$role" --scope "$scope"
    done
echo "remaining role assignments: $(az role assignment list --assignee "$ID_PRINCIPAL" --all --query "length(@)" -o tsv)"

az containerapp delete -g "$RG" -n "$APP" --yes
az identity delete -g "$RG" -n "$ID"
```

The Microsoft Graph `GroupMember.Read.All` app-role assignment needs no separate step — deleting the
managed identity removes its service principal, and the assignment with it. Delete it explicitly only
if you tore down in a different order.

**Then in Auth0, via the dashboard** (the Auth0 MCP tools expose no delete for these):

- the staging **application** — this also removes its client grants, and if the client secret was ever
  pasted anywhere, **deleting the application is what invalidates it**
- the staging **Resource Server (API)** — and check whether there is more than one. Identifiers are
  immutable, so a wrong audience on the first attempt leaves an orphaned API behind.

Confirm the production client `VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D` and the API
`https://vitally.fiscaltec.com/` are untouched.

Finally, verify production is unaffected:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://vitally.fiscaltec.com/health          # expect 200
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://vitally.fiscaltec.com/mcp \
  -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'  # expect 401
```

Production is never modified by any step in this runbook — no image swap, no revision-mode
change, no environment-variable edit on `vitally-prod-ca-uksouth`.
