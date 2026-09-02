# Vitally MCP — Infrastructure as Code (Terraform)

As-built capture of the production estate in **IT-Production / `vitally-prod-rg-uksouth`** (UK South).
The infrastructure was originally deployed by hand; this codifies it so it's reproducible, reviewable,
and DR-able.

> **These resources already exist.** Adopt them via **import** (below) — do **not** `apply` blind, or
> Terraform will try to create duplicates. Always review `terraform plan` first.

## Layout
| File | Contents |
|---|---|
| `providers.tf` | azurerm provider (~> 4.0), state backend (commented) |
| `variables.tf` | all inputs (non-secret have defaults; secrets are `sensitive`) |
| `network.tf` | VNet, subnets (`snet-app`, `snet-pe`), NAT gateway + PIP, private DNS zones |
| `identity.tf` | user-assigned MI + its role assignments (KV Secrets User, AcrPull, CMK Crypto) |
| `keyvault.tf` | secret vault (private), CMK vault (firewalled) + RSA key, KV private endpoint, diag |
| `acr.tf` | ACR (Premium, CMK, private) + private endpoint + diag |
| `monitoring.tf` | Log Analytics + Application Insights |
| `containerapps.tf` | Container Apps env, production app, and the secret-expiry scanner Job (`scan/run.py`) |
| `containerapps-staging.tf` | Staging app (#112) — second app in the *same* env; pre-production target for IdP changes |
| `entra.tf` | Entra app registration + SP + Gate 1 group assignments (#107) — needs the `azuread` provider, so re-run `terraform init` |
| `imports.tf` | import blocks for adoption (comment out after import) |

## Prerequisites
- Terraform >= 1.6, `az login` against subscription `282207c6-…` (IT-Production).
- **State backend:** create a storage account + `tfstate` container, then uncomment the `backend`
  block in `providers.tf` and `terraform init -migrate-state`. (Local state works for the first pass.)
- Secrets — supply via env (not committed):
  ```bash
  export TF_VAR_oauth_shared_client_secret='…'
  export TF_VAR_teams_webhook_url='…'
  ```
  > ⚠️ Terraform persists these values in **state** even though the variables are `sensitive`. Always use a
  > **remote backend with encryption + tight RBAC** (the azurerm backend on a locked-down storage account)
  > and never commit state. To keep secret *values* out of state entirely, switch the Container App/Job
  > secrets to **Key Vault references** (`key_vault_secret_id`) instead of inline values.

## Adoption (import the existing estate)
```bash
terraform init
terraform plan    # shows the imports (from imports.tf) + any drift — REVIEW CAREFULLY
terraform apply   # performs the imports + reconciles
```
After a clean import, comment out `imports.tf`. From then on it's the source of truth.

A few resources need an ID looked up before their import block works (see notes in `imports.tf`):
role assignments (`az role assignment list --scope <id> --query "[].id"`), diagnostic settings
(`<resource-id>|to-law`), the CMK key (needs data-plane access to the firewalled CMK vault), and the
DNS vnet-links / NAT associations (composite IDs).

## Known limitations (reconcile manually / accept drift)
- **Managed TLS certificates** — both the `vitally.fiscaltec.com` and `vitally-staging.fiscaltec.com`
  custom domains use *free managed* certs, which azurerm cannot create. They're bound out-of-band
  (`az containerapp hostname add` then `az containerapp hostname bind`). The `custom_domain` blocks in
  `containerapps.tf` / `containerapps-staging.tf` are commented; either keep binding them manually and
  `ignore_changes`, or switch to BYO certs via `azurerm_container_app_environment_certificate`.
- **DNS** — `fiscaltec.com` is on Cloudflare, not Azure DNS, so no zone is managed here. Each custom
  domain needs an **un-proxied** (DNS-only) `CNAME` to the app's default FQDN plus an
  `asuid.<subdomain>` `TXT` carrying the environment's `customDomainVerificationId`. Proxying the
  CNAME breaks managed-certificate issuance.
- **`vitally-shared` secret value** — the Vitally API key is *not* managed here (would land in state).
  Manage it with `az keyvault secret set` and keep its 180-day expiry; the scanner Job alerts before expiry.
- **CMK key management** — the dedicated CMK vault is firewalled; managing the key via Terraform needs the
  runner to have a network path. On adoption it is imported, not created.
- **Diagnostic-setting block syntax** and **container `cpu`/`memory`** may need minor tweaks to match the
  exact azurerm version / live values — `terraform plan` will surface these.

## CI/CD (wired)
`.github/workflows/deploy.yml` deploys to one of two targets, named by GitHub **environment**:
`production` (default, and where the nightly release train ships) or `staging`. Each target needs
three things, all captured here: a federated credential on the managed identity whose subject is
`repo:fiscaltec/vitally-mcp:environment:<target>` (`identity.tf`), `Contributor` on that target's
Container App (`identity.tf`), and `CONTAINER_APP` + `PUBLIC_ORIGIN` as **environment-scoped** GitHub
variables. `ACR_NAME` / `RESOURCE_GROUP` / `IMAGE_NAME` are shared and stay repo-level.

The workflow itself holds no per-target literals, so adding a target means an environment plus a
federated credential and no YAML change. It fails before building if any of those variables is
missing, rather than mid-deploy.

The registry keeps `network_rule_bypass_option = "AzureServices"`, so ACR Tasks (`az acr build`) still
work with public access off.

## Related
- `docs/runbooks/vitally-private-networking.md` — the VNet/private-endpoint migration
- `docs/runbooks/acr-cmk-migration.md` — the CMK migration
