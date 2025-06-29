# DiffusionNexus.DataAccess.Infrastructure

This project provides file based persistence implementations for the core data access abstractions.  Two serializer adapters are included:

- `JsonSerializerAdapter` using `System.Text.Json`
- `XmlSerializerAdapter` using `System.Xml.Serialization`

`FileConfigStore` persists configuration objects to disk using whichever serializer is registered through dependency injection.
