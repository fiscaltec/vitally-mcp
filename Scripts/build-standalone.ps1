#Requires -Version 7.0

<#
.SYNOPSIS
    Builds the Vitally MCP server as a standalone executable.

.DESCRIPTION
    This script builds a self-contained, single-file executable of the Vitally MCP server
    without creating an MCPB package. Useful for development and testing.

.PARAMETER Architecture
    Optional. Specify the target architecture (win-x64 or win-arm64).
    If not specified, the script will auto-detect based on the system.

.PARAMETER Configuration
    Build configuration (Debug or Release). Default is Release.

.PARAMETER SkipVersionBump
    Optional. Skip the automatic version bump step.

.EXAMPLE
    .\build-standalone.ps1

.EXAMPLE
    .\build-standalone.ps1 -Architecture win-x64 -Configuration Debug

.EXAMPLE
    .\build-standalone.ps1 -SkipVersionBump

.NOTES
    Prerequisites:
    - .NET 10 SDK must be installed
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Architecture,

    [Parameter(Mandatory=$false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter(Mandatory=$false)]
    [switch]$SkipVersionBump
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to script location
$ScriptDir = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $ScriptDir

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Vitally MCP Server - Standalone Build" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# Step 0: Bump version
if (-not $SkipVersionBump) {
    Write-Host "[0/3] Bumping version..." -ForegroundColor Cyan
    $bumpScript = Join-Path $ScriptDir "bump-version.ps1"
    $newVersion = & $bumpScript -BumpType Revision
    Write-Host ""
} else {
    Write-Host "[0/3] Skipping version bump..." -ForegroundColor Yellow
    # Read current version
    [xml]$csproj = Get-Content (Join-Path $ProjectRoot "VitallyMcp.csproj")
    $newVersion = $csproj.Project.PropertyGroup.Version
    Write-Host "Current version: $newVersion" -ForegroundColor Green
    Write-Host ""
}

# Detect architecture if not specified
if (-not $Architecture) {
    $arch = $env:PROCESSOR_ARCHITECTURE
    Write-Host "Detecting system architecture: $arch" -ForegroundColor Yellow

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
}

Write-Host "Configuration: $Configuration" -ForegroundColor Green
Write-Host "Architecture: $Architecture" -ForegroundColor Green
Write-Host ""

# Build the server
Write-Host "[1/3] Publishing VitallyMcp server..." -ForegroundColor Cyan
$publishOutput = Join-Path $ProjectRoot "bin\$Configuration\net10.0\$Architecture\publish"
$projectFile = Join-Path $ProjectRoot "VitallyMcp.csproj"

Push-Location $ProjectRoot
try {
    dotnet publish $projectFile `
        --configuration $Configuration `
        --runtime $Architecture `
        --self-contained true `
        --output $publishOutput `
        /p:PublishSingleFile=true `
        /p:PublishTrimmed=false
} finally {
    Pop-Location
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish VitallyMcp server"
    exit 1
}

Write-Host ""
Write-Host "[2/3] Renaming binary with version suffix..." -ForegroundColor Cyan
$originalExe = Join-Path $publishOutput "VitallyMcp.exe"
$versionedExe = Join-Path $publishOutput "VitallyMcp-$newVersion.exe"

if (Test-Path $versionedExe) {
    Remove-Item $versionedExe -Force
}

Rename-Item -Path $originalExe -NewName "VitallyMcp-$newVersion.exe" -Force

if (-not (Test-Path $versionedExe)) {
    Write-Error "Failed to rename executable to versioned name"
    exit 1
}

Write-Host "✓ Binary renamed successfully" -ForegroundColor Green
Write-Host ""

# Copy binary to Output folder
Write-Host "[3/3] Copying binary to Output folder..." -ForegroundColor Cyan
$outputDir = Join-Path $ProjectRoot "Output"

# Ensure Output directory exists
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

$outputLatestExe = Join-Path $outputDir "VitallyMcp.exe"

# Copy as VitallyMcp.exe (no version prefix)
Copy-Item -Path $versionedExe -Destination $outputLatestExe -Force
Write-Host "✓ Copied: VitallyMcp.exe" -ForegroundColor Green

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $newVersion" -ForegroundColor Yellow
Write-Host ""
Write-Host "Executable locations:" -ForegroundColor Yellow
Write-Host "  Build output (versioned): $versionedExe" -ForegroundColor White
Write-Host "  Output: $outputLatestExe" -ForegroundColor White
Write-Host ""
Write-Host "To use with Claude Desktop, add this to your config:" -ForegroundColor Yellow
Write-Host ""

$escapedPath = $publishOutput -replace '\\', '\\'
$configExample = @"
{
  "mcpServers": {
    "vitally": {
      "command": "$escapedPath\\VitallyMcp-$newVersion.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_your_api_key_here",
        "VITALLY_SUBDOMAIN": "your-subdomain"
      }
    }
  }
}
"@

Write-Host $configExample -ForegroundColor White
Write-Host ""
Write-Host "Configuration file location:" -ForegroundColor Yellow
Write-Host "  %APPDATA%\Claude\claude_desktop_config.json" -ForegroundColor White
Write-Host ""
