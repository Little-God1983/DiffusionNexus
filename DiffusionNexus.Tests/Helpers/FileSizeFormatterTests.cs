using System.Globalization;
using DiffusionNexus.UI.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Helpers;

public class FileSizeFormatterTests
{
    /// <summary>
    /// Runs <paramref name="action"/> with the invariant culture so number
    /// formatting in <c>FormatKilobytes</c>/<c>Format</c> is deterministic on
    /// machines with a comma decimal separator (e.g. de-DE).
    /// </summary>
    private static void WithInvariantCulture(Action action)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-500)]
    public void FormatKilobytes_NonPositiveIsUnknown(double sizeKb)
        => FileSizeFormatter.FormatKilobytes(sizeKb).Should().Be("Unknown");

    [Fact]
    public void FormatKilobytes_SmallSizeRendersAsKB()
        => WithInvariantCulture(() =>
            FileSizeFormatter.FormatKilobytes(500).Should().Be("500.0 KB"));

    [Fact]
    public void FormatKilobytes_DelegatesToTheSharedFormatter()
        => FileSizeFormatter.FormatKilobytes(2048).Should().Be(FileSizeFormatter.Format(2048L * 1024));

    [Fact]
    public void FormatKilobytes_LargeSizeRendersGbThroughFormat()
        => FileSizeFormatter.FormatKilobytes(1_258_291).Should().Be(FileSizeFormatter.Format((long)(1_258_291 * 1024.0)));
}
