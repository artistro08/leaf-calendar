using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+J", "Ctrl+Alt+J")]
    [InlineData("alt + control + j", "Ctrl+Alt+J")]
    [InlineData("Win+Shift+F9", "Shift+Win+F9")]
    [InlineData("Ctrl+Alt+Shift+7", "Ctrl+Alt+Shift+7")]
    [InlineData("Ctrl+F24", "Ctrl+F24")]
    public void TryParse_Valid_WritesCanonicalText(string text, string expected)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(expected, hotkey.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("J")]
    [InlineData("Shift+J")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+J+K")]
    [InlineData("Ctrl+Ctrl+J")]
    [InlineData("Ctrl+Alt+Space")]
    [InlineData("Ctrl+F12")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Alt+ÿ")]
    public void TryParse_Invalid_IsRefused(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void TryCreate_KeyCodes_MatchWin32()
    {
        Assert.True(Hotkey.TryCreate(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x4A, out var join));
        Assert.Equal("Ctrl+Alt+J", join.ToString());
        Assert.Equal(0x3, (int)join.Modifiers);

        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Shift, 0x4A, out _));
        Assert.False(Hotkey.TryCreate(HotkeyModifiers.Ctrl, 0x11, out _));
    }
}
