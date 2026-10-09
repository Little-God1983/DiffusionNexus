using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Settings → ComfyUI Server, read on its own: which server to use and the Custom URL. The URL is
/// null when no settings row exists yet.
/// </summary>
public sealed record ComfyUiServerConnection(ComfyUiServerMode Mode, string? Url);
