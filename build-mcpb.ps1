#Requires -Version 7.0

<#
.SYNOPSIS
    Builds and packages the Vitally MCP server as an MCPB bundle.

.DESCRIPTION
    This script:
    1. Detects the system architecture (x64 or ARM64)
    2. Publishes the VitallyMcp server as a self-contained executable
    3. Copies the executable to the MCPB server directory
    4. Creates an installable .mcpb bundle file

.PARAMETER Architecture
    Optional. Specify the target architecture (win-x64 or win-arm64).
    If not specified, the script will auto-detect based on the system.

.EXAMPLE
    .\build-mcpb.ps1

.EXAMPLE
    .\build-mcpb.ps1 -Architecture win-x64

.NOTES
    Prerequisites:
    - .NET 10 SDK must be installed
    - Node.js must be installed (for @anthropic-ai/mcpb)
    - Run: npm install -g @anthropic-ai/mcpb (first time only)
#>

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Architecture
)

$ErrorActionPreference = "Stop"

Write-Host "=====================================" -ForegroundColor Cyan
Write-Host " Vitally MCP Server - MCPB Builder" -ForegroundColor Cyan
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

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
Write-Host "[1/4] Publishing VitallyMcp server..." -ForegroundColor Cyan
$publishOutput = Join-Path $PSScriptRoot "bin\Release\net10.0\$Architecture\publish"

dotnet publish VitallyMcp.csproj `
    --configuration Release `
    --runtime $Architecture `
    --self-contained true `
    --output $publishOutput `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish VitallyMcp server"
    exit 1
}

Write-Host "✓ Server published successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Copy executable to MCPB directory
Write-Host "[2/4] Staging files for MCPB package..." -ForegroundColor Cyan
$exePath = Join-Path $publishOutput "VitallyMcp.exe"
$targetDir = Join-Path $PSScriptRoot "mcpb\server"
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
Write-Host "[3/4] Creating MCPB bundle..." -ForegroundColor Cyan

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
$mcpbDir = Join-Path $PSScriptRoot "mcpb"
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

# Step 4: Move MCPB file to root
Write-Host "[4/4] Finalizing package..." -ForegroundColor Cyan

$mcpbFile = Get-ChildItem -Path $mcpbDir -Filter "*.mcpb" | Select-Object -First 1
if ($mcpbFile) {
    $finalPath = Join-Path $PSScriptRoot $mcpbFile.Name
    Move-Item -Path $mcpbFile.FullName -Destination $finalPath -Force
    Write-Host "✓ Package ready: $finalPath" -ForegroundColor Green
}
else {
    Write-Warning "MCPB file not found in mcpb directory"
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
