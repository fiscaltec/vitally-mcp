# Supply-chain scanning & gating

**Date:** 2026-06-29
**Status:** Design (approved) — ready for implementation plan.
**Parent initiative:** Release & supply-chain automation for the Vitally MCP server. Decomposed into
three sub-projects, each its own spec → plan → build cycle:

1. **Supply-chain scanning & gating** (this spec) — make vulnerable NuGet packages and container-image
   CVEs visible, and *gate* merges on High/Critical. Establishes the "green = shippable" guarantee the
   other two rely on. In-repo only; no cloud dependency.
2. **Auto-merge dependency updates** — auto-merge green Dependabot PRs by policy (relies on #1 as the
   gate). In-repo.
3. **Release train** — scheduled (nightly/weekly) job that, when `main` is green and changed, cuts a
   semver tag + changelog (conventional commits) and deploys via the existing `deploy.yml` machinery.
   Needs the Azure OIDC federated credential + repo vars/secrets + environment protection.

This spec covers **only sub-project 1**.

## 1. Purpose

Today: Dependabot opens weekly *version* PRs (NuGet grouped, GitHub Actions, Docker base images) and
CodeQL runs SAST, but nothing checks for **known-vulnerable dependencies** or **container-image CVEs**,
and nothing *blocks* a merge on them. For a finance-sector production service that wants automated
releases, the pipeline must be able to assert "this is free of known High/Critical vulnerabilities"
before anything ships. This sub-project adds that assertion as a required gate, with a documented,
time-boxed waiver mechanism so an unfixable transitive CVE can't permanently wedge the pipeline.

## 2. Enforcement policy (decided)

- **Block** the build / merge on **High & Critical** severity findings (NuGet *and* image).
- **Report** Moderate/Low without blocking (printed in logs; image findings tracked in the GitHub
  Security tab via SARIF).
- **Unfixed CVEs still block** (no `ignore-unfixed`): an unpatchable High forces a *documented,
  time-boxed waiver* rather than a silent pass — the auditable behaviour we want.
- A waiver **auto-expires** and the finding re-blocks, forcing periodic review.

## 3. Architecture

A new workflow **`.github/workflows/security-scan.yml`**:

- **Triggers:** `pull_request` → `main`, `push` → `main`, `schedule` (weekly, Monday ~09:00 UTC to sit
  with the CodeQL sweep — chosen so a CVE disclosed against *unchanged* dependencies is still caught),
  and `workflow_dispatch`.
- **Permissions:** `contents: read`, `security-events: write` (SARIF upload). Least privilege.
- **Concurrency:** cancel-in-progress per ref (mirrors `ci.yml`).
- Two independent jobs (below). Both are added to the `main` branch ruleset's **required status
  checks** so an un-waived High/Critical blocks merge.

### Job A — `nuget-vuln` (NuGet package vulnerabilities)

1. `actions/checkout`, `actions/setup-dotnet` (10.0.x), NuGet cache (mirror `ci.yml`).
2. `dotnet restore VitallyMcp.sln`.
3. `dotnet list VitallyMcp.sln package --vulnerable --include-transitive --format json > nuget-vuln.json`.
4. Run **`scripts/Test-NuGetVulnerabilities.ps1`** (`shell: pwsh`) against that JSON + the waiver file.
   The script exits non-zero on any remaining (un-waived, un-expired) **High/Critical**; prints
   Moderate/Low as informational; prints a summary table.

Covers source-level NuGet incl. transitive packages (authoritative via NuGet's audit source), and is
fast (no image build).

### Job B — `image-cve` (container-image CVEs)

1. `actions/checkout`.
2. `docker build -t vitally-mcp:scan -f Dockerfile .` (the existing chiselled multi-stage build;
   validates the Dockerfile builds, too).
3. **Trivy** (`aquasecurity/trivy-action`) `image` scan, `vuln-type: os,library`,
   `severity: HIGH,CRITICAL`, `exit-code: 1`, `ignore-unfixed: false`, `trivyignores: .trivyignore.yaml`.
4. A second, non-blocking Trivy invocation (or `format: sarif`, `exit-code: 0`) produces SARIF for
   **all** severities, uploaded via `github/codeql-action/upload-sarif` so findings are tracked in the
   Security tab over time.

Covers base-OS packages + bundled libraries the NuGet check can't see. Low-noise because the runtime
base is `dotnet/aspnet:10.0-noble-chiseled`.

## 4. Components

### `scripts/Test-NuGetVulnerabilities.ps1`
- **Input:** path to the `dotnet list --vulnerable --format json` output; path to the waiver file.
- **Logic:** walk `projects[].frameworks[].topLevelPackages[]` and `transitivePackages[]`; for each
  `vulnerabilities[]` read `severity` (`Low|Moderate|High|Critical`) and `advisoryurl` (carries the
  GHSA id). A finding is **suppressed** if a waiver entry matches its GHSA id (or package id) **and**
  the waiver's `expires` date is in the future. Fail (exit 1) if any **un-suppressed High/Critical**
  remain; else exit 0. Always print a summary (severity, package, GHSA, suppressed?/expiry).
- PowerShell chosen for native JSON + date handling, parity with the dev environment, and it runs on
  the ubuntu runner via `pwsh` with no new C# project and no Docker impact.

### `security/nuget-vuln-allowlist.json`
A JSON array of `{ "id": "GHSA-xxxx...", "package": "Some.Pkg", "reason": "...", "expires":
"YYYY-MM-DD" }`. `id` matches the advisory; `package` is documentation/secondary match. `expires` is
required. Starts empty (`[]`).

### `.trivyignore.yaml`
Native Trivy ignore file; per-CVE entries with `expired_at` where a documented waiver is needed.
Starts effectively empty (header comment only).

### `docs/runbooks/vulnerability-scanning.md`
Documents: the policy (block High/Critical, waivers expire), how to add a NuGet waiver and a Trivy
waiver (with worked examples), where findings surface (Security tab), and the weekly-scan behaviour.

## 5. Repo / cloud configuration (executed via `gh api`, not code)

- **Enable Dependabot security updates:** `gh api -X PUT repos/{owner}/{repo}/vulnerability-alerts`
  and `gh api -X PUT repos/{owner}/{repo}/automated-security-fixes`. (Distinct from the existing
  *version* updates in `dependabot.yml`.)
- **Add the two jobs as required status checks** on the `main` ruleset, via `gh api` against the
  repository ruleset (`required_status_checks`), once the check names exist (after the workflow's
  first run on the bootstrap PR). Check contexts: the two job names from `security-scan.yml`.

These are done by the controller during/after implementation, not committed files.

## 6. Testing

The only bespoke logic is the PowerShell gate, so it gets **fixture-driven tests** run in CI (a
dedicated step or matrix in the `nuget-vuln` job, guarded so fixtures never gate real merges):

- `tests/security-fixtures/clean.json` → script exits 0.
- `tests/security-fixtures/has-high.json` (an unwaived High) → script exits 1.
- `tests/security-fixtures/has-suppressed-high.json` + a waiver with a **future** `expires` → exits 0.
- `tests/security-fixtures/has-expired-suppression.json` + a waiver with a **past** `expires` → exits 1
  (proves expiry re-blocks).
- A Moderate-only fixture → exits 0 (proves Moderate doesn't block).

Trivy and Dependabot are off-the-shelf (no custom logic to unit-test). The `docker build` step itself
validates the Dockerfile.

## 7. Non-goals (YAGNI)

- **Auto-merge of Dependabot PRs** — sub-project 2.
- **Versioning / changelog / deploy** — sub-project 3.
- **A custom issue-filing bot** for new CVEs — the Security tab + a failed scheduled run + Dependabot
  alerts cover discovery; a bespoke filer is unnecessary.
- **A unified single-suppressions-file wrapper** (the rejected "Approach B") — idiomatic per-tool
  waivers are sufficient; consolidation can be revisited later if waiver volume warrants.
- **Defender-for-Containers as the gate** — kept as complementary at-rest registry scanning, not the
  pre-merge gate.
- **Secret scanning / IaC scanning** — out of scope for this sub-project.

## 8. Acceptance criteria

- `security-scan.yml` runs on PR, push to `main`, weekly schedule, and manual dispatch.
- A PR introducing an un-waived High/Critical NuGet package **fails** `nuget-vuln`; a documented,
  un-expired waiver makes it pass; an expired waiver fails again.
- The image job builds the Dockerfile and fails on an un-waived High/Critical image CVE; all findings
  appear in the Security tab via SARIF.
- The PowerShell gate's fixture tests pass in CI.
- Dependabot security updates are enabled, and both jobs are required checks on `main` (via `gh api`).
- `docs/runbooks/vulnerability-scanning.md` documents the policy and waiver process.
