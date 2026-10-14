using System.Text;
using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiLineReaderTests
{
    private static ApiLineReader Reader(string text, int max = 1024) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

    [Fact]
    public async Task ReadLineAsync_SplitsOnNewlines_AndDropsCarriageReturns()
    {
        var reader = Reader("one\r\ntwo\nthree");
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("one", await reader.ReadLineAsync(ct));
        Assert.Equal("two", await reader.ReadLineAsync(ct));
        Assert.Equal("three", await reader.ReadLineAsync(ct));
        Assert.Null(await reader.ReadLineAsync(ct));
    }

    [Fact]
    public async Task ReadLineAsync_KeepsMultiByteCharactersWhole()
    {
        var reader = Reader("café ☕\n");

        Assert.Equal("café ☕", await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadLineAsync_LongLineAcrossBuffers_IsRead()
    {
        var line = new string('x', 10_000);
        var reader = Reader(line + "\n", max: 20_000);

        Assert.Equal(line, await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadLineAsync_OverLimit_Throws()
    {
        var reader = Reader(new string('x', 5_000), max: 4_096);

        await Assert.ThrowsAsync<ApiLineTooLongException>(() => reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }
}
