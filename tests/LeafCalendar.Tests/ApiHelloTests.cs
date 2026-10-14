using System.Buffers.Binary;
using LeafCalendar.Core.Api;

namespace LeafCalendar.Tests;

public sealed class ApiHelloTests
{
    // A real 1 × 1 PNG
    internal const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private const string Code = "0123456789abcdef0123456789abcdef";

    public static TheoryData<string> BadIcons => new()
    {
        "not base64!",
        PngHeader(257, 16),
        PngHeader(16, 257),
        PngHeader(0, 16),
        PngHeader(16, 0),
        Convert.ToBase64String("GIF89a, not a PNG at all...."u8.ToArray()),
        Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47]),
        PngHeader(16, 16, extra: ApiHello.MaxIconBytes),
    };

    private static string Hello(string paramsJson) => $$"""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{{paramsJson}}}""";

    // A PNG's first 24 bytes (signature, IHDR length and type, width, height), which is all Leaf Calendar checks, plus padding
    internal static string PngHeader(uint width, uint height, int extra = 0)
    {
        var bytes = new byte[24 + extra];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void Read_IconAndLink_KeepsBoth()
    {
        var hello = ApiHello.Read(Hello($$"""{"client":"Raycast","icon":"{{OnePixelPng}}","returnUrl":"raycast://extensions/leaf"}"""));

        Assert.Equal(Convert.FromBase64String(OnePixelPng), hello.Icon.ToArray());
        Assert.Equal("raycast://extensions/leaf", hello.ReturnUrl!.OriginalString);
    }

    [Fact]
    public void Read_HelloWithoutExtras_HasNeither()
    {
        var hello = ApiHello.Read(Hello("""{"client":"Raycast"}"""));

        Assert.True(hello.Icon.IsEmpty);
        Assert.Null(hello.ReturnUrl);
    }

    [Fact]
    public void Read_AskOnLink_KeepsTheCode()
    {
        var hello = ApiHello.Read(Hello($$"""{"client":"Raycast","askOnLink":"{{Code}}"}"""));

        Assert.Equal(Code, hello.LinkCode);
    }

    [Theory]
    [InlineData("""{"client":"Raycast"}""", "icon=none returnUrl=none askOnLink=none")]
    [InlineData("""{"icon":"not base64!","returnUrl":"raycast://extensions/leaf"}""", "icon=refused returnUrl=ok askOnLink=none")]
    [InlineData("""{"icon":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==","returnUrl":"https://example.com"}""", "icon=ok returnUrl=refused askOnLink=none")]
    [InlineData("""{"askOnLink":"0123456789abcdef0123456789abcdef"}""", "icon=none returnUrl=none askOnLink=ok")]
    [InlineData("""{"askOnLink":"short"}""", "icon=none returnUrl=none askOnLink=refused")]
    public void Read_SaysWhatWasUsedForTheLog(string paramsJson, string log)
    {
        Assert.Equal(log, ApiHello.Read(Hello(paramsJson)).Log);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"events.list","params":{"icon":"x"}}""")]
    [InlineData("not json")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"surprise":1}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"leaf.hello","params":{"icon":5}}""")]
    public void Read_NotAUsableHello_IsNone(string line)
    {
        Assert.Same(ApiHello.None, ApiHello.Read(line));
    }

    [Theory]
    [MemberData(nameof(BadIcons))]
    public void ReadIcon_BreaksARule_IsEmpty(string base64)
    {
        Assert.True(ApiHello.ReadIcon(base64).IsEmpty);
    }

    [Fact]
    public void ReadIcon_LargestAllowed_Passes()
    {
        Assert.False(ApiHello.ReadIcon(PngHeader(256, 256, extra: ApiHello.MaxIconBytes - 24)).IsEmpty);
    }

    [Theory]
    [InlineData("https://example.com/back")]
    [InlineData("http://example.com")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("ms-settings:privacy")]
    [InlineData("MS-SETTINGS:privacy")]
    [InlineData("ms-word:ofe|u|https://example.com/a.docx")]
    [InlineData("shell:startup")]
    [InlineData("search-ms:query=x")]
    [InlineData("search:query=x")]
    [InlineData("microsoft-edge:https://example.com")]
    [InlineData("MICROSOFT-EDGE:https://example.com")]
    [InlineData("raycast://user:secret@extensions/leaf")]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadReturnUrl_Refused_IsNull(string? text)
    {
        Assert.Null(ApiHello.ReadReturnUrl(text));
    }

    [Fact]
    public void ReadReturnUrl_TooLong_IsNull()
    {
        Assert.Null(ApiHello.ReadReturnUrl("raycast://" + new string('a', ApiHello.MaxReturnUrlLength)));
    }

    [Theory]
    [InlineData("raycast://extensions/leaf")]
    [InlineData("obsidian://open?vault=notes")]
    [InlineData("slack:open")]
    public void ReadReturnUrl_AppScheme_Passes(string text)
    {
        Assert.Equal(text, ApiHello.ReadReturnUrl(text)!.OriginalString);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("AbC_-0123456789abcdef0123456789abcdef")]
    public void ReadLinkCode_RandomCode_Passes(string code)
    {
        Assert.Equal(code, ApiHello.ReadLinkCode(code));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcde!")]
    [InlineData("0123456789abcdef 0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef&x=1")]
    [InlineData("")]
    [InlineData(null)]
    public void ReadLinkCode_BreaksARule_IsNull(string? code)
    {
        Assert.Null(ApiHello.ReadLinkCode(code));
    }

    [Fact]
    public void ReadLinkCode_TooLong_IsNull()
    {
        Assert.Null(ApiHello.ReadLinkCode(new string('a', ApiHello.MaxLinkCodeLength + 1)));
    }

    [Theory]
    [InlineData("leaf-calendar://connect?code=" + Code)]
    [InlineData("LEAF-CALENDAR://CONNECT?code=" + Code)]
    [InlineData("leaf-calendar://connect/?code=" + Code)]
    public void ReadConnectLink_ConnectLink_IsTheCode(string link)
    {
        Assert.Equal(Code, ApiHello.ReadConnectLink(link));
    }

    [Theory]
    [InlineData("leaf-calendar:")]
    [InlineData("leaf-calendar://connect")]
    [InlineData("leaf-calendar://connect?code=short")]
    [InlineData("leaf-calendar://connect?code=" + Code + "&then=delete")]
    [InlineData("leaf-calendar://connect?other=" + Code)]
    [InlineData("leaf-calendar://connect/more?code=" + Code)]
    [InlineData("leaf-calendar://open?code=" + Code)]
    [InlineData("leaf-calendar://user@connect?code=" + Code)]
    [InlineData("raycast://connect?code=" + Code)]
    [InlineData("not a link")]
    [InlineData(null)]
    public void ReadConnectLink_AnythingElse_IsNull(string? link)
    {
        Assert.Null(ApiHello.ReadConnectLink(link));
    }
}
