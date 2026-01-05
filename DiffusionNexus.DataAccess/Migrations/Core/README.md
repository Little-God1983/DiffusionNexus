# DiffusionNexus Core Database - Migrations

> **Documentation moved**: See [documentation/V2/Database.md](../../../documentation/V2/Database.md)

This folder contains EF Core migrations for the `DiffusionNexusCoreDbContext`.

## Quick Reference

```bash
# Create migration
dotnet ef migrations add <Name> --context DiffusionNexusCoreDbContext --output-dir Migrations/Core

# Apply migration
dotnet ef database update --context DiffusionNexusCoreDbContext
