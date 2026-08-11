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

Run these as `dsearle.adm`, with PIM activated for the Graph grant (Global Administrator is
required to grant `GroupMember.Read.All`; activate via PIM).

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
CAE=vitally-prod-cae-uksouth
ACR=vitallyproducruksouth
KV=vitally-prod-kv-uksouth
APP=vitally-staging-ca-uksouth
ID=vitally-staging-id-uksouth
SUB=$(az account show --query id -o tsv)

# 1. Identity
az identity create -g "$RG" -n "$ID" -l uksouth
ID_PRINCIPAL=$(az identity show -g "$RG" -n "$ID" --query principalId -o tsv)
ID_CLIENT=$(az identity show -g "$RG" -n "$ID" --query clientId -o tsv)
ID_RESOURCE=$(az identity show -g "$RG" -n "$ID" --query id -o tsv)

# 2. Role grants — pull images, read the Vitally secret
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.ContainerRegistry/registries/$ACR"
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"

# 3. Graph GroupMember.Read.All (application permission) — needs Global Administrator via PIM.
#    00000003-0000-0000-c000-000000000000 is Microsoft Graph; the role id is GroupMember.Read.All.
az ad app permission grant --id "$ID_PRINCIPAL" --api 00000003-0000-0000-c000-000000000000 \
  --scope GroupMember.Read.All 2>/dev/null \
  || echo "Grant GroupMember.Read.All to $ID_PRINCIPAL via the portal (Enterprise applications > Permissions) if this fails"

# 4. Container App. ReadOnly is hard-wired true — this is the only guard against mutating
#    real customer data, since there is one live Vitally tenant.
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

# 5. Read the assigned FQDN, then set the origin-dependent settings to match it.
FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "Staging FQDN: https://$FQDN"
```

Substitute the real baseline image tag for `PLACEHOLDER_BASELINE_TAG` — read it from production
with:

```bash
az containerapp show -g "$RG" -n vitally-prod-ca-uksouth \
  --query "properties.template.containers[0].image" -o tsv
```

Then create the Auth0 Resource Server (identifier `https://$FQDN`) and a native client whose only
allowed callback is `https://$FQDN/oauth/callback`, and apply the remaining settings:

```bash
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
