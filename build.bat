@echo off
REM ===========================================
REM .NET Single-File, Self-Contained Build Script
REM ===========================================

REM Name of the project (replace with your .csproj file if needed)
set PROJECT_NAME=DiffusionNexus.UI/DiffusionNexus.UI.csproj

REM Target runtime (win-x64, win-arm64, linux-x64, etc.)
set RUNTIME=win-x64

REM Configuration (Debug/Release)
set CONFIG=Release

REM Output directory
set OUTPUT_DIR=publish

REM Clean previous builds
echo Cleaning previous build...
dotnet clean %PROJECT_NAME% -c %CONFIG%

REM Publish the project as a single file, self-contained build
echo Publishing single-file, self-contained build...
dotnet publish %PROJECT_NAME% -c %CONFIG% -r %RUNTIME% ^
    -p:PublishSingleFile=true ^
    -p:SelfContained=true ^
    -p:IncludeAllContentForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:PublishTrimmed=false ^
    -o %OUTPUT_DIR%

echo.
echo ===========================================
echo Build completed. Files are located in:
echo %OUTPUT_DIR%
echo ===========================================
pause