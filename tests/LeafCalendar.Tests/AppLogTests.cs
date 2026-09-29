using LeafCalendar.Core.Diagnostics;
using LeafCalendar.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace LeafCalendar.Tests;

public sealed class AppLogTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _folder.Dispose();

    [Theory]
    [InlineData("token ya29.a0AfB_byC-xyz leaked", "token [token] leaked")]
    [InlineData("refresh 1//0gAbc-def_ghi leaked", "refresh [token] leaked")]
    [InlineData("code 4/0AeanS0b-xyz leaked", "code [token] leaked")]
    [InlineData("jwt eyJhbGciOi.eyJzdWIi.sig leaked", "jwt [token] leaked")]
    [InlineData("secret GOCSPX-abc_DEF-123 leaked", "secret [token] leaked")]
    [InlineData("calendar family123@group.calendar.google.com synced", "calendar [email] synced")]
    [InlineData("account=109876543210 ok", "account=109876543210 ok")]
    public void Redact_SensitiveText_MasksIt(string input, string expected)
    {
        Assert.Equal(expected, AppLog.Redact(input));
    }

    [Fact]
    public void Info_Detail_WritesTimestampedRedactedLine()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Info("sync.calendar.done", "calendar=me@example.com changes=3");

        Assert.Equal(
            "2026-09-29T12:00:00.0000000+00:00 INFO sync.calendar.done calendar=[email] changes=3",
            File.ReadAllLines(log.FilePath).Single());
    }

    [Fact]
    public void Error_Exception_WritesTypeAndRedactedMessage()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Error("sync.failed", new InvalidOperationException("bad token ya29.abc"));

        Assert.EndsWith("ERROR sync.failed InvalidOperationException: bad token [token]", File.ReadAllLines(log.FilePath).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Info_OverOneMegabyte_RollsToBackupFile()
    {
        var log = new AppLog(_folder.Path, _time);
        File.WriteAllText(log.FilePath, new string('x', 1_000_001));

        log.Info("after.roll");

        Assert.True(File.Exists(log.FilePath + ".1"));
        Assert.Single(File.ReadAllLines(log.FilePath));
    }
}
