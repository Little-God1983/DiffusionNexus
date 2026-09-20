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
    /// The volume exists as far as anyone can tell, but it will not say how much room it has.
    /// Callers fail <b>open</b> and say so — refusing to act on a destination that is probably
    /// fine just moves the failure somewhere less informative.
    /// </summary>
    Unknown,

    /// <summary>
    /// There is no volume behind this path: a dead drive letter, an unplugged disk, a
    /// not-ready removable drive. Callers <b>block</b>.
    /// </summary>
    Unreachable,
}

/// <summary>
/// A free-space reading. <see cref="FreeBytes"/> is 0 for anything but
/// <see cref="FreeSpaceKind.Known"/>, so it must never be compared against a required size
/// without checking <see cref="Kind"/> first.
/// </summary>
public readonly record struct FreeSpaceResult(FreeSpaceKind Kind, long FreeBytes)
{
    /// <summary>True when <see cref="FreeBytes"/> is a real measurement.</summary>
    public bool IsKnown => Kind == FreeSpaceKind.Known;

    internal static FreeSpaceResult Known(long freeBytes) => new(FreeSpaceKind.Known, freeBytes);

    internal static FreeSpaceResult Unknown { get; } = new(FreeSpaceKind.Unknown, 0);

    internal static FreeSpaceResult Unreachable { get; } = new(FreeSpaceKind.Unreachable, 0);
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
/// the reparse point; <see cref="Path.GetPathRoot(string)"/> does not. Elsewhere the
/// <see cref="DriveInfo"/> fallback applies and mount points are not resolved.
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
    /// <param name="resolveMountPoints">
    /// <see langword="false"/> answers from the drive letter alone: no reparse-point resolution,
    /// so a volume mounted into a folder reports its <i>host</i> letter's free space. It exists
    /// for the one caller that runs this synchronously on the UI thread, where the volume lookup
    /// would block for the SMB timeout on a dead network path (measured: ~5 s). Prefer the
    /// default everywhere else.
    /// </param>
    public static FreeSpaceResult TryGetAvailableSpace(string? path, bool resolveMountPoints = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return FreeSpaceResult.Unreachable;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a path at all. Nothing is going to land here.
            return FreeSpaceResult.Unreachable;
        }

        if (OperatingSystem.IsWindows() && resolveMountPoints)
        {
            return MeasureWindowsVolume(full);
        }

        return MeasureDriveRoot(full);
    }

    /// <summary>
    /// The root of the volume <paramref name="path"/> lives on — the drive root for an ordinary
    /// path, the mount point for a volume mounted into a folder. This is the correct key for
    /// grouping several destinations by "same disk", and the correct thing to name in a message
    /// about one: grouping by drive letter puts two genuinely different volumes in one bucket.
    /// </summary>
    /// <returns>The volume root with a trailing separator, or <see langword="null"/> when the
    /// path is empty or malformed.</returns>
    /// <param name="resolveMountPoints">See <see cref="TryGetAvailableSpace"/>. When
    /// <see langword="false"/>, or off Windows, this is the drive root.</param>
    public static string? GetVolumeRoot(string? path, bool resolveMountPoints = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (OperatingSystem.IsWindows() && resolveMountPoints && TryGetVolumePathName(full, out var volume))
        {
            return volume;
        }

        var root = Path.GetPathRoot(full);
        return string.IsNullOrEmpty(root) ? null : root;
    }

    /// <summary>
    /// Windows reading: resolve the volume the path belongs to, then ask that volume. Both steps
    /// tolerate a path that does not exist yet — <c>GetVolumePathName</c> walks up to the nearest
    /// component that does, which is also what makes it land inside a mounted folder rather than
    /// on its host letter.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FreeSpaceResult MeasureWindowsVolume(string fullPath)
    {
        // A failed volume lookup is not a verdict on its own: a dead drive letter fails here and
        // the drive root is still the right thing to ask, so the error below is the one that
        // decides.
        var volume = TryGetVolumePathName(fullPath, out var resolved)
            ? resolved
            : Path.GetPathRoot(fullPath);

        if (string.IsNullOrEmpty(volume)) return FreeSpaceResult.Unreachable;

        if (GetDiskFreeSpaceExW(volume, out var freeForCaller, out _, out _))
        {
            // freeForCaller honours a per-user quota, which is the number that governs whether
            // this process's write succeeds — not the volume-wide total.
            return FreeSpaceResult.Known((long)Math.Min(freeForCaller, long.MaxValue));
        }

        return new FreeSpaceResult(ClassifyProbeFailure(Marshal.GetLastWin32Error()), 0);
    }

    /// <summary>
    /// Cross-platform reading, and the cheap Windows mode: one <see cref="DriveInfo"/> on the
    /// path's drive root. No reparse-point resolution, so a mounted folder reports its host.
    /// </summary>
    private static FreeSpaceResult MeasureDriveRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return FreeSpaceResult.Unknown;

        try
        {
            var drive = new DriveInfo(root);

            // Not ready is a volume that is genuinely not there: an empty optical drive, a
            // disconnected removable disk. Blocking is right.
            if (!drive.IsReady) return FreeSpaceResult.Unreachable;

            return FreeSpaceResult.Known(drive.AvailableFreeSpace);
        }
        catch (ArgumentException)
        {
            // No DriveInfo exists for this root at all — new DriveInfo(@"\\nas\share\") throws on
            // the name alone, without touching the network. Blind, not broken.
            return FreeSpaceResult.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // DriveNotFoundException (an IOException) is the unplugged drive.
            return FreeSpaceResult.Unreachable;
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
