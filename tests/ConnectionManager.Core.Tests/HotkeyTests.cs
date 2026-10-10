using ConnectionManager.Core.Modes;

namespace ConnectionManager.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+G", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x47u)]
    [InlineData(" shift + F12 ", HotkeyModifiers.Shift, 0x7Bu)]
    [InlineData("Win+Control+5", HotkeyModifiers.Win | HotkeyModifiers.Control, 0x35u)]
    [InlineData("Ctrl+0", HotkeyModifiers.Control, 0x30u)]
    [InlineData("Alt+9", HotkeyModifiers.Alt, 0x39u)]
    [InlineData("Ctrl+F1", HotkeyModifiers.Control, 0x70u)]
    [InlineData("Ctrl+F24", HotkeyModifiers.Control, 0x87u)]
    [InlineData("F1", HotkeyModifiers.None, 0x70u)]
    public void Parses_valid_hotkeys(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("G")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Foo")]
    [InlineData("Hyper+G")]
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+F01")]
    [InlineData("Ctrl+F 1")]
    [InlineData("Ctrl+F+1")]
    [InlineData("Shift+G")]
    [InlineData("Ctrl+Ctrl+G")]
    [InlineData("Ctrl+Control+G")]
    [InlineData("+G")]
    [InlineData("Ctrl++")]
    [InlineData("Ctrl+G+H")]
    [InlineData("G+Ctrl")]
    [InlineData("Ctrl+Alt")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_invalid_hotkeys(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out var hotkey));
        Assert.Null(hotkey);
    }
}
