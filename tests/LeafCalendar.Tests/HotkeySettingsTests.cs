using LeafCalendar.Core.Tray;

namespace LeafCalendar.Tests;

public sealed class HotkeySettingsTests
{
    // Win32 Virtual-Key Codes
    private const int Tab = 0x09, J = 0x4A, L = 0x4C, F4 = 0x73, F7 = 0x76, F12 = 0x7B, Left = 0x25, Semicolon = 0xBA;

    [Fact]
    public void Empty_IsEmptyAndNotValid()
    {
        var empty = new HotkeySettings();

        Assert.True(empty.IsEmpty());
        Assert.False(empty.IsValid());
        Assert.Empty(empty.GetKeysList());
    }

    [Theory]
    [InlineData(false, true, true, false, J, "Ctrl+Alt+J")]
    [InlineData(true, false, false, true, F7, "Shift+Win+F7")]
    [InlineData(false, true, false, false, 0x37, "Ctrl+7")]
    public void LeafShortcuts_AreValid_InLeafsText(bool win, bool ctrl, bool alt, bool shift, int code, string text)
    {
        var settings = new HotkeySettings(win, ctrl, alt, shift, code);

        Assert.True(settings.IsValid());
        Assert.Equal(text, settings.ToHotkey()?.ToString());
    }

    [Theory]
    [InlineData(false, false, false, false, J)]   // No modifier
    [InlineData(false, false, false, true, J)]    // Shift alone is a capital letter (PowerToys allows it; Leaf doesn't)
    [InlineData(false, true, true, false, 0)]     // Modifiers only
    [InlineData(false, true, false, false, F12)]  // Kept for debuggers
    [InlineData(false, true, false, false, Left)] // Not a key Leaf registers
    [InlineData(true, false, false, false, L)]    // Windows' lock
    [InlineData(false, false, true, false, F4)]   // Windows' close
    [InlineData(false, false, false, false, Tab)] // Accessible (moves focus)
    [InlineData(false, false, false, true, Tab)]
    public void Others_AreNotValid(bool win, bool ctrl, bool alt, bool shift, int code)
    {
        Assert.False(new HotkeySettings(win, ctrl, alt, shift, code).IsValid());
    }

    [Theory]
    [InlineData(false, false, false, false, J, "Invalid shortcut. It must start with the Windows key, Ctrl, or Alt.")]
    [InlineData(false, false, false, true, J, "Invalid shortcut. It must start with the Windows key, Ctrl, or Alt.")]
    [InlineData(false, true, false, false, Left, "Invalid shortcut. It must end with a letter, a number, or an F key.")]
    [InlineData(false, true, false, false, F12, "Invalid shortcut. Windows keeps Ctrl+F12 for itself.")]
    [InlineData(true, false, false, false, L, "Invalid shortcut. Windows keeps Win+L for itself.")]
    [InlineData(false, false, true, false, F4, "Invalid shortcut. Windows keeps Alt+F4 for itself.")]
    public void InvalidReason_SaysWhy(bool win, bool ctrl, bool alt, bool shift, int code, string reason) =>
        Assert.Equal(reason, new HotkeySettings(win, ctrl, alt, shift, code).InvalidReason());

    [Fact]
    public void InvalidReason_NoneForValidOrEmpty()
    {
        Assert.Null(new HotkeySettings(false, true, true, false, J).InvalidReason());
        Assert.Null(new HotkeySettings().InvalidReason());
    }

    [Fact]
    public void AccessibleShortcut_IsTabOrShiftTabOnly()
    {
        Assert.True(new HotkeySettings(false, false, false, false, Tab).IsAccessibleShortcut());
        Assert.True(new HotkeySettings(false, false, false, true, Tab).IsAccessibleShortcut());
        Assert.False(new HotkeySettings(false, true, false, false, Tab).IsAccessibleShortcut());
    }

    [Fact]
    public void KeysList_IsPowerToysOrder_WinAndShiftAsKeyCodes()
    {
        var keys = new HotkeySettings(true, true, true, true, J).GetKeysList();

        Assert.Equal([92, "Ctrl", "Alt", 16, "J"], keys);
    }

    [Fact]
    public void KeysList_NamesKeysLeafDoesntRegister()
    {
        Assert.Equal(["Ctrl", 37], new HotkeySettings(false, true, false, false, Left).GetKeysList());
        Assert.Equal(["Ctrl", "F12"], new HotkeySettings(false, true, false, false, F12).GetKeysList());
        Assert.Equal(["Ctrl", "Key BA"], new HotkeySettings(false, true, false, false, Semicolon).GetKeysList());
    }

    [Fact]
    public void Text_ReadsLikeTheKeyCaps()
    {
        Assert.Equal("Win+Shift+F7", new HotkeySettings(true, false, false, true, F7).ToString());
        Assert.Equal("Ctrl+Alt", new HotkeySettings(false, true, true, false, 0).ToString());
        Assert.Equal("", new HotkeySettings().ToString());
    }

    [Fact]
    public void AltGrRisk_IsCtrlAltWithoutWin()
    {
        Assert.True(new HotkeySettings(false, true, true, false, J).IsAltGrRisk);
        Assert.False(new HotkeySettings(true, true, true, false, J).IsAltGrRisk);
        Assert.False(new HotkeySettings(false, true, true, false, 0).IsAltGrRisk);
    }

    [Fact]
    public void FromHotkey_RoundTrips()
    {
        Assert.True(Hotkey.TryParse("Ctrl+Alt+Shift+F9", out var hotkey));

        var settings = HotkeySettings.FromHotkey(hotkey);

        Assert.Equal(new HotkeySettings(false, true, true, true, 0x78), settings);
        Assert.Equal("Ctrl+Alt+Shift+F9", settings.ToHotkey()?.ToString());
    }
}
