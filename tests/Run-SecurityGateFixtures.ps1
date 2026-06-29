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
