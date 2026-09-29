using LeafCalendar.Core.Http;

namespace LeafCalendar.Tests;

public class QueryStringTests
{
    [Fact]
    public void Parse_EncodedValues_DecodesThem()
    {
        var query = QueryString.Parse("?code=4%2F0Ab&state=x+y&empty=&flag");

        Assert.Equal("4/0Ab", query["code"]);
        Assert.Equal("x y", query["state"]);
        Assert.Equal("", query["empty"]);
        Assert.Equal("", query["flag"]);
    }

    [Fact]
    public void Parse_DuplicateKeys_KeepsFirst()
    {
        var query = QueryString.Parse("state=first&state=second");

        Assert.Equal("first", query["state"]);
    }
}
