using DiffusionNexus.Service.Classes;
using System.Collections.ObjectModel;
using System.Linq;

namespace DiffusionNexus.Service.Helper;

public static class CustomTagMapPriorityHelper
{
    public static ObservableCollection<CustomTagMap> Normalize(IEnumerable<CustomTagMap> mappings)
    {
        var ordered = mappings.OrderBy(m => m.Priority).ToList();
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].Priority = i + 1;
        return new ObservableCollection<CustomTagMap>(ordered);
    }

    public static void MoveUp(ObservableCollection<CustomTagMap> mappings, CustomTagMap map)
    {
        var ordered = mappings.OrderBy(m => m.Priority).ToList();
        var index = ordered.IndexOf(map);
        if (index > 0)
        {
            var above = ordered[index - 1];
            (above.Priority, ordered[index].Priority) = (ordered[index].Priority, above.Priority);
        }
    }

    public static void MoveDown(ObservableCollection<CustomTagMap> mappings, CustomTagMap map)
    {
        var ordered = mappings.OrderBy(m => m.Priority).ToList();
        var index = ordered.IndexOf(map);
        if (index >= 0 && index < ordered.Count - 1)
        {
            var below = ordered[index + 1];
            (below.Priority, ordered[index].Priority) = (ordered[index].Priority, below.Priority);
        }
    }
}
