# SP3b — Release train (scheduled version + deploy)

**Date:** 2026-06-29
**Status:** Design (approved) — ready for implementation plan.
**Parent initiative:** Release & supply-chain automation. The final sub-project. SP1 (scanning/gating,
#59), SP2 (auto-merge + grouping, #60/#61), SP3a (deploy capability, #64) and the Terraform back-fill
(#65) are done. SP3b layers the scheduled, versioned, self-verifying release on top of SP3a's deploy.

## 1. Purpose

SP3a gave a working manual deploy. SP3b makes releases **automatic**: a nightly job cuts a versioned
release of `main` (semver tag + GitHub Release notes from conventional commits) and deploys it, with
a post-deploy smoke test and automatic rollback. Combined with SP1/SP2, merged changes — including
auto-merged dependency/security updates — reach production within about a day, hands-off, with a
safety net.

## 2. Decisions

- **Cadence:** nightly (only releases when `main` changed since the last release).
- **Approval:** fully automatic — no human gate; safety is the pre-merge CI gate + a post-deploy
  smoke test + automatic rollback to the previous image on failure.
- **Versioning:** semver derived from conventional commits since the last tag.

## 3. Part 1 — Harden `deploy.yml` (reusable + self-protecting)

Extend the existing `deploy.yml` (do not replace its SP3a behaviour):

- **Add `on: workflow_call`** with the same inputs (`ref`, `image_tag`) alongside the existing
  `workflow_dispatch`, so `release.yml` can call it (`uses: ./.github/workflows/deploy.yml`,
  `secrets: inherit`). The job keeps `environment: production` and the existing permissions.
- **Capture the current image** before `az containerapp update`:
  `PREV_IMAGE=$(az containerapp show … --query "properties.template.containers[0].image" -o tsv)`.
- **Fuller smoke test** (after the existing revision-health poll), all bounded with `--max-time`:
  - `GET https://vitally.fiscaltec.com/health` → require **200**.
  - unauthenticated `POST https://vitally.fiscaltec.com/mcp` (JSON body) → require **401** (proves the
    MCP endpoint is live *and* the Auth0 gate is enforced; needs no token/secret).
- **Auto-rollback:** if the health poll or either smoke check fails, run
  `az containerapp update --image "$PREV_IMAGE"` to roll back, then `exit 1` so the run fails loudly.
  Single-revision replace is accepted (brief unhealthy window on a bad deploy); zero-downtime
  multi-revision traffic-splitting is a non-goal.

The rollback + smoke live in `deploy.yml` so they protect **both** manual dispatch and the scheduled
train.

## 4. Part 2 — `release.yml` (the nightly train)

New `.github/workflows/release.yml`:

- **Triggers:** `schedule` nightly at `0 2 * * *` UTC + `workflow_dispatch` (ad-hoc/testing).
- **Permissions:** `contents: write` (create tag + GitHub Release). The deploy is delegated to
  `deploy.yml` (which declares its own `id-token`/`packages` permissions for the called job).
- **Concurrency:** `group: release-train`, `cancel-in-progress: false`.
- **Job `release`** (`runs-on: ubuntu-latest`):
  1. `actions/checkout` with `fetch-depth: 0` (full history + tags).
  2. **Compute + create the next tag** with `mathieudutour/github-tag-action` (pinned; Dependabot
     tracks it), configured with custom release rules so dependency commits still release:
     `feat`→minor, `fix`/`build`/`ci`/`chore`/`refactor`/`perf`→patch, breaking→major,
     `default_bump: false`. It outputs `new_tag` / `new_version` / `previous_tag`. If `new_tag` is
     empty (no releasable commits since the last tag), the remaining steps are skipped — nothing to
     release.
  3. **Create the GitHub Release** for `new_tag` with auto-generated notes:
     `gh release create "$NEW_TAG" --generate-notes --latest`. The Release body is the canonical
     changelog; `CHANGELOG.md` is **not** committed to `main` (the ruleset has no bypass actor, so a
     workflow cannot push to `main`).
  4. **Deploy `new_tag`** via a second job that `needs: release`, guarded on `new_tag != ''`, calling
     the reusable deploy:
     ```yaml
     deploy:
       needs: release
       if: ${{ needs.release.outputs.new_tag != '' }}
       uses: ./.github/workflows/deploy.yml
       with:
         ref: ${{ needs.release.outputs.new_tag }}
         image_tag: ${{ needs.release.outputs.new_tag }}
       secrets: inherit
     ```
     (The `release` job exposes `new_tag` as a job output.)

## 5. Versioning rules rationale

Dependency/security updates arrive as `build:`/`ci:`/`chore:` commits (Dependabot + the grouping from
SP2). The default semantic-release ruleset would NOT bump on those, so a week of only-dependency
updates would never release — defeating the vuln-remediation goal. The custom rules therefore make
those types a **patch** bump so every artifact-affecting merge produces a release. `docs:`/`style:`
need not bump (no artifact change); the action's `default_bump: false` means an all-docs night
produces no release, which is correct.

## 6. Error handling

- Tag/release-creation failure fails the `release` job before any deploy.
- The deploy's own health-poll + smoke + auto-rollback handle a bad image (Part 1).
- A failed nightly run is visible in Actions. Wiring a proactive alert (e.g. the Teams webhook the
  `vitally-prod-secscan` job already uses) is noted as a future follow-up, not built here.

## 7. Testing / verification

CI/CD + cloud glue; no unit-testable logic. Verification:

- `actionlint` clean on both `deploy.yml` and `release.yml`.
- **Live proof:** a manual `workflow_dispatch` of `release.yml` — it should compute the first
  auto-release (a **minor** bump past `v4.0.12`, since there are `feat`s on `main`), publish the
  GitHub Release with generated notes, then deploy that tag through the hardened `deploy.yml`
  (exercising the new prev-image capture, `/health` + `/mcp` 401 smoke, and — only if it failed — the
  rollback). Confirm prod is running the new tag and `/health` is 200.

## 8. Non-goals (YAGNI)

- Committing `CHANGELOG.md` back to `main` (blocked by the ruleset; the GitHub Release is the
  changelog).
- Zero-downtime multi-revision traffic-splitting / canary (single-revision replace + rollback suffices).
- A bespoke alerting/notification integration (noted for later).
- A manual approval gate (explicitly chosen against — fully automatic).
- Per-environment (staging) promotion — single prod environment only.

## 9. Acceptance criteria

- `deploy.yml` is callable via `workflow_call`, captures the previous image, runs the `/health` +
  `/mcp` 401 smoke, and rolls back to the previous image on health/smoke failure.
- `release.yml` runs nightly + on dispatch; when `main` changed it creates a semver tag (dependency
  commits included in the bump rules) + a GitHub Release with notes, and deploys that tag; when `main`
  is unchanged since the last tag it does nothing.
- `actionlint` clean; a live dispatch produces the first auto-release and deploys it with the smoke
  test passing.
