# SP3a — Deploy capability (private-ACR-aware) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework `deploy.yml` into a working manual deploy that gets a built image into the private-networked ACR via `az acr import` and rolls the Container App, then verifies the new revision is healthy.

**Architecture:** A GitHub-hosted runner builds the image and pushes it to a private GHCR package; `az acr import` (server-side, works with ACR public access disabled) pulls it into the private ACR; `az containerapp update` rolls the app (which pulls in-VNet via its existing AcrPull); a bounded health poll + a `/health` 200 check confirm the deploy.

**Tech Stack:** GitHub Actions, GHCR, OIDC (`azure/login`), Azure CLI (`az acr import`, `az containerapp`), Docker buildx.

## Global Constraints

- The ACR `vitallyproducruksouth` has `publicNetworkAccess: Disabled` — never build/push to it from a public runner, and **do not weaken** that posture. Images reach it only via server-side `az acr import`.
- Deploy job must use `environment: production` (the OIDC federated credential's subject is `repo:fiscaltec/vitally-mcp:environment:production`).
- OIDC only — no long-lived Azure credentials. The image carries no secrets (runtime secrets come from Key Vault).
- Action pins follow the repo convention: `actions/checkout@v6`, `azure/login@v3`, `docker/login-action@v3`, `docker/build-push-action@v6`.
- GHCR repo path must be lowercase; `github.repository` (`fiscaltec/vitally-mcp`) already is.
- Fixed names (from repo vars/secrets, already set): ACR `vitallyproducruksouth`, RG `vitally-prod-rg-uksouth`, Container App `vitally-prod-ca-uksouth`, image `vitally-mcp`.
- UK English; LF line endings. Build/run from repo root; do NOT prefix commands with `cd`.

---

### Task 1: Rework `deploy.yml` to the GHCR → import → update flow

**Files:**
- Modify (replace contents): `.github/workflows/deploy.yml`

**Interfaces:**
- Produces: a `workflow_dispatch` workflow named `Deploy` with a `deploy` job (no other task depends on it).

- [ ] **Step 1: Replace the workflow**

Overwrite `.github/workflows/deploy.yml` with:
```yaml
name: Deploy

# Manual production deploy. The ACR has public network access disabled, so the image cannot be
# built/pushed to it from a GitHub-hosted runner. Instead we build + push to a private GHCR package,
# then `az acr import` (a server-side control-plane op that works with public access disabled) pulls
# it into the private ACR, and `az containerapp update` rolls the app. OIDC auth as the user-assigned
# managed identity (federated credential subject repo:fiscaltec/vitally-mcp:environment:production).
on:
  workflow_dispatch:
    inputs:
      ref:
        description: "Git ref (branch/tag/SHA) to deploy"
        required: false
        type: string
      image_tag:
        description: "Image tag to publish (default sha-<short-sha> of the chosen ref)"
        required: false
        type: string

permissions:
  id-token: write # OIDC token for azure/login
  contents: read
  packages: write # push to GHCR

concurrency:
  group: deploy-production
  cancel-in-progress: false

jobs:
  deploy:
    name: Build, import to ACR, roll Container App
    runs-on: ubuntu-latest
    environment: production
    env:
      ACR_NAME: ${{ vars.ACR_NAME }}
      RESOURCE_GROUP: ${{ vars.RESOURCE_GROUP }}
      CONTAINER_APP: ${{ vars.CONTAINER_APP }}
      IMAGE_NAME: ${{ vars.IMAGE_NAME }}
      HEALTH_URL: https://vitally.fiscaltec.com/health
    steps:
      - name: Checkout repository
        uses: actions/checkout@v6
        with:
          ref: ${{ inputs.ref || github.ref }}

      - name: Resolve image tag and GHCR repo
        id: vars
        run: |
          set -euo pipefail
          if [ -n "${{ inputs.image_tag }}" ]; then
            tag="${{ inputs.image_tag }}"
          else
            tag="sha-$(git rev-parse --short HEAD)"
          fi
          echo "tag=$tag" >> "$GITHUB_OUTPUT"
          echo "ghcr=ghcr.io/${GITHUB_REPOSITORY,,}" >> "$GITHUB_OUTPUT"

      - name: Log in to GHCR
        uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push image to GHCR
        uses: docker/build-push-action@v6
        with:
          context: .
          file: Dockerfile
          push: true
          tags: ${{ steps.vars.outputs.ghcr }}:${{ steps.vars.outputs.tag }}

      - name: Azure login (OIDC)
        uses: azure/login@v3
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Import image into the private ACR (server-side)
        run: |
          set -euo pipefail
          az config set extension.use_dynamic_install=yes_without_prompt
          az acr import \
            --name "$ACR_NAME" \
            --source "${{ steps.vars.outputs.ghcr }}:${{ steps.vars.outputs.tag }}" \
            --image "$IMAGE_NAME:${{ steps.vars.outputs.tag }}" \
            --username "${{ github.actor }}" \
            --password "${{ secrets.GITHUB_TOKEN }}" \
            --force

      - name: Update Container App revision
        run: |
          set -euo pipefail
          az containerapp update \
            --name "$CONTAINER_APP" \
            --resource-group "$RESOURCE_GROUP" \
            --image "$ACR_NAME.azurecr.io/$IMAGE_NAME:${{ steps.vars.outputs.tag }}"

      - name: Verify new revision is healthy
        run: |
          set -euo pipefail
          latest=$(az containerapp show -g "$RESOURCE_GROUP" -n "$CONTAINER_APP" \
            --query "properties.latestRevisionName" -o tsv)
          echo "Latest revision: $latest"
          prov=""; health=""
          for i in $(seq 1 12); do
            prov=$(az containerapp revision show -g "$RESOURCE_GROUP" -n "$CONTAINER_APP" --revision "$latest" \
              --query "properties.provisioningState" -o tsv 2>/dev/null || echo "")
            health=$(az containerapp revision show -g "$RESOURCE_GROUP" -n "$CONTAINER_APP" --revision "$latest" \
              --query "properties.healthState" -o tsv 2>/dev/null || echo "")
            echo "attempt $i: provisioning=$prov health=$health"
            if [ "$prov" = "Provisioned" ] && { [ "$health" = "Healthy" ] || [ "$health" = "None" ]; }; then
              break
            fi
            sleep 15
          done
          if [ "$prov" != "Provisioned" ]; then
            echo "::error::Revision $latest did not reach Provisioned (last: $prov / $health)"
            exit 1
          fi

      - name: Smoke-check /health
        run: |
          set -euo pipefail
          code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 20 "$HEALTH_URL" || echo "000")
          echo "/health returned $code"
          [ "$code" = "200" ] || { echo "::error::/health returned $code"; exit 1; }
```

Notes for the implementer:
- `${GITHUB_REPOSITORY,,}` lowercases the repo path for GHCR (bash parameter expansion).
- `az acr import --username/--password` are the **source** (GHCR) creds; `github.actor` + `GITHUB_TOKEN` can read the repo's own GHCR package. `--force` overwrites if the tag already exists.
- `az config set extension.use_dynamic_install=yes_without_prompt` lets the `containerapp` az extension auto-install on the runner without prompting.
- Do not add a `push:` trigger — SP3b decides automatic triggering. This stays manual.

- [ ] **Step 2: Validate with actionlint**

Run:
```
docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/deploy.yml
```
Expected: no errors. (If Docker is unavailable in the environment, say so and instead confirm the YAML parses with a YAML parser; do not skip silently.)

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/deploy.yml
git commit -m @'
feat(deploy): private-ACR-aware deploy via GHCR + az acr import (SP3a)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification (controller — after Task 1 merge-ready)

- [ ] `actionlint` clean (Task 1 Step 2).
- [ ] Open a PR from `feature/sp3a-deploy-capability` into `main`; required checks pass (the `deploy`
  job does not run on PR — it's `workflow_dispatch` only); resolve any Copilot/CodeQL threads; merge (squash).
- [ ] **Assign the UAMI roles via az CLI** (scoped to the two resources):
  ```
  PRINCIPAL=c57b35e7-19b8-4d25-b762-321de5f4f0cb   # vitally-prod-id-uksouth principalId
  ACR_ID=$(az acr show -n vitallyproducruksouth --query id -o tsv)
  CA_ID=$(az containerapp show -g vitally-prod-rg-uksouth -n vitally-prod-ca-uksouth --query id -o tsv)
  az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal --role Contributor --scope "$ACR_ID"
  az role assignment create --assignee-object-id "$PRINCIPAL" --assignee-principal-type ServicePrincipal --role Contributor --scope "$CA_ID"
  ```
  (Back-fill these into the Terraform repo afterwards.)
- [ ] **Live deploy:** trigger the workflow against `main`
  (`gh workflow run Deploy --ref main`), watch it: image builds + lands in GHCR; `az acr import`
  places it in the private ACR; the Container App rolls to a new revision that reaches `Provisioned`;
  `https://vitally.fiscaltec.com/health` returns 200. Confirm prod is now running the current build
  (`az containerapp show -g vitally-prod-rg-uksouth -n vitally-prod-ca-uksouth --query
  "properties.template.containers[0].image"` no longer shows `v4.0.16`).
- [ ] If the deploy fails at import/update/health, capture the error, fix forward, and re-run; do not
  weaken the ACR network posture to work around it.
