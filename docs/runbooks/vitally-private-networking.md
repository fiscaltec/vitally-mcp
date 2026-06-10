# Runbook: Vitally MCP — Private Networking Migration

**Status:** ✅ COMPLETED & VALIDATED 2026-06-09 · **Author:** Infra · **Date:** 2026-06-05
**Goal:** Move Key Vault and ACR off the public internet via a VNet-integrated Container Apps
environment + private endpoints, as a reusable *private-by-default* standard.

> Supersedes the #3/#4 risk-acceptance (KV/ACR public + RBAC). After this, KV and ACR are
> reachable only from the VNet.

## Scope & decisions
- **Scope:** full private — both Key Vault **and** ACR (ACR Basic → Premium).
- **Driver:** private-by-default standard for the business (reference pattern).
- **VNet:** standalone `10.80.0.0/23`, not peered (peer to hub later if desired).
- **Scanner:** re-platform the Consumption Logic App → a VNet-integrated scanner. *(As-built: implemented as a Container Apps **Job** on `python:3-slim`, not a Function — see the As-built section.)*
- **Cutover:** anytime / low-traffic; parallel-run old + new for rollback.
- **Ingress stays external** (public) — only the *back-end dependencies* go private. Public
  hostname `vitally.fiscaltec.com` is preserved, so **no Auth0/OAuth changes**.

## Target resources (subscription IT-Production `282207c6…`, RG `vitally-prod-rg-uksouth`, UK South)
| Resource | Name | Notes |
|---|---|---|
| VNet | `vitally-prod-vnet-uksouth` | `10.80.0.0/23` |
| Subnet (env) | `snet-aca` | `10.80.0.0/27`, delegate `Microsoft.App/environments` |
| Subnet (PE) | `snet-pe` | `10.80.0.32/28`, private-endpoint network policies disabled |
| Subnet (func) | `snet-func` | `10.80.0.48/28`, delegation per Functions Flex VNet-integration (confirm at create) |
| NAT Gateway | `vitally-prod-natgw-uksouth` (+ PIP) | static egress for app/func → Auth0/Vitally/Graph/Teams |
| Private DNS | `privatelink.vaultcore.azure.net` | linked to VNet |
| Private DNS | `privatelink.azurecr.io` | linked to VNet |
| KV private endpoint | `vitally-prod-pe-kv-uksouth` | in `snet-pe` |
| ACR private endpoint | `vitally-prod-pe-acr-uksouth` | in `snet-pe`; requires ACR Premium |
| New env | `vitally-prod-cae2-uksouth` | workload-profiles, VNet `snet-aca`, **external** ingress |
| New app | `vitally-prod-ca2-uksouth` | same identity/image/env/secrets/scale as current |
| Scanner func | `vitally-prod-func-secretscan-uksouth` (+ storage) | timer; replaces the Logic App scanner |

*(The `…ca2…`/`…cae2…` names are because the old + new run in parallel during cutover; optional later cleanup.)*

## Phases

### Phase 1 — Network foundation (zero impact)
Create VNet + 3 subnets, NAT gateway + public IP (attach to `snet-aca`), and both private DNS
zones linked to the VNet. No effect on the running service.

### Phase 2 — Private endpoints, public access STILL ON (zero impact)
- Upgrade ACR **Basic → Premium** (online, no downtime).
- Create KV + ACR private endpoints in `snet-pe`; register A-records in the private DNS zones.
- **Leave `publicNetworkAccess=Enabled` on both** so the current app + CI keep working.

### Phase 3 — New environment + app + scanner (zero impact)
- Create VNet-integrated workload-profiles env (`…cae2…`), external ingress.
- Create new app (`…ca2…`) with identical config (user-assigned MI, image, env vars, the
  `oauth-shared-client-secret`, scale 0→3). It comes up on a temporary `…azurecontainerapps.io` FQDN.
- Deploy the timer Function (`…func-secretscan…`) with VNet integration; port the scan logic
  (list secrets via MI → filter ≤30 days → POST Adaptive Card to the Teams webhook). Grant its MI
  **Key Vault Reader**. Retire the Consumption Logic App after validation.

### Phase 4 — Validate new app on temp FQDN (zero impact)
- Confirm the new app resolves KV/ACR via the **private endpoints** (private DNS makes it use the
  PE even while public is still on), pulls its image, and is healthy (`/health`).
- Confirm the Function run reads KV and (force-test) posts to Teams.

### Phase 5 — Cutover (short planned interruption)
- Pre-lower DNS TTL on `vitally.fiscaltec.com`.
- Add custom domain + managed cert to the new app (asuid TXT + point hostname at new env);
  wait for cert issuance.
- Flip `vitally.fiscaltec.com` DNS to the new env. Old app serves until DNS moves.
- Verify end-to-end: OAuth sign-in + an MCP `tools/list` call.
- **Downtime driver:** managed-cert re-issue + DNS propagation (minutes).

### Phase 6 — Lock down + decommission
- Set **KV `publicNetworkAccess=Disabled`** and **ACR public access disabled**.
- Confirm app + Function still work (now fully private).
- Delete the old env, old app, and the Consumption Logic App + its O365 leftovers.

## Rollback
- **Before Phase 6:** repoint `vitally.fiscaltec.com` DNS to the old env (still running). No data loss.
- **After Phase 6:** re-enable `publicNetworkAccess` on KV/ACR and DNS back to old env.
- Old environment is not deleted until Phase 6 success is confirmed.

## Admin access note
Once KV is private (Phase 6), `az` data-plane secret ops from outside the VNet are blocked. Use a
temporary KV firewall allowance for the admin IP, a VNet jumpbox/Bastion, or perform secret ops via
the management plane / a VNet-joined host.

## CI/CD knock-on (feeds task #10)
`az acr build` runs inside ACR (not the GitHub runner) so image builds still work with ACR private,
but verify push/pull and any registry management from CI after lockdown; allow "trusted Azure
services" on ACR if needed.

## Approx. incremental cost
ACR Premium (+~£36/mo), NAT gateway (~£25/mo + data), 2 private endpoints (~£12/mo + data), NAT
public IP (~£3/mo), scanner Job (pennies, no storage), 2 private DNS zones (~£1/mo).
**≈ £75–85/month.**

## As-built (2026-06-09) — deviations from the plan
- **Naming:** rebuilt the env+app with clean names (`vitally-prod-cae-uksouth` / `vitally-prod-ca-uksouth`,
  no `…2…` suffix). The env infra subnet is **`snet-app`** (10.80.0.64/27), not `snet-aca` — the original
  `snet-aca` couldn't be reused (occupied by the interim env until it was deleted) so it was removed; subnets
  are now `snet-app` (env) + `snet-pe` (private endpoints).
- **Scanner:** implemented as a **Container Apps Job** `vitally-prod-secscan-uksouth` (cron `0 8 * * 1`),
  **not** a Function. Image `python:3-slim`; gets a token from the Container Apps identity endpoint
  (`IDENTITY_ENDPOINT`/`IDENTITY_HEADER`) and calls the **Key Vault REST API** directly (no Azure CLI).
  Uses the app's user-assigned MI. Posts an Adaptive Card to the Teams Workflows webhook (stored as a Job secret).
  *Lesson:* the `azure-cli` image failed (large image + `az login --identity` quirks in a job, and piping a
  here-doc script into `bash`); the slim-python + REST approach is the reliable pattern.
- **Cert cutover:** near-zero downtime — managed cert pre-validated via a `_dnsauth` TXT (Cloudflare) **before**
  flipping the `vitally.fiscaltec.com` CNAME. DNS is in the **FISCALTEC Cloudflare** account (zone
  `7e1bdd29b1340ed108cfeaf6061dcfce`); the CNAME must stay **DNS-only (grey cloud)**.
- **Lock-down:** chose **full disable** (`publicNetworkAccess=Disabled` on KV + ACR), not an IP allowlist.
- **Validation (with public OFF):** scanner read KV via PE ✓; app pulled image via ACR PE + `/health` 200 over
  the internet ✓; user client-tested OAuth + list accounts end-to-end ✓.
- **Admin access:** KV/ACR data-plane from outside the VNet is now blocked. For the 180-day secret rotation,
  temporarily re-enable public + add an IP rule for your **current admin egress IP** (find it with
  `curl -s https://api.ipify.org`), then revert; or use a VNet-joined host / Bastion.
- **CI/CD (feeds #10):** `deploy.yml` still unwired; with ACR public off, verify `az acr build`/push from CI
  and enable ACR "trusted Azure services" if needed.
