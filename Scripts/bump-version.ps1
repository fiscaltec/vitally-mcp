#Requires -Version 7.0

<#
.SYNOPSIS
    Bumps the version number in both VitallyMcp.csproj and Output/mcpb/manifest.json.

.DESCRIPTION
    This script reads the current version from VitallyMcp.csproj, increments it based on
    the specified bump type (Revision, Minor, or Major), and updates both the project file
    and the MCPB manifest file.

.PARAMETER BumpType
    The type of version bump to perform:
    - Revision (default): Increments the revision number (1.1.2 -> 1.1.3)
    - Minor: Increments the minor version and resets revision (1.1.2 -> 1.2.0)
    - Major: Increments the major version and resets minor and revision (1.1.2 -> 2.0.0)

.EXAMPLE
    .\bump-version.ps1
    Bumps the revision number (default)

.EXAMPLE
    .\bump-version.ps1 -BumpType Minor
    Bumps the minor version number

.EXAMPLE
    .\bump-version.ps1 -BumpType Major
    Bumps the major version number
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("Revision", "Minor", "Major")]
    [string]$BumpType = "Revision"
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to script location
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $ProjectRoot "VitallyMcp.csproj"
$ManifestPath = Join-Path $ProjectRoot "Output\mcpb\manifest.json"

# Validate files exist
if (-not (Test-Path $CsprojPath)) {
    Write-Error "Project file not found: $CsprojPath"
    exit 1
}

if (-not (Test-Path $ManifestPath)) {
    Write-Error "Manifest file not found: $ManifestPath"
    exit 1
}

# Read and parse current version from .csproj
[xml]$csproj = Get-Content $CsprojPath
$currentVersion = $csproj.Project.PropertyGroup.Version

if (-not $currentVersion) {
    Write-Error "Version not found in $CsprojPath"
    exit 1
}

# Parse version components
$versionParts = $currentVersion.Split('.')
if ($versionParts.Length -ne 3) {
    Write-Error "Invalid version format: $currentVersion (expected Major.Minor.Revision)"
    exit 1
}

$major = [int]$versionParts[0]
$minor = [int]$versionParts[1]
$revision = [int]$versionParts[2]

# Bump version based on type
switch ($BumpType) {
    "Major" {
        $major++
        $minor = 0
        $revision = 0
    }
    "Minor" {
        $minor++
        $revision = 0
    }
    "Revision" {
        $revision++
    }
}

$newVersion = "$major.$minor.$revision"

Write-Host "Version Bump: $currentVersion -> $newVersion ($BumpType)" -ForegroundColor Cyan
Write-Host ""

# Update .csproj file
Write-Host "Updating $CsprojPath..." -ForegroundColor Yellow
$csproj.Project.PropertyGroup.Version = $newVersion
$csproj.Save($CsprojPath)
Write-Host "✓ Updated VitallyMcp.csproj" -ForegroundColor Green

# Update manifest.json file
Write-Host "Updating $ManifestPath..." -ForegroundColor Yellow
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$manifest.version = $newVersion
$manifest | ConvertTo-Json -Depth 10 | Set-Content $ManifestPath -Encoding UTF8
Write-Host "✓ Updated manifest.json" -ForegroundColor Green

Write-Host ""
Write-Host "Version successfully bumped to $newVersion" -ForegroundColor Green
Write-Host ""

# Return the new version for use by calling scripts
return $newVersion
