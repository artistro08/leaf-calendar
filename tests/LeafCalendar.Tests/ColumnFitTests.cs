using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ColumnFitTests
{
    // The owner's crash: 809.6 DIPs at 125% is 1012 pixels, 253 per day for 4 days, so nothing is left over. The old
    // math left 0.0000123 DIPs, which layout rounding turned into a whole pixel, and the grid flipped between 202.4 and
    // 201.6 wide columns until WinUI gave up (LayoutCycleException)
    // (WinUI hands widths over with float noise: the trace showed 809.599976, and 809.6000123 with the old margin added)
    [Theory]
    [InlineData(809.6)]
    [InlineData(809.599976)]
    [InlineData(809.6000123)]
    public void Fit_WidthThatDividesEvenly_LeavesNoSpare(double available)
    {
        var (width, spare) = ColumnFit.Fit(available, 1.25, 4);

        Assert.Equal(202.4, width, 9);
        Assert.Equal(0, spare);
    }

    [Theory]
    [InlineData(809.6, 1.25, 4)]
    [InlineData(1000, 1.5, 7)]
    [InlineData(1234.4, 1.25, 3)]
    [InlineData(999.999, 1.75, 9)]
    [InlineData(640, 1, 5)]
    public void Fit_SpareIsWholePixels_AndFitsTheWidth(double available, double scale, int columns)
    {
        var (width, spare) = ColumnFit.Fit(available, scale, columns);

        var spareInPixels = spare * scale;
        Assert.Equal(Math.Round(spareInPixels), spareInPixels, 6);
        Assert.Equal(Math.Round(available * scale), Math.Round((width * columns + spare) * scale), 6);
        Assert.InRange(spareInPixels, 0, columns - 1 + 1e-6);
    }

    // What the grid sees on the next pass (the scroller's width, its margin added back) gives the same columns
    [Theory]
    [InlineData(809.6, 1.25, 4)]
    [InlineData(809.599976, 1.25, 4)]
    [InlineData(808.8, 1.25, 4)]
    [InlineData(1000, 1.5, 7)]
    public void Fit_FedItsOwnResult_IsStable(double available, double scale, int columns)
    {
        var first = ColumnFit.Fit(available, scale, columns);
        var scroller = Math.Round((available - first.Spare) * scale) / scale;

        Assert.Equal(first, ColumnFit.Fit(scroller + first.Spare, scale, columns));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(1.25, 0)]
    public void Fit_NoScaleOrNoColumns_Throws(double scale, int columns) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ColumnFit.Fit(800, scale, columns));

    [Fact]
    public void Fit_NarrowSpace_KeepsTheMinimumWidth()
    {
        var (width, spare) = ColumnFit.Fit(100, 1.25, 7);

        Assert.Equal(48, width);
        Assert.Equal(0, spare);
    }
}
