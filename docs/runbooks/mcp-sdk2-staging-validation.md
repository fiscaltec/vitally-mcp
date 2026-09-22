# Runbook: MCP SDK 2.0 staging validation

> # ⚠️ ARCHIVED — DO NOT FOLLOW
>
> **Every command below is superseded.** This records a validation executed on 2026-08-12 and is kept
> only because the Layer 2/3 evidence is the basis for the SDK 2.0 adoption sign-off. It is not a
> procedure, and following its section headings will provision infrastructure that does not exist in
> the current estate — notably the identity-provider client in step 2.5, which no longer exists.
>
> | If you came here to… | Go to |
> |---|---|
> | stand staging up | the **Staging** section of CLAUDE.md — `deploy.yml` plus the two `az containerapp hostname` commands, and set `Authorization__ReadOnly` yourself. `infra/terraform/containerapps-staging.tf` is an as-built *record*, not a stand-up path: `terraform apply` is never run here, so it applies nothing, including that guard |
> | validate an identity-provider change | `docs/runbooks/entra-cutover-staging-validation.md` |
> | work on the app registration | `docs/runbooks/entra-app-registration.md` |
> | tear staging down | the teardown table in CLAUDE.md |
>
> Read the rest as a record of what happened, in the past tense, regardless of how it is worded.

**Purpose:** execute Layers 2 and 3 of the validation design for the MCP SDK 2.0 / spec
2026-07-28 adoption (`docs/superpowers/specs/2026-08-10-mcp-sdk2-validation-design.md`), and tear
staging down afterwards. Layer 1 (in-process integration tests) is automated and already covered
by the test suite — this runbook is for the two layers that are not.

> **Executed end to end on 2026-08-12** for the SDK 2.1.0 branch, and corrected from what that run
> found. Both gates passed: the baseline (production image) showed a bare `Bearer` challenge and a 404
> on the `/mcp`-suffixed metadata path, and the branch image showed the `resource_metadata` pointer and
> 200 on both paths — with the status staying exactly 401 throughout. A real MCP client completed the
> OAuth sign-in flow, listed 56 tools, and successfully invoked a read tool that fetched the Vitally key
> from Key Vault via managed identity. Teardown completed and production verified unaffected.

> **Read this in full before running anything.** Section 3 provisions a real Container App
> against production's Key Vault and ACR. Section 5 (teardown) is not optional — skipping it
> leaves an orphaned OAuth client and an unused managed identity as standing security debt.

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

**Behavioural note:** once any tool carries an `[Authorize]` attribute, MCP SDK 2.1.0 and later require
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
set by an earlier block. Creating the OAuth client (step 2.5) happened in the portal, between
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
    "OAuth__Authority=<the identity provider issuer of the time>" \
    "OAuth__Audience=https://placeholder.invalid/CORRECTED-IN-STEP-2.5"

FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "Staging FQDN: https://$FQDN"
```

Note the FQDN — it was needed to create the identity-provider objects in step 2.5, and that block re-derives
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

### 2.5 Configure the identity provider and finish wiring

**This section is removed rather than updated.** It described provisioning a staging API
registration and OAuth client at the identity provider in use in August 2026, which #156
decommissioned. Rewriting it to name Entra would not make it correct either: the current estate uses
**one** Entra app registration serving both origins, with no staging-specific client to create, so
the procedure has no equivalent rather than a changed one.

Nothing after it depended on those details — the gates in sections 3 and 4 assert the server's own
behaviour, not the provider's. For the current shape see `docs/runbooks/entra-app-registration.md`,
and for validating an identity change `docs/runbooks/entra-cutover-staging-validation.md`.

## 3. Baseline gate

Deploy the current `main` image (the tag already in place from provisioning above, or re-deploy
it explicitly). Verify:

- `GET /health` returns `200`.
- Unauthenticated `POST /mcp` returns `401`.
- A real MCP client completes the OAuth flow end-to-end and lists tools.

Do not proceed to the change gate until all three pass. Without this baseline, a connection
failure on the branch image is indistinguishable between "the change broke it" and "staging's
OAuth client is misconfigured" — both look identical from the client's side.

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

### Note: a non-production audience got no `permissions` claim

**Historical, and no longer reachable.** The provider's post-login hook minted the `permissions`
claim only for production's audience, so a staging-specific audience got none. That mattered in
August 2026 because `ToolAuthorizer` could still fall back to the token claim; #108 removed that
fall-through and #156 removed the hook with the rest of that provider, so no claim can authorise
anyone today regardless of audience.

What survives is the diagnostic, which is still the right first move and has the same cause:

> **An empty `tools/list` does not implicate the code under test.** It means the live-group path is
> not working — check the §2.3 Graph grant first, since a missing `GroupMember.Read.All` fails
> silently to exactly this state. Observed on 2026-08-22 while validating #90.

Read the inverse carefully too: a populated tool list only proves live Entra membership resolved.

To isolate the rest of the path from authorisation entirely, set `Authorization__Enabled=false` and
re-list — `ReadOnly=true` still hides the destructive tools, so expect 56.

### Optional: validating the tier split against real Entra groups

Only if you specifically want to see tier filtering working against live group membership rather
than synthetic test principals. `tools/list` makes no Vitally API call at all, so this needs a valid
token and group membership — not a working Vitally key.

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

**Mandatory, not optional.** An orphaned OAuth client and an unused managed identity are both
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

**Then, at the identity provider:** delete the staging-specific OAuth client and API registration
that step 2.5 created. Neither is part of the current estate — #156 decommissioned that provider — so
this applies only to a historical run, and is kept because an orphaned OAuth client is exactly the
standing security debt this section exists to prevent.


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
