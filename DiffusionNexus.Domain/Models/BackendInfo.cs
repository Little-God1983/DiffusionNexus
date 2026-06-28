namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Lightweight, bindable description of a selectable execution backend
/// (e.g. <c>"ComfyUI"</c> or <c>"Diffusion Nexus Core"</c>). Surfaced by
/// <see cref="Services.IFeatureReadinessService.GetAvailableBackends"/> so the reusable
/// readiness UI can offer a backend picker without depending on the full
/// <see cref="Services.IFeatureBackend"/> contract.
/// </summary>
/// <param name="Kind">The engine this option represents.</param>
/// <param name="DisplayName">Human-readable name shown in the picker.</param>
public sealed record BackendInfo(Enums.BackendKind Kind, string DisplayName);
