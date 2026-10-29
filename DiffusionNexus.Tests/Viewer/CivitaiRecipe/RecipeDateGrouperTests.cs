using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #667: the Recipes tab lists newest first under Today, This week, then one header per month.
/// </summary>
public sealed class RecipeDateGrouperTests
{
    private static DateTimeOffset At(int y, int m, int d, int h = 0, int min = 0, int s = 0)
        => new(new DateTime(y, m, d, h, min, s, DateTimeKind.Local));

    private static DateTime Local(int y, int m, int d, int h = 0, int min = 0)
        => new(y, m, d, h, min, 0, DateTimeKind.Local);

    [Fact]
    public void GroupsTodayThisWeekMonthsAndYears()
    {
        var now = Local(2026, 10, 28, 14);
        var items = new[]
        {
            At(2025, 12, 24, 10),
            At(2026, 10, 2, 8),
            At(2026, 10, 25, 12),
            At(2026, 10, 26, 18),
            At(2026, 10, 28, 9),
        };

        var groups = RecipeDateGrouper.Group(items, i => i, now);

        groups.Select(g => g.Header).Should().Equal("Today", "This week", "October", "December 2025");
        groups.Select(g => g.Items.Count).Should().Equal(1, 1, 2, 1);
        groups[2].Items.Should().Equal(At(2026, 10, 25, 12), At(2026, 10, 2, 8));
    }

    [Fact]
    public void MondayMorning_YesterdayIsLastMonthOrMonthHeader()
    {
        var now = Local(2026, 11, 2, 8);
        var items = new[] { At(2026, 11, 1, 20), At(2026, 10, 31, 20), At(2026, 11, 2, 7) };

        var groups = RecipeDateGrouper.Group(items, i => i, now);

        groups.Select(g => g.Header).Should().Equal("Today", "November", "October");
        groups.Select(g => g.Header).Should().NotContain("This week");
    }

    [Fact]
    public void ItemsAfterMidnight_AreToday()
    {
        var now = Local(2026, 10, 28, 14);

        var groups = RecipeDateGrouper.Group(new[] { At(2026, 10, 28, 0, 0, 30) }, i => i, now);

        groups.Should().ContainSingle().Which.Header.Should().Be("Today");
    }

    [Fact]
    public void TimeLabel_Cases()
    {
        var now = Local(2026, 10, 28, 14);

        RecipeDateGrouper.TimeLabel(At(2026, 10, 28, 9), now).Should().Be("09:00");
        RecipeDateGrouper.TimeLabel(At(2026, 10, 26, 18), now).Should().Be("Mon");
        RecipeDateGrouper.TimeLabel(At(2026, 10, 2, 8), now).Should().Be("Oct 2");
        RecipeDateGrouper.TimeLabel(At(2025, 12, 24, 10), now).Should().Be("Dec 24, 2025");
    }
}
