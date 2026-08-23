using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// The single rule for writing a version's base model from an automatic source, shared by every
/// one that can propose an answer — sidecar (<see cref="SidecarMetadataApplier"/>), Civitai
/// (<see cref="CivitaiMetadataApplier"/>), and the safetensors header / filename heuristic
/// (<see cref="Steps.IdentifyModelStep"/>'s miss-branch) — so all of them agree on what counts as
/// an answer and who is allowed to overwrite what.
/// <para>
/// The detail view's inline editor (<c>ModelDetailViewModel.Editing.SaveSelectedBaseModelAsync</c>)
/// is a deliberate exception, not an oversight. It is the one place a user is allowed to blank a
/// version's base model outright, which <see cref="Write"/> refuses by design — a blank is "no new
/// answer", never "forget the old one" (see <see cref="Write"/>'s remarks). Routing a clearing UI
/// through a method that cannot clear would mean either losing that ability or growing a second
/// flag no automatic caller needs, so the editor keeps its own two-line write of the same two
/// spellings instead of calling in here.
/// </para>
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
    /// other fields on it are still blank. "Placeholder" is <see cref="SyncStateDeriver.IsPlaceholder"/> —
    /// the one place that rule lives, so this and the retry-selection logic can never drift apart.
    /// </summary>
    public static bool CanFill(ModelVersion dbVersion) =>
        !dbVersion.IsUserEdited && SyncStateDeriver.IsPlaceholder(dbVersion.BaseModelRaw);
}
