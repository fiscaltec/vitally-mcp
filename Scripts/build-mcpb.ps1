#Requires -Version 7.0

<#
.SYNOPSIS
    Builds and packages the Vitally MCP server as an MCPB bundle.

.DESCRIPTION
    This script:
    1. Bumps the revision version number
    2. Detects the system architecture (x64 or ARM64)
    3. Publishes the VitallyMcp server as a self-contained executable
    4. Copies the executable to the MCPB directory
    5. Creates an installable .mcpb bundle file with version number

.PARAMETER Architecture
    Optional. Specify the target architecture (win-x64 or win-arm64).
    If not specified, the script will auto-detect based on the system.

.PARAMETER SkipVersionBump
    Optional. Skip the automatic version bump step.

.EXAMPLE
    .\build-mcpb.ps1

.EXAMPLE
    .\build-mcpb.ps1 -Architecture win-x64

.EXAMPLE
    .\build-mcpb.ps1 -SkipVersionBump

.NOTES
    Prerequisites:
    - .NET 10 SDK must be installed
    - Node.js must be installed (for @anthropic-ai/mcpb)
    - Run: npm install -g @anthropic-ai/mcpb (first time only)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Architecture,

    [Parameter(Mandatory=$false)]
    [switch]$SkipVersionBump
)

$ErrorActionPreference = "Stop"

# Resolve paths relative to script location
$ScriptDir = $PSScriptRoot
$ProjectRoot = Split-Path -Parent $ScriptDir

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host " Vitally MCP Server - MCPB Builder" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

# Step 0: Bump version
if (-not $SkipVersionBump) {
    Write-Host "[0/5] Bumping version..." -ForegroundColor Cyan
    $bumpScript = Join-Path $ScriptDir "bump-version.ps1"
    $newVersion = & $bumpScript -BumpType Revision
    Write-Host ""
} else {
    Write-Host "[0/5] Skipping version bump..." -ForegroundColor Yellow
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

Write-Host "Target architecture: $Architecture" -ForegroundColor Green
Write-Host ""

# Step 1: Build the server
Write-Host "[1/5] Publishing VitallyMcp server..." -ForegroundColor Cyan
$publishOutput = Join-Path $ProjectRoot "bin\Release\net10.0\$Architecture\publish"
$projectFile = Join-Path $ProjectRoot "VitallyMcp.csproj"

Push-Location $ProjectRoot
try {
    dotnet publish $projectFile `
        --configuration Release `
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

Write-Host "✓ Server published successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Copy executable to MCPB directory
Write-Host "[2/5] Staging files for MCPB package..." -ForegroundColor Cyan
$exePath = Join-Path $publishOutput "VitallyMcp.exe"
$targetDir = Join-Path $ProjectRoot "Output\mcpb"
$targetPath = Join-Path $targetDir "VitallyMcp.exe"

if (-not (Test-Path $exePath)) {
    Write-Error "Published executable not found at: $exePath"
    exit 1
}

# Ensure target directory exists
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

# Copy the executable
Copy-Item -Path $exePath -Destination $targetPath -Force

if (-not (Test-Path $targetPath)) {
    Write-Error "Failed to copy executable to MCPB directory"
    exit 1
}

Write-Host "✓ Files staged successfully" -ForegroundColor Green
Write-Host ""

# Step 3: Create MCPB bundle
Write-Host "[3/5] Creating MCPB bundle..." -ForegroundColor Cyan

# Check if mcpb CLI is available
$mcpbPath = (Get-Command mcpb -ErrorAction SilentlyContinue)
if (-not $mcpbPath) {
    Write-Warning "mcpb CLI not found. Installing @anthropic-ai/mcpb globally..."
    npm install -g @anthropic-ai/mcpb

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to install @anthropic-ai/mcpb. Please install Node.js first: winget install OpenJS.NodeJS.LTS"
        exit 1
    }
}

# Pack the MCPB
$mcpbDir = Join-Path $ProjectRoot "Output\mcpb"
Push-Location $mcpbDir

try {
    mcpb pack

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to create MCPB bundle"
        exit 1
    }
}
finally {
    Pop-Location
}

Write-Host "✓ MCPB bundle created successfully" -ForegroundColor Green
Write-Host ""

# Step 4: Move and rename MCPB file
Write-Host "[4/5] Finalizing package..." -ForegroundColor Cyan

$mcpbFile = Get-ChildItem -Path $mcpbDir -Filter "*.mcpb" | Select-Object -First 1
if ($mcpbFile) {
    $outputDir = Join-Path $ProjectRoot "Output"
    $versionedFileName = "VitallyMcp-$newVersion.mcpb"
    $finalPath = Join-Path $outputDir $versionedFileName

    # Remove old versioned MCPB files
    Get-ChildItem -Path $outputDir -Filter "VitallyMcp-*.mcpb" | Remove-Item -Force

    Move-Item -Path $mcpbFile.FullName -Destination $finalPath -Force
    Write-Host "✓ Package ready: $finalPath" -ForegroundColor Green
}
else {
    Write-Warning "MCPB file not found in mcpb directory"
}

# Step 5: Cleanup
Write-Host "[5/5] Cleaning up..." -ForegroundColor Cyan
# Remove the executable from the mcpb directory to avoid bloat in git
# (it will be regenerated on next build)
if (Test-Path $targetPath) {
    Remove-Item $targetPath -Force
    Write-Host "✓ Cleaned up temporary files" -ForegroundColor Green
}

Write-Host ""
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "To install the Vitally MCP server:" -ForegroundColor Yellow
Write-Host "  1. Double-click the .mcpb file" -ForegroundColor White
Write-Host "  2. Configure environment variables in your MCP client:" -ForegroundColor White
Write-Host "     - VITALLY_API_KEY: Your Vitally API key" -ForegroundColor White
Write-Host "     - VITALLY_SUBDOMAIN: Your Vitally subdomain" -ForegroundColor White
Write-Host ""
