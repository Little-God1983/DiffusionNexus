using System.Collections.Generic;
using System.Linq;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Decides which registered Base Model Folder rows are not model roots at all.
///
/// The pre-fix registrar fed <c>ComfyUiPathDiscovery.EnumerateModelSearchPaths</c> — every
/// directory a model file might live in — into the root registry, so a single
/// <c>extra_model_paths.yaml</c> turned into twenty rows: the real root <c>D:\Models</c>
/// plus each of its category folders (<c>D:\Models\Lora</c>, <c>D:\Models\VAE</c>, …).
/// This finds those leftovers so they can be dropped once.
///
/// Pure and conservative by construction — a row is only redundant when ALL of these hold:
/// <list type="bullet">
/// <item>it was registered by the app (<c>InstallerPackageId</c> is set), so a folder the
///       user added by hand is never touched, whatever its shape;</item>
/// <item>it is not the ⭐ default download target;</item>
/// <item>it is not itself a current root of ANY installation;</item>
/// <item>it sits strictly inside a current root, which is what makes it a category folder
///       rather than an independent library.</item>
/// </list>
/// The last condition is the important one: a registered row somewhere else entirely may be
/// a stale root whose folder has moved, and guessing about it would delete real settings.
///
/// Rows whose <c>InstallerPackageId</c> names an installation that no longer exists are
/// still pruned, measured against the union of every live installation's roots. Removing and
/// re-adding an installation gives it a new id while its old rows keep the dead one, so
/// requiring the ids to match would have made this cleanup a no-op in exactly the case that
/// produced the mess.
/// </summary>
public static class RedundantBaseModelFolders
{
    /// <summary>
    /// The rows that should be removed, in input order.
    /// </summary>
    /// <param name="rows">Every Base Model Folder row currently registered.</param>
    /// <param name="rootsByPackageId">
    /// The current, correct roots per installation id (from
    /// <see cref="BaseModelFolderRegistrar.ResolveModelRoots"/>). An installation that
    /// resolves to no roots — an unplugged drive, a folder temporarily missing — protects its
    /// own rows: absence of roots is not evidence its registrations are junk.
    /// </param>
    public static IReadOnlyList<BaseModelFolder> Resolve(
        IEnumerable<BaseModelFolder> rows,
        IReadOnlyDictionary<int, IReadOnlyList<string>> rootsByPackageId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rootsByPackageId);

        var allRoots = rootsByPackageId.Values.SelectMany(roots => roots).ToList();
        var redundant = new List<BaseModelFolder>();

        foreach (var row in rows)
        {
            if (row.InstallerPackageId is not { } packageId || row.IsDefault)
            {
                continue;
            }

            // A live installation is judged against its own roots. A row left behind by an
            // installation that no longer exists is judged against every live root instead —
            // otherwise re-adding an installation (new id, old rows) would freeze its junk
            // rows in place forever.
            var knowsPackage = rootsByPackageId.TryGetValue(packageId, out var ownRoots);
            var candidateRoots = knowsPackage ? ownRoots! : allRoots;

            if (knowsPackage && ownRoots!.Count == 0)
            {
                continue;
            }

            // Checked against every root, not just the candidates: a folder that is a
            // legitimate root of some other installation must survive regardless of which
            // package's id happens to be on the row.
            var isItselfARoot = allRoots.Any(root => FolderPathMatch.AreSame(root, row.FolderPath));
            if (isItselfARoot)
            {
                continue;
            }

            if (candidateRoots.Any(root => FolderPathMatch.Contains(root, row.FolderPath)))
            {
                redundant.Add(row);
            }
        }

        return redundant;
    }
}
