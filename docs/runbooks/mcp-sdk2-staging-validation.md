# Runbook: MCP SDK 2.0 staging validation

**Purpose:** execute Layers 2 and 3 of the validation design for the MCP SDK 2.0 / spec
2026-07-28 adoption (`docs/superpowers/specs/2026-08-10-mcp-sdk2-validation-design.md`), and tear
staging down afterwards. Layer 1 (in-process integration tests) is automated and already covered
by the test suite — this runbook is for the two layers that are not.

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
not a delegated permission grant on an app registration — `az ad app permission grant` is the
wrong shape for this and is not used here. Do it through the portal:

1. Requires **Global Administrator**, activated via PIM — application-permission (app role)
   grants need admin consent.
2. Microsoft Entra admin centre → **Enterprise applications** → search for
   `vitally-staging-id-uksouth` (the identity created in step 2.2 — managed identities appear
   here as service principals) → **Permissions** → **Grant admin consent for FISCAL Technologies**,
   adding the Microsoft Graph application permission `GroupMember.Read.All`.
3. Confirm the permission shows as **Granted** against that service principal before continuing.
   Until it is granted, `ToolAuthorizer`'s live Graph lookup fails and falls back to the token
   claim, which is a different code path from the one this layer exists to validate.

A CLI equivalent exists (an app-role assignment on the service principal, not a permission grant
on an app registration), but it is deliberately not included here as a copy-paste command: it
needs the exact Graph app-role id for `GroupMember.Read.All`, which has not been verified for
this runbook. If you want to script it, look that id up against the Microsoft Graph service
principal's `appRoles` first, confirm it, and only then build the `az rest` /
`New-MgServicePrincipalAppRoleAssignment` call — do not guess the id.

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
    "OAuth__Authority=https://fiscal-it.uk.auth0.com/"

FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "Staging FQDN: https://$FQDN"
```

Note the FQDN — it is needed to create the Auth0 objects in step 2.5, and that block re-derives
it independently rather than relying on this shell's `$FQDN` surviving.

### 2.5 Configure Auth0 and finish wiring

Create the Auth0 Resource Server (identifier `https://<staging FQDN>`) and a native client whose
only allowed callback is `https://<staging FQDN>/oauth/callback`, using the FQDN noted in step
2.4. Then apply the remaining settings:

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

`<staging client secret>` and `<staging client id>` come from the Auth0 native client created
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

Deploy the feature-branch image to the same staging app. Verify:

- All three baseline checks above still pass.
- `tools/list` differs by caller tier (reader / editor / admin).
- `ttlMs` is present on the `tools/list` response.

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
az role assignment list --assignee "$ID_PRINCIPAL" --all --query "[].id" -o tsv \
  | xargs -r -n1 az role assignment delete --ids

az containerapp delete -g "$RG" -n "$APP" --yes
az identity delete -g "$RG" -n "$ID"
```

Then in Auth0: delete the staging Resource Server (API) and the staging native client. Confirm
the production client `VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D` and the API
`https://vitally.fiscaltec.com/` are untouched.

Finally, verify production is unaffected:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://vitally.fiscaltec.com/health          # expect 200
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://vitally.fiscaltec.com/mcp \
  -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'  # expect 401
```

Production is never modified by any step in this runbook — no image swap, no revision-mode
change, no environment-variable edit on `vitally-prod-ca-uksouth`.
