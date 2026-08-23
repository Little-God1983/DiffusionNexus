using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// The single rule for writing a version's base model, shared by every source that can propose
/// one — sidecar (<see cref="SidecarMetadataApplier"/>), safetensors header and filename heuristic
/// (<see cref="Steps.IdentifyModelStep"/>'s miss-branch) — so all of them agree on what counts as
/// an answer and who is allowed to overwrite what.
/// </summary>
public static class BaseModelWriter
{
    /// <summary>
    /// Writes both spellings of the base model — the raw Civitai string and the
    /// <see cref="BaseModelType"/> the viewer's filter reads — using the same parser the detail
    /// view's editor uses. Writing only the raw one left the enum reporting the previous base
    /// model forever.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    /// <remarks>
    /// A blank <paramref name="baseModelRaw"/> is a missing answer, not an instruction to forget
    /// the stored one — the call sites only reject an <i>absent</i> value, so a source carrying an
    /// empty string must not blank the raw string and set the enum to <c>Unknown</c> on a version
    /// nobody had edited.
    /// </remarks>
    public static bool Write(ModelVersion dbVersion, string? baseModelRaw)
    {
        if (string.IsNullOrWhiteSpace(baseModelRaw)) return false;

        dbVersion.BaseModelRaw = baseModelRaw;
        dbVersion.BaseModel = BaseModelTypeExtensions.ParseCivitai(baseModelRaw);
        return true;
    }

    /// <summary>
    /// The header/heuristic gate: only fill a placeholder, never a user's edit. Unlike the sidecar
    /// formats (which have their own authored-text guard, <c>CanWriteVersionText</c>, because a
    /// sidecar can also rename the version or replace its trigger words), the header/heuristic
    /// rungs write nothing but the base model, so the narrower placeholder check is the correct
    /// gate for them: a version whose base model already says something real is left alone even if
    /// other fields on it are still blank.
    /// </summary>
    public static bool CanFill(ModelVersion dbVersion) =>
        !dbVersion.IsUserEdited && dbVersion.BaseModelRaw is null or "???";
}
