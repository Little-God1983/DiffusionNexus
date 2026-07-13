using System;
using System.Collections.Generic;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>
/// The minimal contract the Workflows gallery needs to host a run screen in its <c>ActiveRun</c>
/// slot. Satisfied by the generation base <see cref="PipelineRunViewModel"/> (which already has
/// every member) and by the bespoke <see cref="BatchMetadataDistillerViewModel"/>.
/// </summary>
public interface IPipelineRun : IDisposable
{
    /// <summary>Screen title shown in the run UI.</summary>
    string Title { get; }

    /// <summary>Raised when the user clicks Back; the gallery clears its active run.</summary>
    event EventHandler? CloseRequested;

    /// <summary>Set by the gallery to share its single GPU/RAM monitor. May be ignored.</summary>
    ResourceMonitorViewModel? ResourceMonitor { get; set; }

    /// <summary>Pre-loads a set of input images (the "Send to → Workflows" flow).</summary>
    void LoadInputImages(IReadOnlyList<string> paths);
}
