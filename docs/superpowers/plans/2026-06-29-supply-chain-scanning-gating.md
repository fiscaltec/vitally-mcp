# Supply-chain scanning & gating — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a CI gate that fails on un-waived High/Critical vulnerabilities — both vulnerable NuGet packages and container-image CVEs — with time-boxed, documented waivers, so merges to `main` are blocked on known-vulnerable dependencies.

**Architecture:** A new `.github/workflows/security-scan.yml` with two jobs — `nuget-vuln` (runs `dotnet list package --vulnerable --format json`, evaluated by a PowerShell gate script against a waiver file) and `image-cve` (builds the chiselled Dockerfile, scans with Trivy, uploads SARIF). The gate logic lives in one testable PowerShell script with fixture-driven tests. Waivers live in `security/nuget-vuln-allowlist.json` (NuGet) and `.trivyignore.yaml` (image).

**Tech Stack:** GitHub Actions, PowerShell 7 (`pwsh`, preinstalled on ubuntu runners), .NET 10 SDK (`dotnet list package --vulnerable --format json`), Trivy (`aquasecurity/trivy-action`), `github/codeql-action/upload-sarif`.

## Global Constraints

- **Enforcement policy:** block on **High & Critical** (NuGet *and* image); report Moderate/Low without blocking; **do not** set `ignore-unfixed` (unfixed High/Critical still block, forcing a documented waiver).
- **Waivers auto-expire:** an entry past its `expires`/`expired_at` date no longer suppresses — the finding re-blocks.
- UK English in comments/docs. Repo uses **LF** line endings.
- The runtime base image is `dotnet/aspnet:10.0-noble-chiseled` (chiselled/distroless) — do not change it.
- Build/run from repo root; do NOT prefix commands with `cd`. Dev shell is PowerShell; CI runners are ubuntu (`pwsh` available).
- `dotnet list package --vulnerable` exits 0 even when vulnerabilities exist — the PowerShell script is the thing that fails the build; never rely on the command's exit code.
- NuGet severity strings are exactly `Low|Moderate|High|Critical`.

---

### Task 1: PowerShell vulnerability gate + waiver file + fixture tests

**Files:**
- Create: `scripts/Test-NuGetVulnerabilities.ps1`
- Create: `security/nuget-vuln-allowlist.json`
- Create: `tests/Run-SecurityGateFixtures.ps1`
- Create: `tests/security-fixtures/clean.json`
- Create: `tests/security-fixtures/has-high.json`
- Create: `tests/security-fixtures/has-suppressed-high.json`
- Create: `tests/security-fixtures/has-expired-suppression.json`
- Create: `tests/security-fixtures/moderate-only.json`
- Create: `tests/security-fixtures/test-allowlist.json`

**Interfaces:**
- Produces: `scripts/Test-NuGetVulnerabilities.ps1` — params `-JsonPath <string>` (required), `-AllowlistPath <string>` (default `security/nuget-vuln-allowlist.json`), `-Now <string>` (ISO date, default = today). Exit `0` = no un-waived High/Critical; `1` = at least one un-waived High/Critical; `2` = input report file missing. Consumed by Task 2's `nuget-vuln` job.
- Waiver file shape: JSON array of `{ "id": "GHSA-…", "package": "…", "reason": "…", "expires": "YYYY-MM-DD" }`.

- [ ] **Step 1: Write the fixtures and the test harness (the failing test)**

Create the six fixture files under `tests/security-fixtures/`.

`clean.json`:
```json
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "VitallyMcp/VitallyMcp.csproj",
      "frameworks": [ { "framework": "net10.0", "topLevelPackages": [], "transitivePackages": [] } ] }
  ]
}
```

`has-high.json`:
```json
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "VitallyMcp/VitallyMcp.csproj",
      "frameworks": [ { "framework": "net10.0",
        "topLevelPackages": [
          { "id": "Vulnerable.Pkg", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0",
            "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-high-0000-0000" } ] }
        ],
        "transitivePackages": [] } ] }
  ]
}
```

`has-suppressed-high.json` (identical shape, different advisory id):
```json
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "VitallyMcp/VitallyMcp.csproj",
      "frameworks": [ { "framework": "net10.0",
        "topLevelPackages": [
          { "id": "Vulnerable.Pkg", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0",
            "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-supp-1111-1111" } ] }
        ],
        "transitivePackages": [] } ] }
  ]
}
```

`has-expired-suppression.json`:
```json
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "VitallyMcp/VitallyMcp.csproj",
      "frameworks": [ { "framework": "net10.0",
        "topLevelPackages": [
          { "id": "Vulnerable.Pkg", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0",
            "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://github.com/advisories/GHSA-expd-2222-2222" } ] }
        ],
        "transitivePackages": [] } ] }
  ]
}
```

`moderate-only.json` (uses `transitivePackages` to exercise that branch):
```json
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "projects": [
    { "path": "VitallyMcp/VitallyMcp.csproj",
      "frameworks": [ { "framework": "net10.0",
        "topLevelPackages": [],
        "transitivePackages": [
          { "id": "Moderate.Pkg", "resolvedVersion": "2.0.0",
            "vulnerabilities": [ { "severity": "Moderate", "advisoryurl": "https://github.com/advisories/GHSA-mod-3333-3333" } ] }
        ] } ] }
  ]
}
```

`test-allowlist.json`:
```json
[
  { "id": "GHSA-supp-1111-1111", "package": "Vulnerable.Pkg", "reason": "Test fixture: active waiver", "expires": "2099-01-01" },
  { "id": "GHSA-expd-2222-2222", "package": "Vulnerable.Pkg", "reason": "Test fixture: expired waiver", "expires": "2000-01-01" }
]
```

Create `tests/Run-SecurityGateFixtures.ps1` — it spawns the gate as a **child `pwsh` process** (so the script's `exit` sets `$LASTEXITCODE` instead of terminating the harness) and asserts the exit code for each case with a fixed `-Now`:
```powershell
#!/usr/bin/env pwsh
# Fixture-driven tests for scripts/Test-NuGetVulnerabilities.ps1.
# Runs the gate against committed sample reports with a fixed -Now date so waiver
# expiry is deterministic, and asserts the exit code of each case.
$ErrorActionPreference = 'Stop'

$gate     = Join-Path $PSScriptRoot '..' 'scripts' 'Test-NuGetVulnerabilities.ps1'
$fixtures = Join-Path $PSScriptRoot 'security-fixtures'
$allow    = Join-Path $fixtures 'test-allowlist.json'
$now      = '2026-06-29'

$cases = @(
    @{ File = 'clean.json';                   Expected = 0 }
    @{ File = 'has-high.json';                Expected = 1 }
    @{ File = 'has-suppressed-high.json';     Expected = 0 }
    @{ File = 'has-expired-suppression.json'; Expected = 1 }
    @{ File = 'moderate-only.json';           Expected = 0 }
)

$failed = 0
foreach ($c in $cases) {
    pwsh -NoProfile -File $gate -JsonPath (Join-Path $fixtures $c.File) -AllowlistPath $allow -Now $now | Out-Null
    $code = $LASTEXITCODE
    if ($code -ne $c.Expected) {
        Write-Host "FAIL: $($c.File) — expected exit $($c.Expected), got $code"
        $failed++
    }
    else {
        Write-Host "PASS: $($c.File) (exit $code)"
    }
}

if ($failed -gt 0) { Write-Host "$failed fixture case(s) failed."; exit 1 }
Write-Host 'All security-gate fixture cases passed.'
exit 0
```

- [ ] **Step 2: Run the harness to verify it fails**

Run: `pwsh -NoProfile -File tests/Run-SecurityGateFixtures.ps1`
Expected: FAIL — the gate script doesn't exist yet, so every case errors / mismatches and the harness exits 1.

- [ ] **Step 3: Implement the gate script**

Create `scripts/Test-NuGetVulnerabilities.ps1`:
```powershell
#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Fails (exit 1) when `dotnet list package --vulnerable --format json` reports an un-waived
  High/Critical vulnerability. Moderate/Low are reported but never block. Waivers come from a JSON
  allowlist and auto-expire (an entry past its `expires` date no longer suppresses).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$JsonPath,
    [string]$AllowlistPath = 'security/nuget-vuln-allowlist.json',
    [string]$Now
)

$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$nowDate = if ($Now) { [datetime]::Parse($Now, $inv) } else { Get-Date }

if (-not (Test-Path -LiteralPath $JsonPath)) {
    Write-Host "::error::Vulnerability report not found: $JsonPath"
    exit 2
}

$report = Get-Content -Raw -LiteralPath $JsonPath | ConvertFrom-Json

# Active (un-expired) suppressions: GHSA id -> expiry date.
$active = @{}
if (Test-Path -LiteralPath $AllowlistPath) {
    foreach ($entry in @((Get-Content -Raw -LiteralPath $AllowlistPath | ConvertFrom-Json))) {
        if (-not $entry.id -or -not $entry.expires) { continue }
        $expires = [datetime]::Parse($entry.expires, $inv)
        if ($expires -gt $nowDate) { $active[$entry.id] = $expires }
    }
}

$findings = [System.Collections.Generic.List[object]]::new()
foreach ($project in @($report.projects)) {
    foreach ($fw in @($project.frameworks)) {
        $packages = @($fw.topLevelPackages) + @($fw.transitivePackages)
        foreach ($pkg in $packages) {
            if (-not $pkg) { continue }
            foreach ($vuln in @($pkg.vulnerabilities)) {
                if (-not $vuln) { continue }
                $ghsa = ("$($vuln.advisoryurl)" -split '/')[-1]
                $suppressed = $active.ContainsKey($ghsa)
                $findings.Add([pscustomobject]@{
                    Severity   = "$($vuln.severity)"
                    Package    = $pkg.id
                    Resolved   = $pkg.resolvedVersion
                    Advisory   = $ghsa
                    Suppressed = $suppressed
                    Expires    = if ($suppressed) { $active[$ghsa].ToString('yyyy-MM-dd') } else { '' }
                })
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host 'NuGet vulnerability findings:'
    $findings | Sort-Object Severity | Format-Table -AutoSize | Out-String | Write-Host
}
else {
    Write-Host 'No known-vulnerable NuGet packages reported.'
}

$blocking = @($findings | Where-Object { $_.Severity -in @('High', 'Critical') -and -not $_.Suppressed })
if ($blocking.Count -gt 0) {
    Write-Host "::error::$($blocking.Count) un-waived High/Critical NuGet vulnerability(ies) found."
    exit 1
}
Write-Host 'No un-waived High/Critical NuGet vulnerabilities.'
exit 0
```

- [ ] **Step 4: Run the harness to verify it passes**

Run: `pwsh -NoProfile -File tests/Run-SecurityGateFixtures.ps1`
Expected: PASS — all 5 cases print `PASS`, harness exits 0.

- [ ] **Step 5: Create the (empty) production waiver file**

Create `security/nuget-vuln-allowlist.json`:
```json
[]
```

- [ ] **Step 6: Commit**

```powershell
git add scripts/Test-NuGetVulnerabilities.ps1 security/nuget-vuln-allowlist.json tests/Run-SecurityGateFixtures.ps1 tests/security-fixtures
git commit -m @'
feat(security): NuGet vulnerability gate script + fixture tests (scanning SP1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 2: `security-scan.yml` workflow + Trivy waiver + runbook

**Files:**
- Create: `.github/workflows/security-scan.yml`
- Create: `.trivyignore.yaml`
- Create: `docs/runbooks/vulnerability-scanning.md`

**Interfaces:**
- Consumes: `scripts/Test-NuGetVulnerabilities.ps1`, `tests/Run-SecurityGateFixtures.ps1`, `security/nuget-vuln-allowlist.json` (Task 1); `Dockerfile` (existing, repo root).
- Produces: two CI check contexts — job names **`nuget-vuln`** and **`image-cve`** — which the controller adds to the `main` ruleset's required checks in Final Verification.

- [ ] **Step 1: Create the Trivy waiver file**

Create `.trivyignore.yaml` (header only — nothing suppressed initially):
```yaml
# Trivy ignore file for the Vitally MCP container image scan.
# Each waiver MUST carry a reason (statement) and an expiry (expired_at). After expired_at passes,
# the finding re-blocks the build, forcing review. See docs/runbooks/vulnerability-scanning.md.
#
# Example:
# vulnerabilities:
#   - id: CVE-2025-12345
#     statement: "No fix in the chiselled base image yet; revisit at the next .NET base bump."
#     expired_at: 2026-09-30
```

- [ ] **Step 2: Create the workflow**

Create `.github/workflows/security-scan.yml`:
```yaml
name: Security scan

# Gates merges on known-vulnerable NuGet packages and container-image CVEs (High/Critical).
# The weekly schedule re-scans unchanged main, so a CVE disclosed against existing dependencies is
# still caught. Waivers: security/nuget-vuln-allowlist.json (NuGet) and .trivyignore.yaml (image).
on:
  pull_request:
    branches: [main]
  push:
    branches: [main]
  schedule:
    - cron: "0 9 * * 1" # Monday 09:00 UTC, alongside the CodeQL sweep
  workflow_dispatch:

permissions:
  contents: read
  security-events: write # SARIF upload to the Security tab

concurrency:
  group: security-scan-${{ github.ref }}
  cancel-in-progress: true

jobs:
  nuget-vuln:
    name: nuget-vuln
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repository
        uses: actions/checkout@v6

      - name: Self-test the gate script (fixtures)
        shell: pwsh
        run: pwsh -NoProfile -File tests/Run-SecurityGateFixtures.ps1

      - name: Setup .NET 10 SDK
        uses: actions/setup-dotnet@v5
        with:
          dotnet-version: "10.0.x"

      - name: Restore dependencies
        run: dotnet restore VitallyMcp.sln

      - name: List vulnerable packages
        run: >
          dotnet list VitallyMcp.sln package --vulnerable --include-transitive --format json
          > nuget-vuln.json

      - name: Evaluate vulnerability gate
        shell: pwsh
        run: pwsh -NoProfile -File scripts/Test-NuGetVulnerabilities.ps1 -JsonPath nuget-vuln.json

  image-cve:
    name: image-cve
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repository
        uses: actions/checkout@v6

      - name: Build image
        run: docker build -t vitally-mcp:scan -f Dockerfile .

      - name: Trivy scan (gate on High/Critical)
        uses: aquasecurity/trivy-action@0.28.0
        with:
          image-ref: vitally-mcp:scan
          vuln-type: os,library
          severity: HIGH,CRITICAL
          ignore-unfixed: "false"
          exit-code: "1"
          trivyignores: .trivyignore.yaml

      - name: Trivy scan (SARIF, all severities, non-blocking)
        if: always()
        uses: aquasecurity/trivy-action@0.28.0
        with:
          image-ref: vitally-mcp:scan
          format: sarif
          output: trivy-results.sarif
          exit-code: "0"
          trivyignores: .trivyignore.yaml

      - name: Upload Trivy SARIF
        if: always()
        uses: github/codeql-action/upload-sarif@v3
        with:
          sarif_file: trivy-results.sarif
```

Note: `aquasecurity/trivy-action@0.28.0` is the pin at time of writing — if a newer release exists, pin to it (Dependabot's `github-actions` ecosystem will then keep it current). The repo already runs CodeQL, so code-scanning/SARIF upload is enabled.

- [ ] **Step 3: Validate the workflow and the image build locally**

Run (workflow lint — uses the local Docker that the image job needs anyway):
```
docker run --rm -v "${PWD}:/repo" -w /repo rhysd/actionlint:latest -color
```
Expected: no errors for `.github/workflows/security-scan.yml`.

Run (confirm the Dockerfile builds, i.e. the `image-cve` build step will succeed):
```
docker build -t vitally-mcp:scan -f Dockerfile .
```
Expected: build succeeds.

- [ ] **Step 4: Write the runbook**

Create `docs/runbooks/vulnerability-scanning.md`:
```markdown
# Vulnerability scanning & waivers

The `Security scan` workflow (`.github/workflows/security-scan.yml`) runs on every PR and push to
`main`, weekly (Monday 09:00 UTC), and on manual dispatch. It has two required jobs:

- **`nuget-vuln`** — `dotnet list package --vulnerable` evaluated by
  `scripts/Test-NuGetVulnerabilities.ps1`.
- **`image-cve`** — builds the container image and scans it with Trivy; all findings are uploaded to
  the **Security tab** (Code scanning) as SARIF.

## Policy

- **High and Critical** findings **block** the build (and therefore merge — both jobs are required
  checks on `main`). Moderate/Low are reported but do not block.
- Unfixed CVEs still block; to ship anyway you must add a **documented, time-boxed waiver**.

## Waiving a NuGet finding

Add an entry to `security/nuget-vuln-allowlist.json`:

```json
[
  { "id": "GHSA-xxxx-yyyy-zzzz", "package": "Some.Package", "reason": "Why this is acceptable now", "expires": "2026-09-30" }
]
```

`id` is the GHSA id from the advisory URL in the scan output. `expires` is required; once it passes,
the finding re-blocks and must be reviewed again.

## Waiving an image (Trivy) finding

Add an entry to `.trivyignore.yaml`:

```yaml
vulnerabilities:
  - id: CVE-2025-12345
    statement: "Why this is acceptable now"
    expired_at: 2026-09-30
```

## Where findings appear

- PR checks: the `nuget-vuln` / `image-cve` job logs (with a summary table for NuGet).
- Security tab → Code scanning: Trivy SARIF results (all severities), tracked over time.
- Dependabot alerts: GitHub's own security-advisory matches (separate from this gate).
```

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/security-scan.yml .trivyignore.yaml docs/runbooks/vulnerability-scanning.md
git commit -m @'
feat(security): security-scan workflow + Trivy waiver + runbook (scanning SP1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification (controller — after both tasks merge-ready)

- [ ] `pwsh -NoProfile -File tests/Run-SecurityGateFixtures.ps1` passes locally.
- [ ] `docker build -t vitally-mcp:scan -f Dockerfile .` succeeds locally.
- [ ] Open a PR from `feature/supply-chain-scanning-gating` into `main`. Confirm `nuget-vuln` and
  `image-cve` both run and pass on the current (clean) dependency set.
- [ ] **Negative test (optional, on the PR branch):** temporarily add a known-vulnerable package
  (e.g. an old `System.Net.Http` or a deliberately outdated package), push, confirm `nuget-vuln`
  **fails**; then add a waiver with a future `expires` and confirm it **passes**; revert both before
  merge.
- [ ] Enable Dependabot security updates via `gh api`:
  - `gh api -X PUT repos/fiscaltec/vitally-mcp/vulnerability-alerts`
  - `gh api -X PUT repos/fiscaltec/vitally-mcp/automated-security-fixes`
- [ ] After the first workflow run (so the check contexts exist), add `nuget-vuln` and `image-cve`
  to the `main` ruleset's `required_status_checks` via `gh api` (read the current ruleset, append the
  two contexts, PUT it back).
- [ ] CI green; resolve any Copilot/CodeQL review threads (the `main` ruleset requires resolution);
  merge (squash).
