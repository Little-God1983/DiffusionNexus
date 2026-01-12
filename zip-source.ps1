<#
.SYNOPSIS
    Zips the DiffusionNexus source code excluding publish artifacts and test project.

.DESCRIPTION
    Creates a timestamped zip archive of the source code, excluding:
    - publish folder
    - Any existing publish zip files
    - publish.ps1 script
    - zip-source.ps1 script
    - DiffusionNexus.Tests project
    - bin/obj folders
    - .git folder
    - .github folder
    - .gitignore

.EXAMPLE
    .\zip-source.ps1
#>

$ErrorActionPreference = "Stop"

# Configuration
$sourcePath = $PSScriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$zipName = "DiffusionNexus-source-$timestamp.zip"
$zipPath = Join-Path $sourcePath $zipName
$tempFolder = Join-Path $env:TEMP "DiffusionNexus-zip-$timestamp"

# Exclusion patterns
$excludeFolders = @(
    "publish",
    "DiffusionNexus.Tests",
    "bin",
    "obj",
    ".git",
    ".github",
    ".vs"
)

$excludeFiles = @(
    "publish.ps1",
    "zip-source.ps1",
    "rollout-cleanup.ps1",
    ".gitignore",
    "*.zip"
)

try {
    Write-Host "Creating source archive..." -ForegroundColor Cyan
    Write-Host "  Source: $sourcePath" -ForegroundColor Gray
    Write-Host "  Output: $zipPath" -ForegroundColor Gray

    # Create temp folder
    if (Test-Path $tempFolder) {
        Remove-Item $tempFolder -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempFolder -Force | Out-Null

    # Build exclusion filter for folders
    $folderExclusions = $excludeFolders | ForEach-Object { [regex]::Escape($_) }
    $folderPattern = "\\(" + ($folderExclusions -join "|") + ")(\\|$)"

    # Get all files, excluding specified patterns
    $files = Get-ChildItem -Path $sourcePath -Recurse -File | Where-Object {
        $relativePath = $_.FullName.Substring($sourcePath.Length)
        
        # Check folder exclusions
        $excludeByFolder = $false
        foreach ($folder in $excludeFolders) {
            if ($relativePath -match "\\$folder\\|\\$folder$|^\\$folder\\|^\\$folder$") {
                $excludeByFolder = $true
                break
            }
        }
        
        if ($excludeByFolder) { return $false }

        # Check file exclusions
        foreach ($pattern in $excludeFiles) {
            if ($_.Name -like $pattern) {
                return $false
            }
        }

        return $true
    }

    $totalFiles = $files.Count
    $currentFile = 0

    Write-Host "  Copying $totalFiles files..." -ForegroundColor Gray

    foreach ($file in $files) {
        $currentFile++
        $relativePath = $file.FullName.Substring($sourcePath.Length).TrimStart('\')
        $destPath = Join-Path $tempFolder $relativePath
        $destDir = Split-Path $destPath -Parent

        if (-not (Test-Path $destDir)) {
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }

        Copy-Item $file.FullName -Destination $destPath -Force

        if ($currentFile % 100 -eq 0) {
            $percent = [math]::Round(($currentFile / $totalFiles) * 100)
            Write-Host "  Progress: $percent% ($currentFile / $totalFiles)" -ForegroundColor Gray
        }
    }

    # Create the zip archive
    Write-Host "  Compressing..." -ForegroundColor Gray
    
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }

    Compress-Archive -Path "$tempFolder\*" -DestinationPath $zipPath -CompressionLevel Optimal

    # Get zip file size
    $zipSize = (Get-Item $zipPath).Length
    $zipSizeMB = [math]::Round($zipSize / 1MB, 2)

    Write-Host "`nArchive created successfully!" -ForegroundColor Green
    Write-Host "  File: $zipName" -ForegroundColor White
    Write-Host "  Size: $zipSizeMB MB" -ForegroundColor White
    Write-Host "  Files included: $totalFiles" -ForegroundColor White

} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    exit 1
} finally {
    # Cleanup temp folder
    if (Test-Path $tempFolder) {
        Remove-Item $tempFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
