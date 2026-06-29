# SP3b — Release train — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A nightly release train that cuts a semver tag + GitHub Release from conventional commits on `main` and deploys it, on top of a hardened, reusable, self-rolling-back `deploy.yml`.

**Architecture:** Task 1 makes `deploy.yml` callable (`workflow_call`) and self-protecting (capture previous image → deploy → health+smoke → auto-rollback on failure). Task 2 adds `release.yml`: a nightly scheduled job computes the next version from conventional commits, creates the tag + GitHub Release, then calls the reusable `deploy.yml` with the new tag.

**Tech Stack:** GitHub Actions (reusable workflows, `schedule`), `mathieudutour/github-tag-action`, `gh release create --generate-notes`, Azure CLI, OIDC, GHCR.

## Global Constraints

- ACR public access is Disabled — image reaches ACR only via server-side `az acr import` (unchanged from SP3a). Never weaken that.
- Deploy job keeps `environment: production` (OIDC federated subject `repo:fiscaltec/vitally-mcp:environment:production`).
- Fully automatic: no manual approval gate. Safety = pre-merge CI + post-deploy smoke + auto-rollback.
- Versioning from conventional commits since the last tag; **dependency commits must release** — `feat`→minor, `fix`/`build`/`ci`/`chore`/`refactor`/`perf`→patch, breaking→major, no-bump otherwise.
- Changelog lives in the **GitHub Release** (`--generate-notes`); do NOT commit `CHANGELOG.md` to `main` (the ruleset has no bypass actor — a workflow can't push to `main`).
- Tag prefix `v` (matches existing `v4.0.12`).
- Action pins follow the repo convention (`actions/checkout@v7`, `azure/login@v3`, `docker/login-action@v3`, `docker/build-push-action@v6`); verify any NEW action's latest release tag before pinning (learned from a wrong-version pin earlier).
- UK English; LF line endings. Build/run from repo root; do NOT prefix commands with `cd`. Docker is available for `actionlint` (use `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color <file>`).

---

### Task 1: Harden `deploy.yml` — reusable + capture/smoke/rollback

**Files:**
- Modify (replace contents): `.github/workflows/deploy.yml`

**Interfaces:**
- Produces: `deploy.yml` gains `on: workflow_call` (inputs `ref`, `image_tag`) so `release.yml` (Task 2) can `uses: ./.github/workflows/deploy.yml` with `secrets: inherit`.

- [ ] **Step 1: Replace the workflow**

Overwrite `.github/workflows/deploy.yml` with (this preserves the SP3a flow and adds `workflow_call`, previous-image capture, the `/mcp` smoke, and the rollback step):
```yaml
name: Deploy

# Production deploy. The ACR has public network access disabled, so the image cannot be built/pushed
# to it from a GitHub-hosted runner. Instead we build + push to a private GHCR package, then
# `az acr import` (server-side, works with public access disabled) pulls it into the private ACR, and
# `az containerapp update` rolls the app. OIDC auth as the user-assigned managed identity (federated
# credential subject repo:fiscaltec/vitally-mcp:environment:production). Callable manually
# (workflow_dispatch) or by the release train (workflow_call). On a failed health/smoke check it rolls
# the Container App back to the previous image.
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
  workflow_call:
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
      MCP_URL: https://vitally.fiscaltec.com/mcp
    steps:
      - name: Checkout repository
        uses: actions/checkout@v7
        with:
          ref: ${{ inputs.ref || github.ref }}

      - name: Resolve image tag and GHCR repo
        id: vars
        env:
          INPUT_IMAGE_TAG: ${{ inputs.image_tag }}
        run: |
          set -euo pipefail
          if [ -n "$INPUT_IMAGE_TAG" ]; then
            tag="$INPUT_IMAGE_TAG"
          else
            tag="sha-$(git rev-parse --short HEAD)"
          fi
          if ! printf '%s' "$tag" | grep -Eq '^[A-Za-z0-9._-]+$'; then
            echo "::error::Invalid image tag '$tag' (allowed characters: A-Za-z0-9._-)"
            exit 1
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

      - name: Capture current image (for rollback)
        run: |
          set -euo pipefail
          az config set extension.use_dynamic_install=yes_without_prompt
          prev=$(az containerapp show -g "$RESOURCE_GROUP" -n "$CONTAINER_APP" \
            --query "properties.template.containers[0].image" -o tsv)
          echo "Previous image: $prev"
          echo "PREV_IMAGE=$prev" >> "$GITHUB_ENV"

      - name: Import image into the private ACR (server-side)
        run: |
          set -euo pipefail
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

      - name: Verify deployment (revision health + smoke)
        run: |
          set -euo pipefail
          latest=$(az containerapp show -g "$RESOURCE_GROUP" -n "$CONTAINER_APP" \
            --query "properties.latestRevisionName" -o tsv)
          echo "Latest revision: $latest"
          prov=""
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
            echo "::error::Revision $latest did not reach Provisioned (last state: $prov)"
            exit 1
          fi
          # Smoke: /health must be 200; unauthenticated /mcp must be 401 (app live + Auth0 gate enforced).
          health_code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 20 "$HEALTH_URL" || echo "000")
          echo "/health -> $health_code"
          [ "$health_code" = "200" ] || { echo "::error::/health returned $health_code"; exit 1; }
          mcp_code=$(curl -s -o /dev/null -w "%{http_code}" --max-time 20 -X POST "$MCP_URL" \
            -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}' || echo "000")
          echo "/mcp (unauthenticated) -> $mcp_code"
          [ "$mcp_code" = "401" ] || { echo "::error::/mcp unauthenticated returned $mcp_code (expected 401)"; exit 1; }

      - name: Roll back on failure
        if: ${{ failure() && env.PREV_IMAGE != '' }}
        run: |
          set -euo pipefail
          echo "::warning::Deploy verification failed — rolling back to $PREV_IMAGE"
          az containerapp update \
            --name "$CONTAINER_APP" \
            --resource-group "$RESOURCE_GROUP" \
            --image "$PREV_IMAGE"
          echo "::error::Deploy failed and was rolled back to $PREV_IMAGE"
          exit 1
```

Notes:
- The rollback step's `if: ${{ failure() && env.PREV_IMAGE != '' }}` runs only when a prior step failed AND the previous image was captured — so a pre-capture failure (e.g. build) doesn't attempt a rollback. Its trailing `exit 1` keeps the job red after rolling back.
- `/mcp` unauthenticated returns 401 because the endpoint requires a Bearer token (JwtBearer + `RequireAuthorization`). If the live check ever shows a different code, the controller adjusts the expected value — do not relax it speculatively.

- [ ] **Step 2: Validate with actionlint**

Run: `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/deploy.yml`
Expected: exit 0, no errors. (If Docker is down, say so and confirm the YAML parses with `python -c "import yaml; yaml.safe_load(open('.github/workflows/deploy.yml'))"`.)

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/deploy.yml
git commit -m @'
feat(deploy): make deploy reusable + add smoke test and auto-rollback (SP3b)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 2: `release.yml` — nightly version + deploy

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: the reusable `deploy.yml` from Task 1 (`workflow_call`, inputs `ref`/`image_tag`).

- [ ] **Step 1: Verify the tag action's current version**

The release step uses `mathieudutour/github-tag-action`. Confirm its latest release tag before pinning:
`gh release view -R mathieudutour/github-tag-action --json tagName -q .tagName` (at time of writing `v6.2`). Use whatever it returns as the pin in Step 2 (do NOT guess — a wrong pin fails the run, as happened with a different action earlier).

- [ ] **Step 2: Create the workflow**

Create `.github/workflows/release.yml` (replace `@v6.2` with the verified latest tag from Step 1 if different):
```yaml
name: Release

# Nightly release train: if main changed since the last tag, compute the next semver from
# conventional commits, create the tag + a GitHub Release (the changelog), and deploy it via the
# reusable deploy workflow. Fully automatic; the deploy self-rolls-back on a failed smoke test.
on:
  schedule:
    - cron: "0 2 * * *" # 02:00 UTC nightly
  workflow_dispatch:

permissions:
  contents: write # create tag + GitHub Release

concurrency:
  group: release-train
  cancel-in-progress: false

jobs:
  release:
    name: Cut version + GitHub Release
    runs-on: ubuntu-latest
    outputs:
      new_tag: ${{ steps.tag.outputs.new_tag }}
    steps:
      - name: Checkout repository
        uses: actions/checkout@v7
        with:
          fetch-depth: 0

      - name: Compute and create tag
        id: tag
        uses: mathieudutour/github-tag-action@v6.2
        with:
          github_token: ${{ secrets.GITHUB_TOKEN }}
          default_bump: false
          custom_release_rules: "feat:minor,fix:patch,build:patch,ci:patch,chore:patch,refactor:patch,perf:patch"

      - name: Create GitHub Release
        if: ${{ steps.tag.outputs.new_tag != '' }}
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          NEW_TAG: ${{ steps.tag.outputs.new_tag }}
        run: |
          set -euo pipefail
          gh release create "$NEW_TAG" --generate-notes --latest

  deploy:
    needs: release
    if: ${{ needs.release.outputs.new_tag != '' }}
    uses: ./.github/workflows/deploy.yml
    with:
      ref: ${{ needs.release.outputs.new_tag }}
      image_tag: ${{ needs.release.outputs.new_tag }}
    secrets: inherit
```

Notes:
- `default_bump: false` means a night with no releasable commits (e.g. docs-only) creates no tag → `new_tag` is empty → the Release and deploy steps/jobs are skipped (nothing to release).
- The custom rules make `build`/`ci`/`chore` (dependency/security PRs) a patch bump, so auto-merged updates do reach prod.
- `default tag_prefix` is `v`, matching `v4.0.12`.
- The `deploy` job calls the reusable `deploy.yml`; `secrets: inherit` passes `AZURE_*` + `GITHUB_TOKEN`. `deploy.yml` declares its own `id-token`/`packages` permissions for the called job.

- [ ] **Step 3: Validate with actionlint**

Run: `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color .github/workflows/release.yml`
Expected: exit 0, no errors. (Docker-down fallback: YAML parse as in Task 1.)

- [ ] **Step 4: Commit**

```powershell
git add .github/workflows/release.yml
git commit -m @'
feat(release): nightly release train — version, GitHub Release, deploy (SP3b)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification (controller — after both tasks merge-ready)

- [ ] `actionlint` clean on both workflows.
- [ ] Open a PR from `feature/sp3b-release-train` into `main`; required checks pass (neither workflow runs on PR — `deploy` is dispatch/call only, `release` is schedule/dispatch); resolve any Copilot/CodeQL threads; merge (squash).
- [ ] **Live proof:** `gh workflow run Release --ref main`, then watch: the `release` job computes the next tag (a **minor** bump past `v4.0.12` — there are `feat`s on main, expect `v4.1.0`), creates the GitHub Release with generated notes, and the `deploy` job deploys that tag through the hardened `deploy.yml`. Confirm: a new tag + GitHub Release exist (`gh release list`); prod is running the new tag (`az containerapp show … --query "properties.template.containers[0].image"`); the deploy's `/health` (200) and `/mcp` (401) smoke both passed in the run log; no rollback fired.
- [ ] If the `/mcp` smoke returns a code other than 401 against the (known-good) freshly-deployed image, correct the expected code in `deploy.yml` (fix-forward) rather than leaving a false-rollback path.
