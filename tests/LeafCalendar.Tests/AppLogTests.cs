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
    [InlineData("code=4%2F0AeanS0b-xyz leaked", "code=[token] leaked")]
    [InlineData("refresh 1%2F%2F0gAbc-def leaked", "refresh [token] leaked")]
    [InlineData("calendars/me%40gmail.com/events", "calendars/[email]/events")]
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

    [Fact]
    public void Info_ThirdMegabyte_KeepsTwoBackupsAndDropsTheOldest()
    {
        var log = new AppLog(_folder.Path, _time);
        File.WriteAllText(log.FilePath + ".2", "oldest");
        File.WriteAllText(log.FilePath + ".1", "older");
        File.WriteAllText(log.FilePath, new string('x', 1_000_001));

        log.Info("after.roll");

        Assert.Equal("older", File.ReadAllText(log.FilePath + ".2"));
        Assert.StartsWith("xxx", File.ReadAllText(log.FilePath + ".1"), StringComparison.Ordinal);
        Assert.False(File.Exists(log.FilePath + ".3"));
    }

    [Fact]
    public void Trace_DetailedOff_WritesNothing_On_WritesTheBreadcrumb()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Trace("command", "Today");
        Assert.False(File.Exists(log.FilePath));

        log.Detailed = true;
        log.Trace("command", "Today");
        Assert.EndsWith("TRACE command Today", File.ReadAllLines(log.FilePath).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Crash_Exception_WritesTypeAndStackWithoutTheMessage_EvenWithDetailedOff()
    {
        var log = new AppLog(_folder.Path, _time);
        Exception thrown;
        try
        {
            throw new InvalidOperationException("Dinner with me@example.com", new FormatException("Planning notes"));
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        log.Crash("app.unhandled", thrown);

        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("CRASH app.unhandled error=System.InvalidOperationException hresult=0x80131509", text, StringComparison.Ordinal);
        Assert.Contains("caused by System.FormatException", text, StringComparison.Ordinal);
        Assert.Contains("    at LeafCalendar.Tests.AppLogTests.Crash_Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dinner", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Planning", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Crash_FrameworkMessage_IsKeptAndRedacted()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Crash("app.unhandled", new InvalidOperationException("x"), "Layout cycle detected for me@example.com");

        Assert.Contains("message=Layout cycle detected for [email]", File.ReadAllText(log.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void NextDumpPath_KeepsRoomForOnlyTheNewestDumps()
    {
        var log = new AppLog(_folder.Path, _time);
        File.WriteAllText(Path.Combine(_folder.Path, "crash-20260101-000000-000.dmp"), "");
        File.WriteAllText(Path.Combine(_folder.Path, "crash-20260102-000000-000.dmp"), "");
        File.WriteAllText(Path.Combine(_folder.Path, "crash-20260103-000000-000.dmp"), "");

        var next = log.NextDumpPath();
        File.WriteAllText(next, "");

        Assert.Equal(
            ["crash-20260103-000000-000.dmp", "crash-20260929-120000-000.dmp"],
            Directory.GetFiles(_folder.Path, "crash-*.dmp").Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Info_DetailWithNewlines_ReplacesWithSpaces()
    {
        var log = new AppLog(_folder.Path, _time);

        log.Info("test.event", "line1\r\nline2\nline3");

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Single(lines);
        var line = lines[0];
        Assert.DoesNotContain("\r", line);
        Assert.DoesNotContain("\n", line);
        Assert.Contains("line1", line);
        Assert.Contains("line2", line);
        Assert.Contains("line3", line);
    }
}
