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

.EXAMPLE
    .\build-standalone.ps1

.EXAMPLE
    .\build-standalone.ps1 -Architecture win-x64 -Configuration Debug

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
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Vitally MCP Server - Standalone Build" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
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

Write-Host "Configuration: $Configuration" -ForegroundColor Green
Write-Host "Architecture: $Architecture" -ForegroundColor Green
Write-Host ""

# Build the server
Write-Host "Publishing VitallyMcp server..." -ForegroundColor Cyan
$publishOutput = Join-Path $PSScriptRoot "bin\$Configuration\net10.0\$Architecture\publish"

dotnet publish VitallyMcp.csproj `
    --configuration $Configuration `
    --runtime $Architecture `
    --self-contained true `
    --output $publishOutput `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish VitallyMcp server"
    exit 1
}

Write-Host ""
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Executable location:" -ForegroundColor Yellow
Write-Host "  $publishOutput\VitallyMcp.exe" -ForegroundColor White
Write-Host ""
Write-Host "To use with Claude Desktop, add this to your config:" -ForegroundColor Yellow
Write-Host ""

$escapedPath = $publishOutput -replace '\\', '\\'
$configExample = @"
{
  "mcpServers": {
    "vitally": {
      "command": "$escapedPath\\VitallyMcp.exe",
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
