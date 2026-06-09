# Runbook: ACR Customer-Managed Key (CMK) Migration

**Status:** ✅ COMPLETED & VALIDATED 2026-06-09 (zero downtime — app kept warm) · **Date:** 2026-06-09
**Goal:** Encrypt the container registry at rest with a customer-managed key, while keeping the
app's secret vault fully private and the app online throughout.

## Why this is non-trivial
- **ACR CMK is create-time only** — the existing registry was created without it, so it must be
  **recreated**. The exact `vitally-mcp:v4.0.16` image must be preserved (not rebuilt from source).
- **ACR must read the CMK key from a Key Vault.** Our secret vault `vitally-prod-kv-uksouth` is now
  fully private (`publicNetworkAccess=Disabled`), which ACR (a multi-tenant service, not in our VNet)
  cannot reach. So the CMK key goes in a **dedicated key vault** that allows ACR via the
  **trusted-Azure-services bypass** — the secret vault stays fully private and untouched.

## Open item to verify FIRST (step 0)
Confirm ACR can read a CMK key from a **firewalled** key vault (trusted-services bypass). If ACR is
not an accepted trusted service for Key Vault, the CMK vault must instead use a private endpoint into
the VNet or a documented network exception. **Do not proceed past step 0 until confirmed.**

## Target design
| Resource | Name | Config |
|---|---|---|
| **CMK key vault** | `vitally-prod-cmk-uksouth` | RBAC, soft-delete + **purge protection** (required for CMK), `publicNetworkAccess=Enabled` + `networkAcls{defaultAction:Deny, bypass:AzureServices}`. Holds ONLY the ACR key. |
| **CMK key** | `acr-cmk` | RSA 3072, **version-less** URI (enables auto-rotation) |
| **ACR identity** | reuse `vitally-prod-id-uksouth` | grant **Key Vault Crypto Service Encryption User** on the CMK vault |
| **New ACR** | `vitallyproducruksouth` (same name) | Premium, created with `--identity` + `--key-encryption-key`, public disabled, private endpoint |
| **Secret vault** | `vitally-prod-kv-uksouth` | **UNCHANGED** — stays fully private |

## Sequence (app stays warm → no downtime)
0. **Verify** ACR ↔ firewalled-KV feasibility (above).
1. Create CMK vault + RSA key; grant the MI crypto-encryption-user on it. *(additive)*
2. **Pin the app warm:** set Container App `minReplicas=1` so the running replica keeps serving from
   its cached image and never needs to pull during the window.
3. Temporarily re-enable old ACR public access (to allow image copy).
4. Create a temp Basic registry `vitallyprodtmpuksouth`; `az acr import` `vitally-mcp:v4.0.16` into it
   (preserves the exact image).
5. Delete the old ACR (frees the name + its private endpoint).
6. Create the new ACR `vitallyproducruksouth` (Premium) **with CMK** (`--identity` + versionless key uri).
7. `az acr import` `vitally-mcp:v4.0.16` from the temp registry into the new CMK ACR.
8. Recreate the ACR **private endpoint** + private DNS record; set the new ACR `publicNetworkAccess=Disabled`.
9. **Validate:** confirm `az acr encryption show` = enabled/CMK; force a new app revision so it pulls
   from the CMK registry over the PE; `/health` 200; scanner unaffected.
10. Delete the temp registry; restore app `minReplicas=0`.

## Rollback
The running replica serves from cache throughout (step 2), so there's no user-facing downtime even if
a step fails. If the new ACR/CMK misbehaves, the app keeps serving while we troubleshoot; the temp
registry retains the image until step 10.

## Approx. incremental cost
Dedicated CMK key vault (~£0 for a standard vault + key ops pennies). No other ongoing cost beyond the
already-Premium ACR.
