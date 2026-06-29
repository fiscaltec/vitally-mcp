# Auto-merge dependency updates

**Date:** 2026-06-29
**Status:** Design (approved) — ready for implementation plan.
**Parent initiative:** Release & supply-chain automation (see
[supply-chain scanning & gating spec](./2026-06-29-supply-chain-scanning-gating-design.md) for the
3-sub-project decomposition). This is **sub-project 2**.

- **SP1** — supply-chain scanning & gating — **DONE** (PR #59). `nuget-vuln` + `image-cve` are now
  required checks on `main`.
- **SP2** — auto-merge dependency updates (this spec).
- **SP3** — release train (scheduled version + deploy). Pending; deploy half blocked on Azure OIDC
  wiring.

## 1. Purpose

Dependabot already opens weekly grouped PRs (NuGet, GitHub Actions, Docker base images) plus
security-update PRs, but every one waits for a human to merge. This sub-project makes those merges
**hands-off**: a Dependabot PR auto-merges as soon as the full required-check suite passes, with no
human step — while preserving the gate that nothing red ever merges.

## 2. Policy (decided)

- **Auto-merge all Dependabot update types** (patch, minor, **and major**). The safety net is the
  required-check suite, not a semver cut-off — a breaking bump fails CI and therefore never merges.
- Applies only to **Dependabot-authored** PRs (`github.event.pull_request.user.login ==
  'dependabot[bot]'`). All other PRs are unaffected.
- Squash merge (the only method the `main` ruleset allows).

## 3. Safety model

The `main` ruleset requires these checks before any merge, and auto-merge cannot bypass them:
`Build and test (ubuntu-latest, net10.0)`, `Analyze (csharp) (csharp)` (CodeQL), `Validate PR title`,
`nuget-vuln`, `image-cve`. The `Build and test` job runs the **full xUnit suite**, which includes
WebApplicationFactory integration tests that boot the app in-process and exercise the MCP
`initialize` / `tools/list` responses and the OAuth proxy endpoints. Therefore a dependency bump that
breaks the build, a unit/integration test, app startup/DI, or introduces a High/Critical CVE **fails
a required check and never auto-merges** — it waits for manual attention.

**Residual gap (explicit):** CI mocks the Vitally HTTP API, so a bump that changes *runtime*
behaviour against the live Vitally API would pass CI. That class of risk is addressed at **deploy
time in SP3** (a container smoke test + a held/abortable release), not here. SP2's guarantee is
"nothing merges unless the full automated check suite is green".

## 4. Architecture

A single new workflow **`.github/workflows/dependabot-auto-merge.yml`** using **GitHub-native
auto-merge** (no third-party action or SaaS). The workflow does not merge directly; it *enables*
native auto-merge, and GitHub performs the merge itself once every ruleset condition is satisfied.

- **Triggers:**
  - `pull_request` (`opened`, `reopened`, `synchronize`) → enable auto-merge.
  - `pull_request_review` (`submitted`) → run the bot-thread resolver (Copilot's review arrives
    *after* the PR opens, so its threads must be resolved when the review lands, not only at open).
- **Guard:** every job is gated on `github.event.pull_request.user.login == 'dependabot[bot]'` (this
  works for both trigger types, which both carry the PR object; `github.actor` would be the review
  author on the review event, so it must NOT be used for gating).
- **Permissions:** `contents: write` + `pull-requests: write` on `GITHUB_TOKEN` (GitHub's documented
  pattern for Dependabot auto-merge; Dependabot-triggered runs honour the declared `permissions`).
- **Steps:**
  1. **Resolve bot review threads** (runs on both triggers) — see §5.
  2. **Enable auto-merge** (runs on `pull_request` only) — `gh pr merge --auto --squash
     "${{ github.event.pull_request.html_url }}"`. A `dependabot/fetch-metadata@v2` step precedes it
     to log the update type (all types currently auto-merge; the metadata makes a future per-type
     gate a one-line change).

## 5. Bot-thread resolver (tightly scoped)

A step that lists the PR's review threads via GraphQL and resolves **only** unresolved threads whose
first comment author login is in an explicit **bot allowlist**: `copilot-pull-request-reviewer` and
`github-advanced-security` (CodeQL). It must:

- never resolve a human-authored thread;
- run only on Dependabot-authored PRs (enforced by the job guard);
- be idempotent (already-resolved threads are skipped).

Owner/repo and PR number come from the `github` event context. The `GITHUB_TOKEN` with
`pull-requests: write` can call `resolveReviewThread`.

## 6. Repo configuration (via `gh api`, not committed code)

- **Enable repo auto-merge:** `gh api -X PATCH repos/fiscaltec/vitally-mcp -F allow_auto_merge=true`
  (native auto-merge can't be enabled on a PR unless the repo allows it).

## 7. Testing / verification

This sub-project is workflow + GitHub-API glue with no pure-logic unit to fixture-test (unlike SP1's
PowerShell gate). Verification is therefore:

- **`actionlint`** on the new workflow (run in the local Docker container, as in SP1).
- **Static review** of the trigger guards, permissions, and the resolver's bot-allowlist scoping.
- **Live proof:** observe a real Dependabot PR auto-merge end-to-end. Rather than wait for the Monday
  schedule, the controller triggers a Dependabot run on-demand (`gh api -X POST
  repos/.../dependabot/...` re-run, or the "Check for updates" action in the Dependabot UI / `@dependabot
  recreate`), confirms auto-merge is enabled, that a Copilot thread (if any) is auto-resolved, and
  that the PR merges once checks are green.

The spec is explicit that the end-to-end behaviour is proven by the live Dependabot PR, not by a unit
test.

## 8. Non-goals (YAGNI)

- No third-party merge action or SaaS (Mergify, etc.) — native auto-merge only.
- No change to `dependabot.yml`'s existing schedule, grouping, or limits.
- No deploy logic, smoke test, or release versioning — that is SP3.
- No per-update-type gating (policy is "all types"); the `fetch-metadata` step only logs the type so
  a future gate is trivial to add.
- No auto-resolution of human review threads, ever.

## 9. Acceptance criteria

- A Dependabot PR has native auto-merge enabled automatically and merges (squash) once all required
  checks pass, with no human action.
- Bot-authored review threads (Copilot, CodeQL) on Dependabot PRs are auto-resolved so the
  thread-resolution rule does not block the merge; human-authored threads are never touched.
- A Dependabot PR whose checks fail does **not** merge.
- Non-Dependabot PRs are entirely unaffected.
- `actionlint` is clean; the behaviour is confirmed on a live Dependabot PR.
- Repo `allow_auto_merge` is enabled.
