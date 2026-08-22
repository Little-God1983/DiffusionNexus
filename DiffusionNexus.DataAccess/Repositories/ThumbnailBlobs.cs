namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// Query-side constants for the <c>ModelImage.ThumbnailData</c> BLOB column.
/// </summary>
internal static class ThumbnailBlobs
{
    /// <summary>
    /// The empty BLOB, compared against rather than measured. <c>ThumbnailData.Length</c> has no
    /// SQLite translation: EF answers the <c>!= null</c> half in SQL, then <b>projects the column
    /// itself</b> so it can finish the comparison in memory — which is the one thing a query over
    /// every image row must never do. <c>&lt;&gt; X''</c> is the same question answered entirely
    /// inside the engine.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="SyncStateRepository"/> (candidate selection) and
    /// <see cref="ModelRepository"/> (the light tile load), because both ask exactly this question
    /// of exactly this column, and both are library-wide.
    /// </remarks>
    internal static readonly byte[] Empty = [];
}
