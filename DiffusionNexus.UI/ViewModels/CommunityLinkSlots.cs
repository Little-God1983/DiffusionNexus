namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// How the sidebar fits a community-link list of any length into a fixed height. The list is
/// operator-edited (up to <c>CommunityLink.MaxLinks</c> rows), so the sidebar reserves a fixed
/// number of slots: when the list fits, every link gets one; when it does not, the last slot
/// becomes a "More" button and the rest move into its flyout. The module list above therefore
/// never loses room to a longer list.
/// </summary>
public static class CommunityLinkSlots
{
    /// <summary>Sidebar slots reserved for community links, the "More" button included.</summary>
    public const int Count = 4;

    /// <summary>
    /// Splits <paramref name="links"/> into the rows shown inline and the rows behind "More".
    /// Up to <paramref name="slots"/> links are all inline; above that the first
    /// <c>slots - 1</c> are inline, so "More" never hides a single link that would have fit in
    /// its own slot.
    /// </summary>
    public static (IReadOnlyList<T> Inline, IReadOnlyList<T> Overflow) Split<T>(IReadOnlyList<T> links, int slots = Count)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentOutOfRangeException.ThrowIfLessThan(slots, 1);

        if (links.Count <= slots)
        {
            return (links, []);
        }

        var inlineCount = slots - 1;
        return (links.Take(inlineCount).ToList(), links.Skip(inlineCount).ToList());
    }
}
