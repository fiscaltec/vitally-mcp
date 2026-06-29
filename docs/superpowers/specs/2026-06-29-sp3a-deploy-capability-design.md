# SP3a — Deploy capability (private-ACR-aware)

**Date:** 2026-06-29
**Status:** Design (approved) — ready for implementation plan.
**Parent initiative:** Release & supply-chain automation. SP1 (scanning/gating, #59) and SP2
(auto-merge, #60/#61) are done. SP3 (release train) is split:

- **SP3a — deploy capability** (this spec): a working, push-button deploy of a chosen commit to
  production, through the private-networked ACR. Delivers value alone (gets prod off the stale
  `v4.0.16` image onto current `main`).
- **SP3b — release-train orchestration** (later): scheduled trigger, semver tag + changelog, fuller
  smoke test, held/abortable release + auto-rollback, layered on SP3a.

## 1. Purpose

`deploy.yml` exists but its `az acr build` step cannot run from a GitHub-hosted runner because the
ACR (`vitallyproducruksouth`) has `publicNetworkAccess: Disabled` (Premium, private endpoint). The
management plane (ARM) is public, so `az containerapp update` is fine, but the image can't be built
or pushed from a public runner. SP3a reworks the deploy so the image reaches the private ACR via a
server-side **`az acr import`**, giving a reliable manual deploy without weakening the private-network
posture or standing up new in-VNet compute.

## 2. Prerequisites already in place (from the OIDC groundwork, 2026-06-29)

- Federated credential `github-actions-prod-env` on the reused runtime UAMI
  `vitally-prod-id-uksouth` (clientId `d93687a0-ef76-4df8-804e-d941067abdeb`), subject
  `repo:fiscaltec/vitally-mcp:environment:production`.
- GitHub secrets `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`; vars `ACR_NAME`,
  `RESOURCE_GROUP`, `CONTAINER_APP`, `IMAGE_NAME`.

## 3. Deploy mechanism (decided): build → private GHCR → `az acr import` → update

A GitHub-hosted runner builds the image and pushes it to a **private GHCR** package; `az acr import`
(a control-plane operation that runs server-side and works even when the registry's public access is
disabled) pulls it into the private ACR; then `az containerapp update` rolls the Container App, which
pulls the new image in-VNet via its existing AcrPull on the UAMI. No new in-VNet compute; the
private posture is unchanged. The built image transits a **private** GHCR package; no secrets are
baked into the image (runtime secrets come from Key Vault).

## 4. Workflow design (`.github/workflows/deploy.yml`, reworked)

- **Trigger:** `workflow_dispatch` with inputs:
  - `ref` (the git ref/branch/tag/SHA to deploy; default the default branch).
  - `image_tag` (optional; default `sha-<short-sha>` of the checked-out ref).
- **Job `deploy`:** `runs-on: ubuntu-latest`, `environment: production` (so the OIDC token subject
  matches the federated credential and any environment protection applies).
- **Permissions:** `id-token: write` (OIDC), `contents: read`, `packages: write` (GHCR push).
- **Steps:**
  1. `actions/checkout` at `inputs.ref`.
  2. Resolve `IMAGE_TAG` (input or `sha-$(git rev-parse --short HEAD)`); compute lowercase GHCR repo
     `ghcr.io/${{ github.repository }}` (GHCR requires a lowercase path).
  3. Log in to GHCR (`docker/login-action` with `github.actor` + `GITHUB_TOKEN`) and build+push
     `ghcr.io/<owner>/<repo>:<IMAGE_TAG>` from the repo `Dockerfile`.
  4. `azure/login@v3` with the three Azure ids (OIDC).
  5. `az acr import --name <ACR_NAME> --source ghcr.io/<owner>/<repo>:<IMAGE_TAG>
     --image <IMAGE_NAME>:<IMAGE_TAG> --username <github.actor> --password <GITHUB_TOKEN>`
     (`--force` so re-running the same tag overwrites rather than failing).
  6. `az containerapp update -g <RESOURCE_GROUP> -n <CONTAINER_APP>
     --image <ACR_NAME>.azurecr.io/<IMAGE_NAME>:<IMAGE_TAG>`.
  7. **Post-deploy health check:** poll the Container App's latest revision until
     `properties.runningState`/health is `Running`/healthy (bounded, e.g. ~10 attempts × 15s); fail
     the job if it doesn't reach healthy. As a final confirmation, GET `https://vitally.fiscaltec.com/health`
     and require HTTP 200.
- **Concurrency:** `group: deploy-production`, `cancel-in-progress: false` (never interrupt an
  in-flight deploy).

Pin actions to the repo's existing major-version convention (`actions/checkout@v6`,
`azure/login@v3`, `docker/login-action@v3`, `docker/build-push-action@v6`); Dependabot tracks them.

## 5. UAMI role assignments (applied via az CLI by the controller)

Scoped to the specific resources (not the resource group), least-privilege within the reuse choice:

- **Contributor** on the ACR `vitallyproducruksouth` — `az acr import` needs
  `Microsoft.ContainerRegistry/registries/importImage/action`, which `AcrPush` does not include;
  Contributor does. (Custom role with just that action is a future hardening.)
- **Contributor** on the Container App `vitally-prod-ca-uksouth` — for `Microsoft.App/containerApps/write`.

**Recorded trade-off:** the UAMI is the *runtime* identity (reuse was chosen over a dedicated deploy
identity), so the running container now also holds Contributor on the ACR + Container App — a wider
blast radius. Acceptable per the reuse decision; a dedicated deploy identity remains an easy future
hardening. Both assignments are applied via az CLI and should be **back-filled into the Terraform
repo** (the team's az-then-Terraform pattern).

## 6. Error handling

- Build/push failures fail the run before any Azure mutation.
- `az acr import` / `az containerapp update` failures fail the job loudly with the Azure error text.
- The post-deploy health poll catches an image that deploys but won't start (bad config, broken
  startup) — the job fails so the operator knows the live revision may be unhealthy. (Automatic
  rollback to the previous image is SP3b.)

## 7. Non-goals (YAGNI — deferred to SP3b)

- Scheduled/automatic triggering (SP3a is manual `workflow_dispatch` only).
- Semver tagging + changelog generation.
- Fuller smoke test (MCP `initialize` / `tools/list`), held/abortable release, and automatic
  rollback to the previous image on failure.
- A dedicated deploy identity / custom least-privilege role (future hardening).
- Terraform authoring of the role assignments here (back-fill happens in the Terraform repo).

## 8. Testing / verification

This is CI/CD + cloud glue, no unit-testable logic. Verification:

- `actionlint` clean on the reworked `deploy.yml`.
- **Live deploy:** run the workflow against `main`, confirm: the image builds and lands in GHCR;
  `az acr import` places it in the private ACR (`az acr repository show-tags`); the Container App
  rolls to the new revision and it reaches healthy; `https://vitally.fiscaltec.com/health` returns
  200; and the live server reflects current `main` (no longer `v4.0.16`). This live deploy is the
  acceptance proof.

## 9. Acceptance criteria

- `deploy.yml` (workflow_dispatch) builds → pushes to private GHCR → imports into the private ACR →
  updates the Container App → verifies revision health + `/health` 200.
- The UAMI holds Contributor on the ACR and the Container App (via az CLI).
- A live deploy of `main` succeeds and moves prod off `v4.0.16` onto the current build.
- `actionlint` clean; the private-network posture is unchanged (ACR public access stays Disabled).
