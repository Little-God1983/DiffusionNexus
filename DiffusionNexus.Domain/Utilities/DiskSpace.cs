using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace DiffusionNexus.Domain.Utilities;

/// <summary>How much a free-space reading is worth.</summary>
public enum FreeSpaceKind
{
    /// <summary>A real number, read off the volume the path resolves to.</summary>
    Known,

    /// <summary>
    /// The volume exists as far as anyone can tell, but it will not say how much room it has —
    /// or answering would have meant a network round trip the caller could not afford.
    /// Callers fail <b>open</b> and say so: refusing to act on a destination that is probably
    /// fine just moves the failure somewhere less informative.
    /// </summary>
    Unknown,

    /// <summary>
    /// There is no volume behind this path: a dead drive letter, an unplugged disk, a
    /// not-ready removable drive. Callers <b>block</b>.
    /// </summary>
    Unreachable,
}

/// <summary>How far a reading may go to get its answer.</summary>
public enum DiskProbe
{
    /// <summary>
    /// Read whatever volume the path lands on, network shares included. Measured: an unreachable
    /// UNC path costs the full SMB timeout (~5 s), so this belongs off the UI thread.
    /// </summary>
    Full,

    /// <summary>
    /// Never touch the network. Local volumes still get the complete mount-point-aware reading —
    /// measured at well under a millisecond, junctions included — and a network destination
    /// reports <see cref="FreeSpaceKind.Unknown"/> instead of stalling. Deciding which is which
    /// costs no I/O at all (a UNC prefix, or <see cref="DriveInfo.DriveType"/>, which reads the
    /// mount table). Safe on the UI thread.
    /// </summary>
    LocalVolumesOnly,
}

/// <summary>
/// A free-space reading. <see cref="FreeBytes"/> is 0 for anything but
/// <see cref="FreeSpaceKind.Known"/> — enforced here rather than merely documented, so it can
/// never be compared against a required size without checking <see cref="Kind"/> first.
/// </summary>
public readonly record struct FreeSpaceResult(FreeSpaceKind Kind, long FreeBytes)
{
    /// <summary>Zero unless this is a real measurement.</summary>
    public long FreeBytes { get; init; } = Kind == FreeSpaceKind.Known ? FreeBytes : 0;

    /// <summary>True when <see cref="FreeBytes"/> is a real measurement.</summary>
    public bool IsKnown => Kind == FreeSpaceKind.Known;

    /// <summary>A real reading of <paramref name="freeBytes"/> free.</summary>
    public static FreeSpaceResult Known(long freeBytes) => new(FreeSpaceKind.Known, freeBytes);

    /// <summary>A volume that will not say how much room it has.</summary>
    public static FreeSpaceResult Unknown { get; } = new(FreeSpaceKind.Unknown, 0);

    /// <summary>A path with no volume behind it.</summary>
    public static FreeSpaceResult Unreachable { get; } = new(FreeSpaceKind.Unreachable, 0);
}

/// <summary>
/// The one answer to "how much room is there where this file is going?".
/// </summary>
/// <remarks>
/// <para>
/// It lives in Domain, next to <see cref="LocalPathRoots"/> and for the same reason: every layer
/// asks it. Five copies of this question had grown across three projects — the download queue,
/// the LoRA sorter, the captioning model manager (twice, in one class) and the ONNX model
/// manager — each doing <c>Path.GetPathRoot</c> → <c>new DriveInfo(root)</c> →
/// <c>IsReady ? AvailableFreeSpace : &lt;sentinel&gt;</c> inside a <c>try</c>, and each picking a
/// different sentinel: throw, -1, 0, or exception-type archaeology at the call site. Issue #581.
/// </para>
/// <para>
/// The distinction that earned the tri-state: an <i>unknowable</i> reading (a share that will not
/// report its size, but whose folder is right there) and an <i>unreachable</i> one (a drive letter
/// with nothing behind it) want opposite answers. Conflating them cost a real run —
/// <c>LoraSorterViewModel</c> failed open on an unplugged drive, armed Start, and the executor
/// then reported "Done: 0 sorted, 0 duplicates skipped, 412 failed". Making it a value rather
/// than an exception type is what stops the next caller re-deriving it by hand.
/// </para>
/// <para>
/// On Windows the reading comes from <c>GetVolumePathName</c> + <c>GetDiskFreeSpaceEx</c>, not
/// from <see cref="DriveInfo"/>, because a destination can sit on a volume mounted into a folder:
/// with an 8 TB disk mounted at <c>C:\Models</c>, <see cref="DriveInfo"/> reports <c>C:</c>'s free
/// space and both directions are wrong — a full <c>C:</c> blocks downloads that would land on an
/// empty disk, and a nearly-full mounted volume reports plenty of room. The Win32 pair resolves
/// the reparse point; <see cref="Path.GetPathRoot(string)"/> does not.
/// </para>
/// </remarks>
public static class DiskSpace
{
    /// <summary>
    /// Free bytes on the volume that <paramref name="path"/> resolves to. The path does not have
    /// to exist — a destination file or folder about to be created is measured against the volume
    /// it will land on, which is what every download preflight actually needs.
    /// </summary>
    /// <param name="path">
    /// A file or directory path, absolute or relative. Relative paths are resolved first; the
    /// probes this replaced fed <see cref="Path.GetPathRoot(string)"/> the raw string, which
    /// returns <c>""</c> for a relative path and left every caller to invent a meaning for it.
    /// </param>
    /// <param name="probe">See <see cref="DiskProbe"/>. The default may block on a network path.</param>
    public static FreeSpaceResult TryGetAvailableSpace(string? path, DiskProbe probe = DiskProbe.Full)
    {
        if (!TryResolve(path, out var full)) return FreeSpaceResult.Unreachable;

        if (probe == DiskProbe.LocalVolumesOnly && VerdictWithoutTouchingTheNetwork(full) is { } cheap)
        {
            return cheap;
        }

        return Measure(VolumeRootOf(full));
    }

    /// <summary>
    /// Reads a volume root that <see cref="GetVolumeRoot"/> already resolved, without resolving it
    /// a second time. The queue groups destinations by volume and then reads each volume once;
    /// going back through <see cref="TryGetAvailableSpace"/> would repeat the resolution, which on
    /// an unreachable network destination means paying the SMB timeout twice.
    /// </summary>
    public static FreeSpaceResult TryGetVolumeSpace(string? volumeRoot, DiskProbe probe = DiskProbe.Full)
    {
        if (!TryResolve(volumeRoot, out var full)) return FreeSpaceResult.Unreachable;

        if (probe == DiskProbe.LocalVolumesOnly && VerdictWithoutTouchingTheNetwork(full) is { } cheap)
        {
            return cheap;
        }

        return Measure(full);
    }

    /// <summary>
    /// The root of the volume <paramref name="path"/> lives on — the drive root for an ordinary
    /// path, the mount point for a volume mounted into a folder. This is the correct key for
    /// grouping several destinations by "same disk", and the correct thing to name in a message
    /// about one: grouping by drive letter puts two genuinely different volumes in one bucket.
    /// </summary>
    /// <returns>The volume root with a trailing separator, or <see langword="null"/> when the
    /// path is empty or malformed.</returns>
    /// <param name="probe">See <see cref="DiskProbe"/>. Under
    /// <see cref="DiskProbe.LocalVolumesOnly"/> a network path falls back to its share root
    /// rather than being resolved, so the caller still has something to name.</param>
    public static string? GetVolumeRoot(string? path, DiskProbe probe = DiskProbe.Full)
    {
        if (!TryResolve(path, out var full)) return null;

        if (probe == DiskProbe.LocalVolumesOnly && VerdictWithoutTouchingTheNetwork(full) is not null)
        {
            return WithTrailingSeparator(Path.GetPathRoot(full));
        }

        return VolumeRootOf(full);
    }

    /// <summary>
    /// The deepest mount root that <paramref name="fullPath"/> lies under, or <see langword="null"/>
    /// when none does. Deepest wins: <c>/mnt/models</c> and <c>/</c> both contain
    /// <c>/mnt/models/loras</c>, and answering <c>/</c> is the same bug this class exists to fix.
    /// </summary>
    internal static string? LongestMountRootContaining(string fullPath, IEnumerable<string> mountRoots)
    {
        string? best = null;
        var bestLength = -1;

        foreach (var root in mountRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;

            var trimmed = root.TrimEnd('/', '\\');

            // The filesystem root trims to nothing and contains everything. LocalPathRoots.IsUnder
            // answers false there on purpose — "everything" is the wrong reading of an empty
            // source folder, which is the question it was written for — so that one case is
            // settled here rather than by loosening a rule other callers depend on.
            var contains = trimmed.Length == 0
                ? fullPath.Length > 0 && LocalPathRoots.IsSeparator(fullPath[0])
                : LocalPathRoots.IsUnder(fullPath, trimmed);

            if (!contains || trimmed.Length <= bestLength) continue;

            best = root;
            bestLength = trimmed.Length;
        }

        return best;
    }

    private static bool TryResolve(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path at all. Nothing is going to land here.
            return false;
        }
    }

    /// <summary>
    /// Settles the paths that must not be read from the UI thread, using no I/O: a UNC path is a
    /// network path by spelling, a mapped network drive says so through
    /// <see cref="DriveInfo.DriveType"/> (a mount-table lookup, measured at ~1 µs), and an
    /// unmapped letter reports <see cref="DriveType.NoRootDirectory"/>, which is a definitive
    /// "not there" rather than a "did not look".
    /// </summary>
    /// <returns>The verdict, or <see langword="null"/> when the volume is local and the full
    /// reading is safe to take.</returns>
    private static FreeSpaceResult? VerdictWithoutTouchingTheNetwork(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return FreeSpaceResult.Unknown;

        if (root.Length > 1 && LocalPathRoots.IsSeparator(root[0]) && LocalPathRoots.IsSeparator(root[1]))
        {
            return FreeSpaceResult.Unknown;
        }

        try
        {
            return new DriveInfo(root).DriveType switch
            {
                DriveType.Network => FreeSpaceResult.Unknown,
                DriveType.NoRootDirectory => FreeSpaceResult.Unreachable,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return FreeSpaceResult.Unknown;
        }
    }

    private static string? VolumeRootOf(string fullPath) => OperatingSystem.IsWindows()
        ? WindowsVolumeRoot(fullPath)
        : UnixMountRoot(fullPath);

    private static FreeSpaceResult Measure(string? volumeRoot)
    {
        if (string.IsNullOrEmpty(volumeRoot)) return FreeSpaceResult.Unknown;

        return OperatingSystem.IsWindows()
            ? MeasureWindowsVolume(volumeRoot)
            : MeasureUnixMount(volumeRoot);
    }

    /// <summary>
    /// Windows: <c>GetVolumePathName</c> walks up to the nearest component that exists, which is
    /// both what tolerates a destination that has not been created yet and what makes it land
    /// inside a mounted folder rather than on its host letter. Falls back to the drive root, whose
    /// own failure below is the verdict — a dead drive letter fails here and asking its root is
    /// still the right next move.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string WindowsVolumeRoot(string fullPath) =>
        WithTrailingSeparator(TryGetVolumePathName(fullPath, out var resolved)
            ? resolved
            : Path.GetPathRoot(fullPath)) ?? string.Empty;

    [SupportedOSPlatform("windows")]
    private static FreeSpaceResult MeasureWindowsVolume(string volumeRoot)
    {
        if (GetDiskFreeSpaceExW(volumeRoot, out var freeForCaller, out _, out _))
        {
            // freeForCaller honours a per-user quota, which is the number that governs whether
            // this process's write succeeds — not the volume-wide total.
            return FreeSpaceResult.Known((long)Math.Min(freeForCaller, long.MaxValue));
        }

        return new FreeSpaceResult(ClassifyProbeFailure(Marshal.GetLastWin32Error()), 0);
    }

    // TODO: Linux Implementation for issue #581 — mount-point resolution off Windows.
    // DriveInfo.GetDrives() enumerates the mounted filesystems, so the volume a path belongs to is
    // the deepest mount root containing it (LongestMountRootContaining). This is the same job
    // GetVolumePathName does on Windows and it needs no P/Invoke. Two known gaps, left open for
    // extension rather than guessed at here:
    //   * LocalPathRoots.IsUnder folds case, which is Windows semantics; on a case-sensitive
    //     filesystem /Data and /data are different mounts. A case-sensitive comparison belongs
    //     behind an OperatingSystem check.
    //   * bind mounts and overlayfs can put one path under several roots that are not nested;
    //     statvfs(2) answers that exactly, where this picks the longest textual match.
    private static string? UnixMountRoot(string fullPath)
    {
        try
        {
            return LongestMountRootContaining(fullPath, ReadableMountRoots());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static FreeSpaceResult MeasureUnixMount(string mountRoot)
    {
        try
        {
            var drive = new DriveInfo(mountRoot);
            if (!drive.IsReady) return FreeSpaceResult.Unreachable;
            return FreeSpaceResult.Known(drive.AvailableFreeSpace);
        }
        catch (ArgumentException)
        {
            return FreeSpaceResult.Unknown;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The mount exists but will not answer. That is "could not measure", not "not there" —
            // the same conclusion ERROR_ACCESS_DENIED reaches on Windows.
            return FreeSpaceResult.Unknown;
        }
        catch (IOException)
        {
            // DriveNotFoundException lands here: the mount is gone.
            return FreeSpaceResult.Unreachable;
        }
    }

    /// <summary>Mount roots we can name without reading any of them.</summary>
    private static IEnumerable<string> ReadableMountRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                // A dead network mount must not be read here — only named.
                if (drive.DriveType == DriveType.Network) continue;
                root = drive.RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return root;
        }
    }

    /// <summary>
    /// What a failed <c>GetDiskFreeSpaceEx</c> means. Errors that name a missing or dead volume
    /// block; anything else (denied, unsupported filesystem, a transport hiccup) is a volume we
    /// simply could not measure.
    /// </summary>
    private static FreeSpaceKind ClassifyProbeFailure(int win32Error) => win32Error switch
    {
        ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidDrive or ErrorNotReady
            or ErrorBadNetpath or ErrorDevNotExist or ErrorNetnameDeleted or ErrorBadNetName
            or ErrorInvalidName or ErrorNoNetOrBadPath or ErrorNetworkUnreachable
            or ErrorHostUnreachable => FreeSpaceKind.Unreachable,
        _ => FreeSpaceKind.Unknown,
    };

    /// <summary>
    /// <c>Path.GetPathRoot(@"\\nas\share\loras")</c> is <c>\\nas\share</c> — no separator — while
    /// <c>GetDiskFreeSpaceEx</c> documents a trailing backslash as required for a UNC name, and
    /// this string is shown to the user as the destination to go and free room on.
    /// </summary>
    private static string? WithTrailingSeparator(string? root)
    {
        if (string.IsNullOrEmpty(root)) return root;
        return LocalPathRoots.IsSeparator(root[^1]) ? root : root + Path.DirectorySeparatorChar;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetVolumePathName(string fullPath, out string volume)
    {
        // MAX_PATH is the documented minimum; a mount point under a long path needs more, and the
        // call says so rather than truncating.
        foreach (var capacity in new[] { 260, 32768 })
        {
            var buffer = new StringBuilder(capacity);
            if (GetVolumePathNameW(fullPath, buffer, buffer.Capacity))
            {
                volume = buffer.ToString();
                return volume.Length > 0;
            }

            if (Marshal.GetLastWin32Error() != ErrorFilenameExcedRange) break;
        }

        volume = string.Empty;
        return false;
    }

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorInvalidDrive = 15;
    private const int ErrorNotReady = 21;
    private const int ErrorBadNetpath = 53;
    private const int ErrorDevNotExist = 55;
    private const int ErrorNetnameDeleted = 64;
    private const int ErrorBadNetName = 67;
    private const int ErrorInvalidName = 123;
    private const int ErrorFilenameExcedRange = 206;
    private const int ErrorNoNetOrBadPath = 1203;
    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);
}
