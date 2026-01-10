# ============================================================
# DiffusionNexus - Rollout Cleanup Script
# ============================================================
# This script performs cleanup tasks needed for rollout/updates.
# Currently clears the DisclaimerAcceptances table to force
# users to re-accept the disclaimer after an update.
# ============================================================

param(
    [switch]$Force,           # Skip confirmation prompts
    [string]$DatabasePath     # Optional: specify database path directly
)

$ErrorActionPreference = "Stop"

# Configuration - matches DiffusionNexusCoreDbContext settings
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

function Find-Database {
    param([string]$CustomPath)
    
    # If custom path provided, use it
    if ($CustomPath -and (Test-Path $CustomPath)) {
        return $CustomPath
    }
    
    # Check portable mode (next to script/executable)
    $ScriptDir = $PSScriptRoot
    $PortableDbPath = Join-Path $ScriptDir $DbFilename
    if (Test-Path $PortableDbPath) {
        Write-Host "Found portable database: $PortableDbPath" -ForegroundColor Green
        return $PortableDbPath
    }
    
    # Check AppData location
    if (Test-Path $UserDbPath) {
        Write-Host "Found user database: $UserDbPath" -ForegroundColor Green
        return $UserDbPath
    }
    
    # Check publish folder (for pre-rollout cleanup)
    $PublishDbPath = Join-Path $ScriptDir "publish\$DbFilename"
    if (Test-Path $PublishDbPath) {
        Write-Host "Found publish database: $PublishDbPath" -ForegroundColor Green
        return $PublishDbPath
    }
    
    return $null
}

function Invoke-SqliteCommand {
    param(
        [string]$DatabasePath,
        [string]$Query
    )
    
    # Try using System.Data.SQLite via PowerShell
    # First, check if we can use the dotnet approach
    $connectionString = "Data Source=$DatabasePath"
    
    try {
        # Try loading Microsoft.Data.Sqlite
        Add-Type -Path (Join-Path $PSScriptRoot "publish\Microsoft.Data.Sqlite.dll") -ErrorAction SilentlyContinue
    }
    catch {
        # Ignore - will try alternative method
    }
    
    # Use dotnet to execute the query via a simple inline program
    $tempScript = @"
using Microsoft.Data.Sqlite;
using var connection = new SqliteConnection("$connectionString");
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = @"$Query";
var affected = command.ExecuteNonQuery();
Console.WriteLine(affected);
"@
    
    # Alternative: Use sqlite3.exe if available
    $sqlite3 = Get-Command "sqlite3" -ErrorAction SilentlyContinue
    if ($sqlite3) {
        Write-Host "Using sqlite3 command-line tool..." -ForegroundColor Gray
        $result = & sqlite3 $DatabasePath $Query 2>&1
        return $true
    }
    
    # Alternative: Use .NET Core's dotnet-script or direct ADO.NET
    # For simplicity, we'll create a temporary .NET console app
    $tempDir = Join-Path $env:TEMP "DiffusionNexus_Rollout_$(Get-Random)"
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    
    try {
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
var query = args[1];

using var connection = new SqliteConnection(`$"Data Source={dbPath}");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = query;
var affected = command.ExecuteNonQuery();

Console.WriteLine(`$"Rows affected: {affected}");
"@
        
        Set-Content -Path (Join-Path $tempDir "rollout.csproj") -Value $csprojContent
        Set-Content -Path (Join-Path $tempDir "Program.cs") -Value $programContent
        
        Push-Location $tempDir
        try {
            Write-Host "Building temporary SQLite executor..." -ForegroundColor Gray
            $buildResult = & dotnet build -c Release --verbosity quiet 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed: $buildResult"
            }
            
            Write-Host "Executing query..." -ForegroundColor Gray
            $runResult = & dotnet run -c Release --no-build -- $DatabasePath $Query 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Execution failed: $runResult"
            }
            
            Write-Host $runResult -ForegroundColor Green
            return $true
        }
        finally {
            Pop-Location
        }
    }
    finally {
        # Cleanup temp directory
        if (Test-Path $tempDir) {
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ============================================================
# MAIN SCRIPT
# ============================================================

Write-Header "DiffusionNexus - Rollout Cleanup"

# Find database
Write-SubHeader "Locating Database"

$DbPath = Find-Database -CustomPath $DatabasePath

if (-not $DbPath) {
    Write-Host "ERROR: Database not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Searched locations:" -ForegroundColor Yellow
    Write-Host "  1. Custom path: $DatabasePath" -ForegroundColor Gray
    Write-Host "  2. Portable:    $(Join-Path $PSScriptRoot $DbFilename)" -ForegroundColor Gray
    Write-Host "  3. User data:   $UserDbPath" -ForegroundColor Gray
    Write-Host "  4. Publish:     $(Join-Path $PSScriptRoot 'publish\' + $DbFilename)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Please specify the database path using -DatabasePath parameter." -ForegroundColor Yellow
    exit 1
}

Write-Host "Database: $DbPath" -ForegroundColor White
Write-Host ""

# Confirm action
if (-not $Force) {
    Write-Host "This script will perform the following cleanup:" -ForegroundColor Yellow
    Write-Host "  - Delete all records from DisclaimerAcceptances table" -ForegroundColor White
    Write-Host "    (Users will need to re-accept the disclaimer)" -ForegroundColor Gray
    Write-Host ""
    
    $confirm = Read-Host "Continue? (Y/N)"
    if ($confirm -ne "Y" -and $confirm -ne "y") {
        Write-Host "Cancelled." -ForegroundColor Yellow
        exit 0
    }
}

# Execute cleanup
Write-SubHeader "Executing Cleanup"

$query = "DELETE FROM [DisclaimerAcceptances];"

try {
    $result = Invoke-SqliteCommand -DatabasePath $DbPath -Query $query
    
    if ($result) {
        Write-Host ""
        Write-Host "SUCCESS: DisclaimerAcceptances table cleared." -ForegroundColor Green
    }
}
catch {
    Write-Host ""
    Write-Host "ERROR: Failed to execute cleanup." -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

# Summary
Write-Header "CLEANUP COMPLETE"

Write-Host "Database:    $DbPath" -ForegroundColor White
Write-Host "Action:      Cleared DisclaimerAcceptances table" -ForegroundColor Green
Write-Host ""
Write-Host "Users will be prompted to accept the disclaimer on next launch." -ForegroundColor Cyan
Write-Host ""

# Keep window open if running interactively
if (-not $Force) {
    Read-Host "Press Enter to exit"
}
