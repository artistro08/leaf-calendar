using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public class TrayGlyphTests
{
    // The repo's Assets/Tray, found by walking up from the test binaries to the solution file
    private static string Assets
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeafCalendar.slnx")))
            {
                dir = dir.Parent;
            }

            return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("LeafCalendar.slnx not found"), "src", "LeafCalendar.App", "Assets", "Tray");
        }
    }

    [Theory]
    [InlineData(1, false, 16, "tray-dark-1-16.png")]
    [InlineData(25, true, 16, "tray-light-25-16.png")]
    [InlineData(31, false, 24, "tray-dark-31-24.png")]
    public void FileName_DayModeAndSize(int day, bool light, int size, string expected) =>
        Assert.Equal(expected, TrayGlyph.FileName(day, light, size));

    [Theory]
    [InlineData(16, 16)]
    [InlineData(18, 20)]
    [InlineData(28, 28)]
    [InlineData(64, 64)]
    [InlineData(80, 64)]
    public void SizeFor_ExactElseNextUpElseLargest(int iconSize, int expected) => Assert.Equal(expected, TrayGlyph.SizeFor(iconSize));

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public void FileName_NoSuchDay_Throws(int day) => Assert.Throws<ArgumentOutOfRangeException>(() => TrayGlyph.FileName(day, false, 16));

    [Fact]
    public void EveryDayModeAndSize_IsDrawnAtExactlyItsSize()
    {
        foreach (var light in new[] { false, true })
        {
            for (var day = 1; day <= 31; day++)
            {
                foreach (var size in TrayGlyph.Sizes)
                {
                    var file = Path.Combine(Assets, TrayGlyph.FileName(day, light, size));
                    Assert.True(File.Exists(file), $"{file} is missing.");

                    // A PNG's IHDR holds the width and height (big-endian) at bytes 16 and 20
                    var head = File.ReadAllBytes(file).AsSpan(0, 24);
                    Assert.Equal(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]));
                    Assert.Equal(size, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
                }
            }
        }
    }
}
