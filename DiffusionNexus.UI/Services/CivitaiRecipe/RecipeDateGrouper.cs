using System.Globalization;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// Groups recipes the way the Recipes tab lists them: Today, This week, then a header per month,
/// newest first. Pure, so the tab and its tests share one definition of "today" and "this week".
/// </summary>
public static class RecipeDateGrouper
{
    /// <summary>Header of the group of recipes pasted since local midnight.</summary>
    public const string TodayHeader = "Today";

    /// <summary>Header of the group since Monday of the current ISO week, excluding today.</summary>
    public const string ThisWeekHeader = "This week";

    public static IReadOnlyList<(string Header, IReadOnlyList<T> Items)> Group<T>(
        IEnumerable<T> items, Func<T, DateTimeOffset> pastedAt, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var weekStart = WeekStart(today);

        var ordered = items
            .Select(i => (Item: i, Local: pastedAt(i).ToLocalTime().DateTime))
            .OrderByDescending(x => x.Local)
            .ToList();

        var result = new List<(string, IReadOnlyList<T>)>();
        foreach (var group in ordered.GroupBy(x => HeaderFor(x.Local, today, weekStart, nowLocal.Year), StringComparer.Ordinal))
            result.Add((group.Key, group.Select(x => x.Item).ToList()));

        return result;
    }

    public static string TimeLabel(DateTimeOffset pastedAt, DateTime nowLocal)
    {
        var local = pastedAt.ToLocalTime().DateTime;
        var today = nowLocal.Date;
        var inv = CultureInfo.InvariantCulture;

        if (local >= today) return local.ToString("HH:mm", inv);
        if (local >= WeekStart(today)) return local.ToString("ddd", inv);
        return local.Year == nowLocal.Year
            ? local.ToString("MMM d", inv)
            : local.ToString("MMM d, yyyy", inv);
    }

    private static string HeaderFor(DateTime local, DateTime today, DateTime weekStart, int currentYear)
    {
        if (local >= today) return TodayHeader;
        if (local >= weekStart) return ThisWeekHeader;
        return local.ToString(local.Year == currentYear ? "MMMM" : "MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private static DateTime WeekStart(DateTime today)
        => today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
}
