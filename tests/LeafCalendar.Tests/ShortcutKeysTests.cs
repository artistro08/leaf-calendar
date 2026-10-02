using LeafCalendar.Core.Views;

namespace LeafCalendar.Tests;

public class ShortcutKeysTests
{
    // Keys as [key], words between them as plain text, joined by spaces
    static string Show(string shortcut) =>
        string.Join(' ', ShortcutKeys.Parse(shortcut).Select(p => p.IsKey ? $"[{p.Text}]" : p.Text));

    [Theory]
    [InlineData("T", "[T]")]
    [InlineData("Ctrl+K", "[Ctrl] [K]")]
    [InlineData("Ctrl+Shift+L", "[Ctrl] [Shift] [L]")]
    [InlineData("Ctrl+,", "[Ctrl] [,]")]
    [InlineData("Ctrl+-", "[Ctrl] [-]")]
    [InlineData("Ctrl++", "[Ctrl] [+]")]
    [InlineData("Ctrl+= / Ctrl+- / Ctrl+0", "[Ctrl] [=] / [Ctrl] [-] / [Ctrl] [0]")]
    [InlineData("B or Shift+N", "[B] or [Shift] [N]")]
    [InlineData("E then Y / N / M", "[E] then [Y] / [N] / [M]")]
    [InlineData("← / →", "[←] / [→]")]
    [InlineData("?", "[?]")]
    [InlineData("/", "/")]
    [InlineData("2–9", "[2–9]")]
    [InlineData("Ctrl+F or /", "[Ctrl] [F] or /")]
    public void Parse_SplitsKeysFromTheWordsBetweenThem(string shortcut, string expected) =>
        Assert.Equal(expected, Show(shortcut));

    [Theory]
    [InlineData("Shift+drag", "[Shift] drag")]
    [InlineData("Ctrl+click", "[Ctrl] click")]
    [InlineData("Alt+drag", "[Alt] drag")]
    public void Parse_MouseActions_AreWordsNotKeys(string shortcut, string expected) =>
        Assert.Equal(expected, Show(shortcut));

    [Fact]
    public void Parse_CommaAndMouseWords_RunTogether() =>
        Assert.Equal("[Alt] [Left] / [Alt] [Right] , mouse back / forward", Show("Alt+Left / Alt+Right, mouse back / forward"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Blank_IsEmpty(string? shortcut) => Assert.Empty(ShortcutKeys.Parse(shortcut));

    [Fact]
    public void Parse_EveryCheatSheetRow_HasAKey()
    {
        foreach (var row in ShortcutCatalog.Rows)
        {
            Assert.Contains(ShortcutKeys.Parse(row.Keys), p => p.IsKey);
        }
    }

    [Fact]
    public void Part_ToString_IsItsText() => Assert.Equal("Ctrl", new ShortcutKeyPart("Ctrl", IsKey: true).ToString());
}
