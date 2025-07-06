# ADR: Split FileControllerService into focused components

## Status
Accepted

## Context
`FileControllerService` previously handled progress reporting, hashing, disk checks and cleanup in addition to orchestrating model moves. This violated the Single Responsibility Principle and made unit testing difficult.

## Decision
We extracted new services under `Services/IO`:

- `DiskUtility` for disk space checks, path validation and directory cleanup.
- `HashingService` for computing and comparing hashes.
- `IProgressReporter` with `ConsoleProgressReporter` as the default implementation.

`FileControllerService` now orchestrates these services and contains only two private fields. Dependency injection registrations are provided via `IoServiceCollectionExtensions`.

## Consequences
The service layer is easier to unit test and extend. Existing consumers continue to use `FileControllerService` without modifications.
