#Requires -Version 7.0

<#
.SYNOPSIS
    Comprehensive build script that bumps version, builds standalone, and creates MCPB package.

.DESCRIPTION
    This script performs a complete build process:
    1. Bumps the revision version number
    2. Builds standalone executables for both x64 and ARM64 (or specified architecture)
    3. Creates an MCPB package for distribution

.PARAMETER Architecture
    Optional. Specify the target architecture (win-x64 or win-arm64).
    If not specified, builds for the current system architecture.

.PARAMETER SkipStandalone
    Optional. Skip the standalone build and only build MCPB package.

.PARAMETER BumpType
    Optional. Type of version bump (Revision, Minor, Major). Default is Revision.

.EXAMPLE
    .\build-all.ps1
    Bumps revision, builds standalone and MCPB for current architecture

.EXAMPLE
    .\build-all.ps1 -Architecture win-x64
    Builds for x64 architecture only

.EXAMPLE
    .\build-all.ps1 -SkipStandalone
    Only builds MCPB package, skips standalone build

.EXAMPLE
    .\build-all.ps1 -BumpType Minor
    Bumps minor version instead of revision

.NOTES
    Prerequisites:
    - .NET 10 SDK must be installed
    - Node.js must be installed (for MCPB packaging)
    - Run: npm install -g @anthropic-ai/mcpb (first time only)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Architecture,

    [Parameter(Mandatory=$false)]
    [switch]$SkipStandalone,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Revision", "Minor", "Major")]
    [string]$BumpType = "Revision"
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to script location
$ScriptDir = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $ScriptDir

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "  Vitally MCP Server - Complete Build  " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Bump version
Write-Host "[Step 1/3] Bumping version ($BumpType)..." -ForegroundColor Cyan
$bumpScript = Join-Path $ScriptDir "bump-version.ps1"
$newVersion = & $bumpScript -BumpType $BumpType
Write-Host ""

# Detect architecture if not specified
if (-not $Architecture) {
    $arch = $env:PROCESSOR_ARCHITECTURE
    Write-Host "Auto-detecting architecture: $arch" -ForegroundColor Yellow

    if ($arch -eq "ARM64") {
        $Architecture = "win-arm64"
    }
    elseif ($arch -eq "AMD64" -or $arch -eq "x64") {
        $Architecture = "win-x64"
    }
    else {
        Write-Error "Unsupported architecture: $arch. Supported: AMD64, ARM64"
        exit 1
    }
    Write-Host "Target architecture: $Architecture" -ForegroundColor Green
    Write-Host ""
}

# Step 2: Build standalone (optional)
if (-not $SkipStandalone) {
    Write-Host "[Step 2/3] Building standalone executable..." -ForegroundColor Cyan
    Write-Host ""

    $standaloneScript = Join-Path $ScriptDir "build-standalone.ps1"
    & $standaloneScript -Architecture $Architecture -SkipVersionBump

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Standalone build failed"
        exit 1
    }
    Write-Host ""
} else {
    Write-Host "[Step 2/3] Skipping standalone build..." -ForegroundColor Yellow
    Write-Host ""
}

# Step 3: Build MCPB package
Write-Host "[Step 3/3] Building MCPB package..." -ForegroundColor Cyan
Write-Host ""

$mcpbScript = Join-Path $ScriptDir "build-mcpb.ps1"
& $mcpbScript -Architecture $Architecture -SkipVersionBump

if ($LASTEXITCODE -ne 0) {
    Write-Error "MCPB build failed"
    exit 1
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "       All builds complete!             " -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $newVersion" -ForegroundColor Yellow
Write-Host "Architecture: $Architecture" -ForegroundColor Yellow
Write-Host ""

if (-not $SkipStandalone) {
    $standaloneExe = Join-Path $ProjectRoot "bin\Release\net10.0\$Architecture\publish\VitallyMcp-$newVersion.exe"
    Write-Host "Standalone executable:" -ForegroundColor Green
    Write-Host "  $standaloneExe" -ForegroundColor White
    Write-Host ""
}

$mcpbPackage = Join-Path $ProjectRoot "Output\VitallyMcp-$newVersion.mcpb"
Write-Host "MCPB package:" -ForegroundColor Green
Write-Host "  $mcpbPackage" -ForegroundColor White
Write-Host ""
Write-Host "Ready for distribution!" -ForegroundColor Green
Write-Host ""
