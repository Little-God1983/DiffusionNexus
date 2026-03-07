Unified Logging \& Process Console Refactor

Summary

Refactor the entire logging system into a single, centralized Unified Console that replaces both the current Logging Console and the Installer Manager's Process Console. All logging, progress tracking, and process management must flow through one unified service accessible from anywhere in the application.



Problem Statement

Logging calls are scattered across multiple files/classes with no consistent pattern

Error logs lack contextual information (no stack traces, no source identification, no timestamps in some cases)

The Installer Manager has its own separate Process Console, duplicating UI and logic

Progress-tracking logging (like the backup process) works well but is not reused elsewhere

Users cannot start/stop instances from outside the Installer Manager view

Requirements

1\. Centralized Logging Service

Create a single IUnifiedLogger service that all logging flows through.



csharp



public enum LogLevel

{

&nbsp;   Trace,

&nbsp;   Debug,

&nbsp;   Info,

&nbsp;   Warning,

&nbsp;   Error,

&nbsp;   Fatal

}



public enum LogCategory

{

&nbsp;   General,

&nbsp;   Backup,

&nbsp;   Download,

&nbsp;   Installation,

&nbsp;   InstanceManagement,

&nbsp;   ModManagement,

&nbsp;   Network,

&nbsp;   FileSystem,

&nbsp;   Configuration

}



public record LogEntry

{

&nbsp;   public DateTime Timestamp { get; init; }

&nbsp;   public LogLevel Level { get; init; }

&nbsp;   public LogCategory Category { get; init; }

&nbsp;   public string Source { get; init; }          // Class/method that produced the log

&nbsp;   public string Message { get; init; }

&nbsp;   public string? Detail { get; init; }         // Extended info, stack traces, etc.

&nbsp;   public string? TaskId { get; init; }          // Links to a tracked task if applicable

&nbsp;   public Exception? Exception { get; init; }

}



public interface IUnifiedLogger

{

&nbsp;   // Basic logging

&nbsp;   void Log(LogLevel level, LogCategory category, string source, string message, string? detail = null, Exception? ex = null);

&nbsp;   

&nbsp;   // Convenience methods

&nbsp;   void Info(LogCategory category, string source, string message);

&nbsp;   void Warn(LogCategory category, string source, string message, string? detail = null);

&nbsp;   void Error(LogCategory category, string source, string message, Exception? ex = null);

&nbsp;   void Fatal(LogCategory category, string source, string message, Exception ex);



&nbsp;   // Observable stream for UI binding

&nbsp;   IObservable<LogEntry> LogStream { get; }

&nbsp;   

&nbsp;   // Query existing logs

&nbsp;   IReadOnlyList<LogEntry> GetEntries(LogCategory? category = null, LogLevel? minLevel = null);

&nbsp;   

&nbsp;   // Clear

&nbsp;   void Clear();

}

Key rules:



Every catch block in the entire application must log through this service with at minimum: the exception, the source class name, and a human-readable message describing what was being attempted.

No more Console.WriteLine, Debug.WriteLine, or ad-hoc logging anywhere.

The service must be registered as a singleton in DI.

2\. Tracked Task System (Progress Tracking)

Model the backup process's progress logging pattern as a reusable system for any long-running task.



csharp



public enum TrackedTaskStatus

{

&nbsp;   Queued,

&nbsp;   Running,

&nbsp;   Paused,

&nbsp;   Completed,

&nbsp;   Failed,

&nbsp;   Cancelled

}



public record TrackedTaskInfo

{

&nbsp;   public string TaskId { get; init; }

&nbsp;   public string Name { get; init; }             // e.g., "Downloading Forge 1.20.1"

&nbsp;   public LogCategory Category { get; init; }

&nbsp;   public TrackedTaskStatus Status { get; set; }

&nbsp;   public double Progress { get; set; }          // 0.0 to 1.0, -1 for indeterminate

&nbsp;   public string StatusText { get; set; }        // e.g., "Extracting files... (3/17)"

&nbsp;   public DateTime StartedAt { get; init; }

&nbsp;   public DateTime? CompletedAt { get; set; }

&nbsp;   public CancellationTokenSource Cts { get; init; }

}



public interface ITaskTracker

{

&nbsp;   /// Start tracking a new task. Returns a handle to report progress.

&nbsp;   ITrackedTaskHandle BeginTask(string name, LogCategory category, CancellationTokenSource? cts = null);

&nbsp;   

&nbsp;   /// All currently active tasks

&nbsp;   IObservable<IReadOnlyList<TrackedTaskInfo>> ActiveTasks { get; }

&nbsp;   

&nbsp;   /// All tasks (including completed/failed) for history

&nbsp;   IReadOnlyList<TrackedTaskInfo> AllTasks { get; }

&nbsp;   

&nbsp;   /// Cancel a task by ID

&nbsp;   void CancelTask(string taskId);

}



public interface ITrackedTaskHandle : IDisposable

{

&nbsp;   string TaskId { get; }

&nbsp;   void ReportProgress(double progress, string? statusText = null);

&nbsp;   void ReportIndeterminate(string? statusText = null);

&nbsp;   void Complete(string? message = null);

&nbsp;   void Fail(Exception ex, string? message = null);

&nbsp;   void Log(LogLevel level, string message);    // Logs linked to this task

&nbsp;   CancellationToken CancellationToken { get; }

}

Usage pattern (follow the backup process style):



csharp



public async Task DownloadModpackAsync(ModpackInfo modpack)

{

&nbsp;   using var task = \_taskTracker.BeginTask(

&nbsp;       $"Downloading {modpack.Name}", 

&nbsp;       LogCategory.Download);

&nbsp;   

&nbsp;   try

&nbsp;   {

&nbsp;       task.ReportProgress(0, "Preparing download...");

&nbsp;       

&nbsp;       var files = await GetFileListAsync(task.CancellationToken);

&nbsp;       

&nbsp;       for (int i = 0; i < files.Count; i++)

&nbsp;       {

&nbsp;           task.CancellationToken.ThrowIfCancellationRequested();

&nbsp;           task.ReportProgress((double)i / files.Count, $"Downloading {files\[i].Name} ({i+1}/{files.Count})");

&nbsp;           await DownloadFileAsync(files\[i], task.CancellationToken);

&nbsp;       }

&nbsp;       

&nbsp;       task.Complete($"Downloaded {files.Count} files");

&nbsp;   }

&nbsp;   catch (OperationCanceledException)

&nbsp;   {

&nbsp;       task.Log(LogLevel.Warning, "Download cancelled by user");

&nbsp;       throw;

&nbsp;   }

&nbsp;   catch (Exception ex)

&nbsp;   {

&nbsp;       task.Fail(ex, $"Failed to download {modpack.Name}");

&nbsp;       throw;

&nbsp;   }

}

Every place that currently does progress logging must be migrated to this pattern:



Backup process (already works well — extract the pattern from here)

Downloads

Installation / Installer Manager processes

Any file operations that take noticeable time

Instance start/stop operations

3\. Unify with Installer Manager Process Console

Remove the Process Console from the Installer Manager entirely. Instead:



The Installer Manager must use IUnifiedLogger and ITaskTracker for all its output

Installation processes become tracked tasks with progress

Process stdout/stderr from launched installers gets piped into the unified logger with LogCategory.Installation

The Installer Manager view should show a filtered view of the Unified Console (filtered to LogCategory.Installation) — not its own separate console

text



Before:  InstallerManager has own console widget + own logging

After:   InstallerManager uses ITaskTracker, UI shows filtered UnifiedConsole

4\. Instance Start/Stop from Anywhere

Create an IInstanceProcessManager service (singleton) that decouples instance lifecycle from any specific view.



csharp



public interface IInstanceProcessManager

{

&nbsp;   /// Launch an instance. Returns the tracked task handle.

&nbsp;   Task<ITrackedTaskHandle> StartInstanceAsync(string instanceId);

&nbsp;   

&nbsp;   /// Stop a running instance.

&nbsp;   Task StopInstanceAsync(string instanceId);

&nbsp;   

&nbsp;   /// Kill a running instance forcefully.

&nbsp;   Task KillInstanceAsync(string instanceId);

&nbsp;   

&nbsp;   /// Observable of currently running instance IDs

&nbsp;   IObservable<IReadOnlySet<string>> RunningInstances { get; }

&nbsp;   

&nbsp;   /// Check if a specific instance is running

&nbsp;   bool IsRunning(string instanceId);

&nbsp;   

&nbsp;   /// Game process stdout/stderr flows into IUnifiedLogger automatically

}

Instance stdout/stderr must be captured and logged as LogCategory.InstanceManagement with the TaskId linked to the instance's tracked task

Any view in the app can call StartInstanceAsync / StopInstanceAsync via injected IInstanceProcessManager

The Unified Console can filter by TaskId to show output for a specific instance

5\. Unified Console UI (View/ViewModel)

Create a single UnifiedConsoleView / UnifiedConsoleViewModel that replaces both the current Logging Console and the Installer Manager's Process Console.



Features:



Feature	Description

Log list	Virtualized scrolling list of LogEntry items with colored level indicators

Level filter	Toggle buttons for Trace/Debug/Info/Warning/Error

Category filter	Dropdown or toggle chips to filter by LogCategory

Search	Text search across message and detail fields

Active tasks panel	Top section showing currently running tracked tasks with progress bars, status text, and cancel buttons

Task history	Expandable section showing completed/failed tasks

Auto-scroll	Auto-scroll to bottom with smart pause when user scrolls up

Task detail drill-down	Click a tracked task to filter the log list to only entries linked to that task

Context actions	Right-click a running instance task → Stop / Kill

Export	Button to export current filtered log to file

Clear	Clear button (with confirmation)

Layout sketch:



text



┌─────────────────────────────────────────────────────┐

│ ● Active Tasks (3)                           \[—]\[×] │

│ ┌─────────────────────────────────────────────────┐ │

│ │ ▶ Downloading Forge 1.20.1    ████████░░ 78%    │ │

│ │   Downloading file 14/18...            \[Cancel] │ │

│ │ ▶ Instance "SMP" running       ●  01:23:45      │ │

│ │                                   \[Stop]\[Kill]  │ │

│ │ ▶ Backup: World Save          ██████████ Done ✓ │ │

│ └─────────────────────────────────────────────────┘ │

│ ─────────────────────────────────────────────────── │

│ Filter: \[All▾] Level: \[I]\[W]\[E] Search: \[\_\_\_\_\_\_\_\_] │

│ ─────────────────────────────────────────────────── │

│ 12:01:03 INF \[Backup]     Starting backup...       │

│ 12:01:04 INF \[Backup]     Compressing world/...    │

│ 12:01:15 WRN \[Network]    Retry 1/3 for file.jar   │

│ 12:01:16 ERR \[Network]    Download failed: Ti...  ▸ │

│ 12:03:22 INF \[Instance]   SMP: \[Server thread/...  │

│                                                     │

│                                          \[Export 📄] │

└─────────────────────────────────────────────────────┘

6\. Error Logging Standards

Establish and enforce these rules across the entire codebase:



csharp



// ❌ BAD - What exists now in many places

catch (Exception ex)

{

&nbsp;   Console.WriteLine(ex.Message);

}



// ❌ BAD - Slightly better but still insufficient  

catch (Exception ex)

{

&nbsp;   Logger.Error(ex.Message);

}



// ✅ GOOD - Full context

catch (Exception ex)

{

&nbsp;   \_logger.Error(

&nbsp;       LogCategory.FileSystem,

&nbsp;       nameof(InstanceManager),              // Source

&nbsp;       $"Failed to delete instance '{instanceName}' at path '{instancePath}'",

&nbsp;       ex                                     // Full exception with stack trace

&nbsp;   );

}

Audit every catch block and every existing log call in the codebase. Every single one must conform to this pattern.



Migration Checklist

&nbsp;Create IUnifiedLogger service and implementation

&nbsp;Create ITaskTracker service and implementation

&nbsp;Create IInstanceProcessManager service and implementation

&nbsp;Register all three as singletons in DI

&nbsp;Build UnifiedConsoleView and UnifiedConsoleViewModel

&nbsp;Refactor backup process to use ITrackedTaskHandle (use as reference implementation since it already works)

&nbsp;Refactor all download operations to use ITrackedTaskHandle

&nbsp;Refactor Installer Manager to use IUnifiedLogger + ITaskTracker

&nbsp;Remove the Process Console from Installer Manager entirely

&nbsp;Replace Installer Manager's console area with a filtered UnifiedConsoleView (category = Installation)

&nbsp;Implement IInstanceProcessManager with stdout/stderr capture

&nbsp;Wire instance start/stop commands to be available globally (toolbar, system tray, context menus, etc.)

&nbsp;Audit every catch block in the entire solution — add proper contextual logging

&nbsp;Audit every existing log/print/debug statement — migrate to IUnifiedLogger

&nbsp;Remove all Console.WriteLine / Debug.WriteLine / ad-hoc logging

&nbsp;Replace the old Logging Console view with UnifiedConsoleView

&nbsp;Test: verify backup logging still works identically (regression baseline)

&nbsp;Test: verify installation process output appears in unified console

&nbsp;Test: verify instance start/stop works from multiple views

&nbsp;Test: verify error entries contain full exception details

&nbsp;Test: verify progress tracking for downloads shows correct percentages

&nbsp;Test: verify cancel buttons actually cancel operations via CancellationToken

Acceptance Criteria

Zero Console.WriteLine or Debug.WriteLine calls remain in the codebase

One logging console exists in the entire application — no separate Process Console

Every error log entry contains: timestamp, source class, category, human-readable message, and full exception (when applicable)

All long-running operations (backups, downloads, installations, instance runs) appear as tracked tasks with progress

Users can cancel any cancellable task from the Unified Console

Users can start/stop instances from the Unified Console, from the instance list, and from any other relevant view

Backup process logging works at least as well as it does today (regression baseline)

The Installer Manager no longer contains any console/logging UI of its own

Filtering by category, level, and search text works in the console UI

Log entries linked to a tracked task can be viewed in isolation by clicking the task



NO Duplicated Code.

