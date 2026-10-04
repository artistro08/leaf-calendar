using LeafCalendar.Core.Events;

namespace LeafCalendar.Tests;

public class DescriptionAnchorsTests
{
    private static readonly Uri First = new("https://example.com/first");
    private static readonly Uri Second = new("https://example.com/second");
    private static readonly Uri Doc = new("https://example.com/doc");

    // Two Links With The Same Text Keep Their Own Targets
    [Fact]
    public void Resolve_TwoLinksSameText_KeepTheirOwnTargets()
    {
        var targets = DescriptionAnchors.Resolve([("here", First), ("here", Second)], [("here", 0), (" and ", null), ("here", 1)]);

        Assert.Equal([First, null, Second], targets);
    }

    // Deleting The First Doesn't Hand Its Target To The Second
    [Fact]
    public void Resolve_FirstLinkDeleted_SecondKeepsItsTarget()
    {
        Assert.Equal([Second], DescriptionAnchors.Resolve([("here", First), ("here", Second)], [("here", 1)]));
    }

    // Plain Text That Reads Like A Link Stays Plain
    [Fact]
    public void Resolve_PlainRunReadingLikeALink_StaysPlain()
    {
        Assert.Equal([null, Doc], DescriptionAnchors.Resolve([("Doc", Doc)], [("Doc", null), ("Doc", 0)]));
    }

    // An Edited Link, Or A Copy Of One, Is Plain Text
    [Fact]
    public void Resolve_EditedOrDuplicatedLink_IsPlain()
    {
        Assert.Equal([null], DescriptionAnchors.Resolve([("Doc", Doc)], [("Docs", 0)]));
        Assert.Equal([Doc, null], DescriptionAnchors.Resolve([("Doc", Doc)], [("Doc", 0), ("Doc", 0)]));
    }

    // A Format Change Inside A Link (Bold On Part Of It) Splits It Into Runs; The Link Stays On Every Piece
    [Fact]
    public void Resolve_LinkSplitByAFormatChange_KeepsItsTarget()
    {
        var targets = DescriptionAnchors.Resolve([("Design doc", Doc), ("here", First)], [("See ", null), ("Design", 0), (" doc", 0), (" or ", null), ("he", 1), ("r", 1), ("e", 1)]);

        Assert.Equal([null, Doc, Doc, null, First, First, First], targets);
    }

    // Pieces That Don't Add Up To The Link's Text Stay Plain
    [Fact]
    public void Resolve_SplitLinkWithChangedText_IsPlain()
    {
        Assert.Equal([null, null], DescriptionAnchors.Resolve([("Design doc", Doc)], [("Design", 0), (" docs", 0)]));
        Assert.Equal([null, null, null], DescriptionAnchors.Resolve([("Design doc", Doc)], [("Design", 0), (" and", null), (" doc", 0)]));
        Assert.Equal([null, Doc], DescriptionAnchors.Resolve([("Design doc", Doc)], [("Design", 0), ("Design doc", 0)]));
    }

    // Anchors Past The Slot Count Share A Tint But Still Go In Order
    [Fact]
    public void Resolve_MoreLinksThanSlots_GoInOrder()
    {
        var anchors = Enumerable.Range(0, DescriptionAnchors.Slots + 1).Select(i => ("x", new Uri($"https://example.com/{i}"))).ToList();

        var targets = DescriptionAnchors.Resolve(anchors, [("x", 0), ("x", 0)]);

        Assert.Equal([anchors[0].Item2, anchors[DescriptionAnchors.Slots].Item2], targets);
    }

    [Fact]
    public void Tint_RoundTripsEverySlot_AndRejectsOtherColors()
    {
        (byte R, byte G, byte B) accent = (0x00, 0x5F, 0xB8);

        for (var slot = 0; slot < DescriptionAnchors.Slots; slot++)
        {
            Assert.Equal(slot, DescriptionAnchors.SlotOf(DescriptionAnchors.Tint(accent, slot), accent));
        }

        Assert.Null(DescriptionAnchors.SlotOf((0xFF, 0xFF, 0xFF), accent));
        Assert.Null(DescriptionAnchors.SlotOf((0x00, 0x5F, 0xBC), accent));
    }
}
