using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One clickable chip in the Advanced Search tag cloud.</summary>
public partial class TagCloudEntryViewModel : ObservableObject
{
    public string Name { get; }

    /// <summary>How many indexed images carry this tag, gallery-wide.</summary>
    public int Count { get; }

    /// <summary>
    /// How many of those fall inside the CURRENT view scope — the toolbar's
    /// date/search/favorites filters plus the drawer's NSFW mode. Clicking a
    /// chip can only ever surface this many images, so showing the global
    /// count alone was a lie: "1girl (55)" clicked into an empty grid when
    /// every match sat outside the default 3-month date window.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private int _scopedCount;

    /// <summary>
    /// "name (in-scope/total)" when the view scope hides some carriers,
    /// "name (total)" when it hides none — the split only appears when it
    /// carries information.
    /// </summary>
    public string DisplayText => ScopedCount == Count
        ? $"{Name} ({Count})"
        : $"{Name} ({ScopedCount}/{Count})";

    [ObservableProperty]
    private bool _isActive;

    public TagCloudEntryViewModel(string name, int count)
    {
        Name = name;
        Count = count;
        _scopedCount = count;
    }
}
