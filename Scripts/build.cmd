@echo off
setlocal
set "PS=pwsh"
where pwsh >nul 2>nul || set "PS=powershell"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
set "RC=%ERRORLEVEL%"
rem A failed build always waits for a key. A good one waits only when this file was started from
rem Explorer - a double click, whose window closes the moment it ends, and the end of a successful
rem build says whether a running DiffusionNexus was left on the old version. Started from PowerShell, Git
rem Bash, an open Command Prompt, a task or a CI step it does not wait: the output stays on screen,
rem or nobody is there to press a key. Another file manager is not Explorer, so a double click there
rem closes the window after a good build.
rem DIFFUSIONNEXUS_BUILD_PAUSE=never switches the wait off altogether, =always forces it. Spaces are
rem taken out first: "set DIFFUSIONNEXUS_BUILD_PAUSE=never && build.cmd" stores "never " with one.
set "MODE="
if defined DIFFUSIONNEXUS_BUILD_PAUSE set "MODE=%DIFFUSIONNEXUS_BUILD_PAUSE: =%"
set "WAIT="
if /i "%MODE%"=="never" goto :finish
if /i "%MODE%"=="always" set "WAIT=1"
if defined MODE if not defined WAIT echo DIFFUSIONNEXUS_BUILD_PAUSE is "%MODE%", which is neither never nor always - ignored.
if not "%RC%"=="0" set "WAIT=1"
if not defined WAIT "%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0_started-by-explorer.ps1" && set "WAIT=1"
rem Its own line, not pause's: pause prints in the Windows display language.
if defined WAIT (
    echo Press any key to continue . . .
    pause >nul
)
:finish
exit /b %RC%
