using LeafCalendar.Core.Data;
using LeafCalendar.Core.Google;
using LeafCalendar.Tests.Support;

namespace LeafCalendar.Tests;

public sealed class ShareGroupStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    private static BusyRange At(int startHour, int endHour) => new(Now.AddHours(startHour), Now.AddHours(endHour));

    [Fact]
    public void Insert_ThenGetAll_ReadsTheGroupBack()
    {
        using var conn = _db.Database.Open();

        var id = ShareGroupStore.Insert(conn, "Coffee", "Here:\r\n{times}", "America/New_York", [At(3, 4), At(1, 2)], Now, []);

        var group = Assert.Single(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(id, group.Id);
        Assert.Equal("Coffee", group.Title);
        Assert.Equal("Here:\r\n{times}", group.Message);
        Assert.Equal("America/New_York", group.ZoneId);
        Assert.Equal([At(1, 2), At(3, 4)], group.Slots);
    }

    [Fact]
    public void Insert_CleansAndCapsTheTitle()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "  Lunch\r\n" + new string('x', 200), "", "UTC", [At(1, 2)], Now, []);

        var title = Assert.Single(ShareGroupStore.GetAll(conn, Now)).Title;
        Assert.True(title.Length <= ShareGroupStore.MaxTitleLength);
        Assert.StartsWith("Lunch", title, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', title);
    }

    [Fact]
    public void Insert_CapsTheMessage()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", new string('m', 5000), "UTC", [At(1, 2)], Now, []);

        Assert.Equal(LeafCalendar.Core.People.AvailabilityText.MaxMessageLength, Assert.Single(ShareGroupStore.GetAll(conn, Now)).Message.Length);
    }

    [Fact]
    public void Insert_OverlappingTimes_AreMerged()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 3), At(2, 4)], Now, []);

        Assert.Equal([At(1, 4)], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Slots);
    }

    [Fact]
    public void Update_ReplacesTitleMessageZoneAndTimes()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "Old", "old", "UTC", [At(1, 2)], Now, []);

        ShareGroupStore.Update(conn, id, "New", "new", "Europe/Paris", [At(5, 6)], []);

        var group = Assert.Single(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(("New", "new", "Europe/Paris"), (group.Title, group.Message, group.ZoneId));
        Assert.Equal([At(5, 6)], group.Slots);
    }

    [Fact]
    public void Update_NoTimesLeft_DeletesTheGroup()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, []);

        ShareGroupStore.Update(conn, id, "", "", "UTC", [], []);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(0L, conn.Query(null, "SELECT COUNT(*) FROM share_groups;", r => r.GetInt64(0)).Single());
    }

    [Fact]
    public void Delete_RemovesItsTimesToo()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2), At(3, 4)], Now, []);

        ShareGroupStore.Delete(conn, id);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
        Assert.Equal(0L, conn.Query(null, "SELECT COUNT(*) FROM share_slots;", r => r.GetInt64(0)).Single());
    }

    [Fact]
    public void GetAll_HidesEndedTimes()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "", "", "UTC", [At(-3, -2), At(-1, 1), At(2, 3)], Now, []);

        // A time still running stays (only an end at or before now is past)
        Assert.Equal([At(-1, 1), At(2, 3)], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Slots);
    }

    [Fact]
    public void GetAll_GroupWithOnlyEndedTimes_IsLeftOut()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "", "", "UTC", [At(-3, -2)], Now, []);

        Assert.Empty(ShareGroupStore.GetAll(conn, Now));
    }

    [Fact]
    public void GetAll_OldestGroupFirst()
    {
        using var conn = _db.Database.Open();
        var first = ShareGroupStore.Insert(conn, "A", "", "UTC", [At(5, 6)], Now, []);
        var second = ShareGroupStore.Insert(conn, "B", "", "UTC", [At(1, 2)], Now.AddMinutes(1), []);

        Assert.Equal([first, second], ShareGroupStore.GetAll(conn, Now).Select(g => g.Id));
    }

    [Fact]
    public void Prune_DropsEndedTimesAndEmptyGroups()
    {
        using var conn = _db.Database.Open();
        ShareGroupStore.Insert(conn, "Gone", "", "UTC", [At(-3, -2)], Now, []);
        ShareGroupStore.Insert(conn, "Kept", "", "UTC", [At(-3, -2), At(1, 2)], Now, []);

        ShareGroupStore.Prune(conn, Now);

        Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM share_groups;", r => r.GetInt64(0)).Single());
        Assert.Equal(1L, conn.Query(null, "SELECT COUNT(*) FROM share_slots;", r => r.GetInt64(0)).Single());
    }

    [Fact]
    public void Insert_KeepsTheGuestsInOrder()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, ["sam@example.com", " pat@example.com "]);

        Assert.Equal(["sam@example.com", "pat@example.com"], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Guests);
    }

    [Fact]
    public void Update_ReplacesTheGuests()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, ["pat@example.com", "lee@example.com"]);

        ShareGroupStore.Update(conn, id, "", "", "UTC", [At(1, 2)], ["sam@example.com"]);

        Assert.Equal(["sam@example.com"], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Guests);
    }

    [Fact]
    public void Insert_DropsEmptyAndDuplicateGuests()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, ["pat@example.com", " ", "PAT@example.com", "", "sam@example.com"]);

        Assert.Equal(["pat@example.com", "sam@example.com"], Assert.Single(ShareGroupStore.GetAll(conn, Now)).Guests);
    }

    [Fact]
    public void Insert_CapsEachGuestAndHowMany()
    {
        using var conn = _db.Database.Open();

        ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, [new string('g', 400), .. Enumerable.Range(0, 60).Select(i => $"p{i}@example.com")]);

        var guests = Assert.Single(ShareGroupStore.GetAll(conn, Now)).Guests;
        Assert.Equal(ShareGroupStore.MaxGuests, guests.Count);
        Assert.Equal(ShareGroupStore.MaxGuestEmailLength, guests[0].Length);
    }

    [Fact]
    public void Delete_RemovesItsGuestsToo()
    {
        using var conn = _db.Database.Open();
        var id = ShareGroupStore.Insert(conn, "", "", "UTC", [At(1, 2)], Now, ["pat@example.com"]);

        ShareGroupStore.Delete(conn, id);

        Assert.Equal(0L, conn.Query(null, "SELECT COUNT(*) FROM share_guests;", r => r.GetInt64(0)).Single());
    }
}
