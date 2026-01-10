# DiffusionNexus DataAccess

Core data access abstractions and entity classes used by the application.

**Project**: `DiffusionNexus.DataAccess`

## Overview

This project contains:
- EF Core DbContext for the core database
- Repository and Unit of Work patterns
- Configuration storage interfaces

## DbContexts

### DiffusionNexusCoreDbContext

The main DbContext for V2 domain entities.

```csharp
// Registration
services.AddDiffusionNexusCoreDatabase();

// Or with custom directory
services.AddDiffusionNexusCoreDatabase(@"D:\MyData");

// Usage
public class MyService(DiffusionNexusCoreDbContext db)
{
    public async Task<Model?> GetModelAsync(int id)
    {
        return await db.Models
            .Include(m => m.Versions)
            .FirstOrDefaultAsync(m => m.Id == id);
    }
}
```

### DiffusionNexusDbContext (Legacy)

The original DbContext for V1 entities. Maintained for backward compatibility.

## Adding New Storage Providers

Implement `IRepository<T>` and `IUnitOfWork` interfaces in a new project and wire them up through dependency injection.

### IRepository\<T\>

```csharp
public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id);
    Task<IEnumerable<T>> GetAllAsync();
    Task AddAsync(T entity);
    void Update(T entity);
    void Delete(T entity);
}
```

### IUnitOfWork

```csharp
public interface IUnitOfWork : IDisposable
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
```

## Configuration Storage

Configuration values can be read and written using `IConfigStore` implementations:

```csharp
public interface IConfigStore
{
    Task<T?> GetAsync<T>(string key);
    Task SetAsync<T>(string key, T value);
    Task RemoveAsync(string key);
}
```

## Database Files

| Database | Filename | Purpose |
|----------|----------|---------|
| Core (V2) | `Diffusion_Nexus-core.db` | Domain entities, model metadata |
| Legacy (V1) | `DiffusionNexus.db` | Original entities |

Default location: `%LOCALAPPDATA%/DiffusionNexus/Data/`

## Migrations

See [Database.md](Database.md) for detailed migration instructions.

```bash
# Create migration
cd DiffusionNexus.DataAccess
dotnet ef migrations add <Name> --context DiffusionNexusCoreDbContext --output-dir Migrations/Core

# Apply migration
dotnet ef database update --context DiffusionNexusCoreDbContext
```
