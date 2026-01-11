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
    [switch]$NoZip,
    [switch]$ClearDisclaimerAcceptances  # Clear disclaimer acceptances for rollout
)

$ErrorActionPreference = "Stop"

# Configuration
$ScriptDir = $PSScriptRoot
$Project = Join-Path $ScriptDir "DiffusionNexus.UI\DiffusionNexus.UI.csproj"
$PropsFile = Join-Path $ScriptDir "Directory.Build.props"
$Configuration = "Release"
$Runtime = "win-x64"
$OutputDir = Join-Path $ScriptDir "publish"

# Database paths - matches DiffusionNexusCoreDbContext.DatabaseFileName
$DbFilename = "Diffusion_Nexus-core.db"
$UserDbDir = Join-Path $env:LOCALAPPDATA "DiffusionNexus\Data"
$UserDbPath = Join-Path $UserDbDir $DbFilename

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

# Clean first to ensure fresh resource embedding
Write-Host "Cleaning project artifacts..."
& dotnet clean $Project --configuration $Configuration --verbosity quiet

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

# Clean up unnecessary build artifacts
Write-SubHeader "Cleaning Build Artifacts"
$artifactsRemoved = 0

# Remove EF Core design-time build hosts (not needed at runtime)
$buildHost472 = Join-Path $OutputDir "BuildHost-net472"
$buildHostNetcore = Join-Path $OutputDir "BuildHost-netcore"
if (Test-Path $buildHost472) {
    Remove-Item -Path $buildHost472 -Recurse -Force
    Write-Host "Removed: BuildHost-net472" -ForegroundColor Gray
    $artifactsRemoved++
}
if (Test-Path $buildHostNetcore) {
    Remove-Item -Path $buildHostNetcore -Recurse -Force
    Write-Host "Removed: BuildHost-netcore" -ForegroundColor Gray
    $artifactsRemoved++
}

# Remove ONNX runtime static library stub (not needed at runtime)
$onnxLib = Join-Path $OutputDir "onnxruntime.lib"
if (Test-Path $onnxLib) {
    Remove-Item -Path $onnxLib -Force
    Write-Host "Removed: onnxruntime.lib" -ForegroundColor Gray
    $artifactsRemoved++
}

if ($artifactsRemoved -gt 0) {
    Write-Host "Cleaned up $artifactsRemoved unnecessary build artifacts." -ForegroundColor Green
} else {
    Write-Host "No build artifacts to clean." -ForegroundColor Gray
}

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
        
        # Clear DisclaimerAcceptances table for rollout if requested
        $shouldSanitize = $ClearDisclaimerAcceptances

        if (-not $shouldSanitize -and -not $SkipDatabasePrompt) {
            Write-Host ""
            $resp = Read-Host "Sanitize database (clear personal paths, history, backups)? (Y/N)"
            $shouldSanitize = $resp -eq "Y" -or $resp -eq "y"
        }

        if ($shouldSanitize) {
            Write-SubHeader "Sanitizing Database for Release"
            
            try {
                # Create a temporary .NET project to execute the SQLite command
                $tempDir = Join-Path $env:TEMP "DiffusionNexus_DbCleanup_$(Get-Random)"
                New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
                
                $csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
  </ItemGroup>
</Project>
"@
                
                $programContent = @"
using Microsoft.Data.Sqlite;

var dbPath = args[0];
using var connection = new SqliteConnection(`$"Data Source={dbPath}");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = @"
    DELETE FROM [DisclaimerAcceptances];
    DELETE FROM [LoraSources];
    UPDATE [AppSettings] 
    SET AutoBackupEnabled = 0, 
        AutoBackupLocation = NULL,
        DatasetStoragePath = NULL,
        LoraSortSourcePath = NULL,
        LoraSortTargetPath = NULL;
";
var rows = command.ExecuteNonQuery();

Console.WriteLine(`$"Sanitization complete. Database reset for rollout (Rows modified: {rows}).");
"@
                
                Set-Content -Path (Join-Path $tempDir "cleanup.csproj") -Value $csprojContent
                Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $programContent
                
                Push-Location $tempDir
                try {
                    Write-Host "Building SQLite cleanup tool..." -ForegroundColor Gray
                    & dotnet build -c Release --verbosity quiet 2>&1 | Out-Null
                    
                    if ($LASTEXITCODE -eq 0) {
                        $result = & dotnet run -c Release --no-build -- $destDbPath 2>&1
                        Write-Host $result -ForegroundColor Green
                    } else {
                        Write-Host "WARNING: Could not build cleanup tool." -ForegroundColor Yellow
                    }
                }
                finally {
                    Pop-Location
                }
                
                # Cleanup temp directory
                Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                Write-Host "WARNING: Failed to clear DisclaimerAcceptances: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
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
