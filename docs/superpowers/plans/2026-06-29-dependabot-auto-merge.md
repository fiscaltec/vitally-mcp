# Auto-merge dependency updates — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Dependabot PRs merge hands-off — enable GitHub-native auto-merge (all update types, squash) on Dependabot-authored PRs and auto-resolve bot-authored review threads, while the existing required checks ensure nothing red ever merges.

**Architecture:** A single workflow `.github/workflows/dependabot-auto-merge.yml` with two triggers (`pull_request` → enable auto-merge; `pull_request_review` → resolve bot threads), gated to Dependabot-authored PRs. It enables native auto-merge (it does not merge directly); GitHub performs the merge once all ruleset conditions pass. A GraphQL step resolves only bot-authored (`copilot-pull-request-reviewer`, `github-advanced-security`) review threads.

**Tech Stack:** GitHub Actions, `GITHUB_TOKEN` (contents+pull-requests write), `gh` CLI (`gh pr merge --auto`, `gh api graphql`), `dependabot/fetch-metadata`.

## Global Constraints

- **Policy:** auto-merge **all** Dependabot update types (patch, minor, major). The required-check suite is the safety net — a breaking bump fails CI and never merges.
- **Scope guard:** act only on Dependabot-authored PRs via `github.event.pull_request.user.login == 'dependabot[bot]'` (NOT `github.actor`, which is the review author on the `pull_request_review` event).
- **Squash merge only** (the `main` ruleset allows no other method).
- **Never resolve human-authored review threads** — only threads whose first comment author is in the bot allowlist `copilot-pull-request-reviewer` / `github-advanced-security`.
- Repo uses **LF** line endings; UK English in comments.
- Build/run from repo root; do NOT prefix commands with `cd`. Docker is available locally for `actionlint`.

---

### Task 1: `dependabot-auto-merge.yml` workflow

**Files:**
- Create: `.github/workflows/dependabot-auto-merge.yml`

**Interfaces:**
- Produces: a workflow named `Dependabot auto-merge` with one job `auto-merge`. No other task depends on it.

- [ ] **Step 1: Create the workflow**

Create `.github/workflows/dependabot-auto-merge.yml`:
```yaml
name: Dependabot auto-merge

# Enables GitHub-native auto-merge on Dependabot PRs (all update types) and resolves bot-authored
# review threads, so the main ruleset's thread-resolution requirement does not block an unattended
# merge. Nothing merges unless every required check passes — GitHub performs the merge itself once
# the ruleset is satisfied. See docs/superpowers/specs/2026-06-29-dependabot-auto-merge-design.md.
on:
  pull_request:
    types: [opened, reopened, synchronize]
  pull_request_review:
    types: [submitted]

permissions:
  contents: write
  pull-requests: write

concurrency:
  group: dependabot-auto-merge-${{ github.event.pull_request.number }}
  cancel-in-progress: false

jobs:
  auto-merge:
    # Only Dependabot-authored PRs. Use the PR author, not github.actor: on the
    # pull_request_review event the actor is the review author (e.g. Copilot), not Dependabot.
    if: ${{ github.event.pull_request.user.login == 'dependabot[bot]' }}
    runs-on: ubuntu-latest
    steps:
      - name: Resolve bot-authored review threads
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          OWNER: ${{ github.event.repository.owner.login }}
          REPO: ${{ github.event.repository.name }}
          PR: ${{ github.event.pull_request.number }}
        run: |
          set -euo pipefail
          # Resolve only unresolved threads whose first comment is authored by a known review bot.
          # Human-authored threads are never touched. Idempotent: already-resolved threads are skipped.
          gh api graphql -f query='
            query($owner:String!,$repo:String!,$pr:Int!){
              repository(owner:$owner,name:$repo){
                pullRequest(number:$pr){
                  reviewThreads(first:100){
                    nodes{ id isResolved comments(first:1){ nodes{ author{ login } } } }
                  }
                }
              }
            }' -f owner="$OWNER" -f repo="$REPO" -F pr="$PR" \
            --jq '.data.repository.pullRequest.reviewThreads.nodes[]
                  | select(.isResolved == false)
                  | select(.comments.nodes[0].author.login == "copilot-pull-request-reviewer"
                        or .comments.nodes[0].author.login == "github-advanced-security")
                  | .id' \
          | while read -r thread_id; do
              echo "Resolving bot review thread $thread_id"
              gh api graphql -f query='
                mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ thread{ isResolved } } }' \
                -f id="$thread_id"
            done

      - name: Fetch Dependabot metadata
        if: ${{ github.event_name == 'pull_request' }}
        id: metadata
        uses: dependabot/fetch-metadata@v2

      - name: Enable auto-merge (squash)
        if: ${{ github.event_name == 'pull_request' }}
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          set -euo pipefail
          echo "Update type: ${{ steps.metadata.outputs.update-type }} (all types auto-merge)."
          gh pr merge --auto --squash "${{ github.event.pull_request.html_url }}"
```

Notes for the implementer:
- `-F pr="$PR"` sends the PR number as a GraphQL `Int!`; `-f` is used for the string variables.
- The two `if: github.event_name == 'pull_request'` steps mean the enable-auto-merge logic runs only on PR-open/reopen/synchronize; the resolver runs on every trigger (including when Copilot submits its review, which is what clears the thread that would otherwise block the merge).
- Do not add `pull_request_target` — it is not needed and would broaden the trust surface.

- [ ] **Step 2: Validate the workflow with actionlint**

Run (the same container approach used by the security-scan workflow):
```
docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:latest -color
```
Expected: no errors reported for `.github/workflows/dependabot-auto-merge.yml`. (actionlint runs shellcheck over the `run:` blocks; the `set -euo pipefail` + piped `while read` pattern should pass cleanly.)

- [ ] **Step 3: Commit**

```powershell
git add .github/workflows/dependabot-auto-merge.yml
git commit -m @'
feat(ci): auto-merge Dependabot PRs with bot-thread resolution (release-automation SP2)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification (controller — after Task 1 merge-ready)

- [ ] `actionlint` clean on the new workflow (Task 1 Step 2).
- [ ] Open a PR from `feature/dependabot-auto-merge` into `main`. The new workflow's `auto-merge`
  job will be **skipped** on this PR (its `if` guard requires a Dependabot author), which is correct —
  confirm it shows as skipped, not failed. CI/CodeQL/scan checks must pass; resolve any Copilot/CodeQL
  threads; merge (squash).
- [ ] Enable repo auto-merge via `gh api`:
  `gh api -X PATCH repos/fiscaltec/vitally-mcp -F allow_auto_merge=true` (confirm the response shows
  `"allow_auto_merge": true`).
- [ ] **Live proof on a real Dependabot PR:** list open Dependabot PRs
  (`gh pr list --author 'app/dependabot' --state open`). If one exists, comment `@dependabot recreate`
  (or push a no-op to retrigger) and confirm: the `auto-merge` job runs, auto-merge is enabled
  (`gh pr view <n> --json autoMergeRequest`), any Copilot thread is auto-resolved, and the PR merges
  once checks are green. If none are open, record that the behaviour will be confirmed on the next
  Dependabot run (Monday schedule) — the workflow logic is otherwise validated by actionlint + review.
