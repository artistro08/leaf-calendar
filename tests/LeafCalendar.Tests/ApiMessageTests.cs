using System.Text.Json;
using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiMessageTests
{
    private static int CodeOf(string line) => Assert.Throws<ApiProtocolException>(() => ApiMessage.Parse(line)).Code;

    [Fact]
    public void Parse_Request_ReadsIdMethodAndParams()
    {
        var request = ApiMessage.Parse("""{"jsonrpc":"2.0","id":7,"method":"events.list","params":{"start":"x"}}""");

        Assert.Equal(7, request.Id!.Value.GetInt32());
        Assert.Equal("events.list", request.Method);
        Assert.Equal("x", request.Params.GetProperty("start").GetString());
    }

    [Fact]
    public void Parse_NoParams_IsAnEmptyObject()
    {
        var request = ApiMessage.Parse("""{"jsonrpc":"2.0","id":"a","method":"leaf.hello"}""");

        Assert.Equal(JsonValueKind.Object, request.Params.ValueKind);
    }

    [Fact]
    public void Parse_NoId_IsANotification()
    {
        Assert.Null(ApiMessage.Parse("""{"jsonrpc":"2.0","method":"leaf.hello"}""").Id);
    }

    [Fact]
    public void Parse_NullId_IsKept()
    {
        Assert.Equal(JsonValueKind.Null, ApiMessage.Parse("""{"jsonrpc":"2.0","id":null,"method":"leaf.hello"}""").Id!.Value.ValueKind);
    }

    [Fact]
    public void Parse_Errors_UseJsonRpcCodes()
    {
        Assert.Equal(ApiErrors.ParseError, CodeOf("{nope"));
        Assert.Equal(ApiErrors.InvalidRequest, CodeOf("""[{"jsonrpc":"2.0","id":1,"method":"a"}]"""));
        Assert.Equal(ApiErrors.InvalidRequest, CodeOf("""{"id":1,"method":"a"}"""));
        Assert.Equal(ApiErrors.InvalidRequest, CodeOf("""{"jsonrpc":"1.0","id":1,"method":"a"}"""));
        Assert.Equal(ApiErrors.InvalidRequest, CodeOf("""{"jsonrpc":"2.0","id":{},"method":"a"}"""));
        Assert.Equal(ApiErrors.InvalidRequest, CodeOf("""{"jsonrpc":"2.0","id":1,"method":""}"""));
        Assert.Equal(ApiErrors.InvalidParams, CodeOf("""{"jsonrpc":"2.0","id":1,"method":"a","params":[1]}"""));
    }

    [Fact]
    public void Result_WritesTheEnvelope()
    {
        using var id = JsonDocument.Parse("3");
        using var result = JsonDocument.Parse("""{"ok":true}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":3,"result":{"ok":true}}""", ApiMessage.Result(id.RootElement, result.RootElement));
    }

    [Fact]
    public void Error_WithDataCode_WritesItUnderData()
    {
        using var id = JsonDocument.Parse("\"x\"");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":"x","error":{"code":-32000,"message":"No event with that ID.","data":{"code":"not_found"}}}""",
            ApiMessage.Error(id.RootElement, ApiErrors.ServerError, "No event with that ID.", ApiErrors.NotFound));
    }

    [Fact]
    public void Error_WithoutId_WritesNull()
    {
        Assert.StartsWith("""{"jsonrpc":"2.0","id":null,""", ApiMessage.Error(null, ApiErrors.ParseError, "Parse error."), StringComparison.Ordinal);
    }

    [Fact]
    public void NotApproved_EchoesTheRequestId()
    {
        var reply = ApiMessage.NotApproved("""{"jsonrpc":"2.0","id":42,"method":"events.list"}""");

        Assert.Contains("\"id\":42", reply, StringComparison.Ordinal);
        Assert.Contains("\"not_approved\"", reply, StringComparison.Ordinal);
    }
}
