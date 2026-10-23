# Exit code 0 when the .cmd wrapper that called this was started by Explorer - a double click, whose
# window closes the moment the wrapper ends. Anything else gives 1: a .cmd started from PowerShell,
# Git Bash, a VS Code task, a scheduled task or a CI step also runs as cmd /c "<file>", but there the
# window stays open or nobody is at the keyboard, and a pause after a good build would wait for ever.
#
# This process's parent is the cmd.exe running the wrapper, and that one's parent is what started it.
# The wrapper has to be a cmd /c as well: a Command Prompt opened from the Start menu is a child of
# Explorer too, but it runs a .cmd typed into it inside itself, and its window stays open.
#
# -StarterName is for the tests, which cannot have Explorer as their parent: they name themselves.
param([string]$StarterName = 'explorer.exe')

$ErrorActionPreference = 'SilentlyContinue'
$self    = Get-CimInstance Win32_Process -Filter "ProcessId = $PID"
$wrapper = if ($self)    { Get-CimInstance Win32_Process -Filter "ProcessId = $($self.ParentProcessId)" }
$parent  = if ($wrapper) { Get-CimInstance Win32_Process -Filter "ProcessId = $($wrapper.ParentProcessId)" }
if ($parent -and $parent.Name -ieq $StarterName -and $wrapper.CommandLine -match '(^|\s)/c(\s|$)') { exit 0 }
exit 1
