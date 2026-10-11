namespace DiffusionNexus.Domain.Models;

/// <summary>The app's catalog channel (null = never chosen) and its "apply automatically" setting (#618).</summary>
public sealed record CatalogUpdateSettings(string? Channel, bool ApplyAutomatically);
