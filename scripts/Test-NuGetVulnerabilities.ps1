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
