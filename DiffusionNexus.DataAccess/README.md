# DiffusionNexus.DataAccess

> **Documentation moved**: See [documentation/V2/DataAccess.md](../documentation/V2/DataAccess.md)

This project contains the core data access abstractions and entity classes used by the application.

## Adding new storage providers

Implement `IRepository<T>` and `IUnitOfWork` interfaces in a new project and wire them up through dependency injection.  Configuration values can be read and written using `IConfigStore` implementations.
