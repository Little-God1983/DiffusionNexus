# ============================================================
# DiffusionNexus.UI - Single File Publish Script
# ============================================================
# This script builds and publishes DiffusionNexus.UI as a single 
# self-contained executable file with optional portable database.
# Creates both unzipped output for testing and a versioned ZIP file.
# ============================================================

param(
    [switch]$SkipDatabasePrompt,
    [switch]$IncludeDatabase,
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

# Configuration
$ScriptDir = $PSScriptRoot
$Project = Join-Path $ScriptDir "DiffusionNexus.UI\DiffusionNexus.UI.csproj"
$PropsFile = Join-Path $ScriptDir "Directory.Build.props"
$Configuration = "Release"
$Runtime = "win-x64"
$OutputDir = Join-Path $ScriptDir "publish"

# Database paths
$UserDbPath = Join-Path $env:LOCALAPPDATA "diffusion_nexus.db"
$DbFilename = "diffusion_nexus.db"

function Write-Header {
    param([string]$Text)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host $Text -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Write-SubHeader {
    param([string]$Text)
    Write-Host ""
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan
    Write-Host $Text -ForegroundColor DarkCyan
    Write-Host "------------------------------------------------------------" -ForegroundColor DarkCyan
}

function Get-CurrentVersion {
    if (-not (Test-Path $PropsFile)) {
        return $null
    }
    
    $content = Get-Content $PropsFile -Raw
    if ($content -match '<Version>(\d+\.\d+\.\d+\.\d+)</Version>') {
        return $matches[1]
    }
    return $null
}

# ============================================================
# MAIN SCRIPT
# ============================================================

Write-Header "DiffusionNexus.UI - Single File Publisher"

# Validate required files
if (-not (Test-Path $PropsFile)) {
    Write-Host "ERROR: Directory.Build.props not found at: $PropsFile" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $Project)) {
    Write-Host "ERROR: Project file not found at: $Project" -ForegroundColor Red
    exit 1
}

# Get current version from Directory.Build.props
$Version = Get-CurrentVersion
if (-not $Version) {
    Write-Host "ERROR: Could not read version from Directory.Build.props" -ForegroundColor Red
    exit 1
}

Write-Host "Current Version: $Version" -ForegroundColor Green
Write-Host ""
Write-Host "Configuration: $Configuration"
Write-Host "Runtime:       $Runtime"
Write-Host "Output:        $OutputDir"

# Clean previous publish output
Write-SubHeader "Cleaning Previous Output"
if (Test-Path $OutputDir) {
    Write-Host "Removing previous publish folder..."
    Remove-Item -Path $OutputDir -Recurse -Force
}

# Also clean any existing zip files for this version
$ZipFileName = "diffusion_nexus.V$Version.zip"
$ZipPath = Join-Path $ScriptDir $ZipFileName
if (Test-Path $ZipPath) {
    Write-Host "Removing previous zip file: $ZipFileName"
    Remove-Item -Path $ZipPath -Force
}

# Build and publish
Write-SubHeader "Building and Publishing"

$publishResult = & dotnet publish $Project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $OutputDir `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "BUILD SUCCESSFUL!" -ForegroundColor Green

# Database handling
Write-SubHeader "Portable Database Setup"

Write-Host "The app uses portable-first database resolution:"
Write-Host "  1. First checks for: [exe folder]\$DbFilename"
Write-Host "  2. Falls back to:    `$env:LOCALAPPDATA\$DbFilename"
Write-Host ""

$IncludeDatabaseFinal = $false

if (Test-Path $UserDbPath) {
    Write-Host "Found database at: $UserDbPath" -ForegroundColor Green
    
    if ($SkipDatabasePrompt) {
        $IncludeDatabaseFinal = $IncludeDatabase
        if ($IncludeDatabaseFinal) {
            Write-Host "Including database (--IncludeDatabase flag)" -ForegroundColor Yellow
        } else {
            Write-Host "Skipping database (no --IncludeDatabase flag)" -ForegroundColor Yellow
        }
    } else {
        $response = Read-Host "Include database for portable distribution? (Y/N)"
        $IncludeDatabaseFinal = $response -eq "Y" -or $response -eq "y"
    }
    
    if ($IncludeDatabaseFinal) {
        $destDbPath = Join-Path $OutputDir $DbFilename
        Copy-Item -Path $UserDbPath -Destination $destDbPath
        Write-Host "Database copied to publish folder." -ForegroundColor Green
    } else {
        Write-Host "Skipping database. Users will need existing configs in `$env:LOCALAPPDATA." -ForegroundColor Yellow
    }
} else {
    Write-Host "No database found at: $UserDbPath" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To create a portable distribution with pre-configured settings:"
    Write-Host "  1. Run the Configurator to create configurations"
    Write-Host "  2. Run this script again"
}

# Show output files
Write-SubHeader "Output Files (Unzipped - For Quick Testing)"
Get-ChildItem -Path $OutputDir | Format-Table Name, Length, LastWriteTime

# Create ZIP file
if (-not $NoZip) {
    Write-SubHeader "Creating ZIP Distribution"
    
    $ZipFileName = "diffusion_nexus.V$Version.zip"
    $ZipPath = Join-Path $ScriptDir $ZipFileName
    
    Write-Host "Creating: $ZipFileName" -ForegroundColor Cyan
    
    # Use Compress-Archive to create the ZIP
    Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipPath -Force
    
    if (Test-Path $ZipPath) {
        $ZipInfo = Get-Item $ZipPath
        $ZipSizeMB = [math]::Round($ZipInfo.Length / 1MB, 2)
        
        Write-Host ""
        Write-Host "ZIP file created successfully!" -ForegroundColor Green
        Write-Host "  File: $ZipFileName" -ForegroundColor White
        Write-Host "  Size: $ZipSizeMB MB" -ForegroundColor White
        Write-Host "  Path: $ZipPath" -ForegroundColor Gray
    } else {
        Write-Host "WARNING: Failed to create ZIP file" -ForegroundColor Yellow
    }
}

# Summary
Write-Header "PUBLISH COMPLETE"

Write-Host "Version:     $Version" -ForegroundColor Green
Write-Host "Unzipped:    $OutputDir" -ForegroundColor White

if (-not $NoZip) {
    Write-Host "ZIP File:    $ZipFileName" -ForegroundColor White
}

if ($IncludeDatabaseFinal) {
    Write-Host "Database:    Included (portable mode)" -ForegroundColor Green
} else {
    Write-Host "Database:    Not included (standard mode)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Distribution options:" -ForegroundColor Cyan
Write-Host ""
Write-Host "  For Testing:" -ForegroundColor Yellow
Write-Host "    - Use files in: $OutputDir"
Write-Host ""
Write-Host "  For Distribution:" -ForegroundColor Yellow
Write-Host "    - Share: $ZipFileName"
Write-Host ""

# Keep window open if running interactively
if (-not $SkipDatabasePrompt) {
    Write-Host ""
    Read-Host "Press Enter to exit"
}
