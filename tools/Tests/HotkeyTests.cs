using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class HotkeyTests
{
    private const uint Win = HotkeyService.ModWin;
    private const uint Ctrl = HotkeyService.ModControl;
    private const uint Alt = HotkeyService.ModAlt;
    private const uint Shift = HotkeyService.ModShift;

    [Theory]
    [InlineData("Win+Alt+P", 9, 0x50, "Win+Alt+P")]
    [InlineData("Ctrl+Shift+F9", 6, 0x78, "Ctrl+Shift+F9")]
    [InlineData("Alt+5", 1, 0x35, "Alt+5")]
    public void TryParse_ValidSpecs(string spec, uint expectMods, uint expectVk, string expectDisplay)
    {
        var ok = HotkeyService.TryParse(spec, out var mods, out var vk);
        Assert.True(ok, $"expected '{spec}' to parse");
        Assert.Equal(expectMods, mods);
        Assert.Equal(expectVk, vk);
        Assert.Equal(expectDisplay, HotkeyService.ToDisplay(mods, vk));
    }

    [Theory]
    [InlineData("")]
    [InlineData("P")]
    [InlineData("Win+Banana")]
    [InlineData("Ctrl+F13")]
    [InlineData("Win+Alt+")]
    [InlineData("Alt+Alt+Q")]
    [InlineData("Win+Alt+ZZ")]
    public void TryParse_InvalidSpecs(string spec)
    {
        Assert.False(HotkeyService.TryParse(spec, out _, out _), $"expected '{spec}' to be rejected");
    }

    [Theory]
    [InlineData("win+alt+p", "Win+Alt+P")]
    [InlineData("ctrl+f9", "Ctrl+F9")]
    [InlineData("alt+shift+M", "Alt+Shift+M")]
    [InlineData("Win+Ctrl+K", "Win+Ctrl+K")]
    public void ToDisplay_Normalizes(string spec, string expect)
    {
        Assert.True(HotkeyService.TryParse(spec, out var mods, out var vk));
        Assert.Equal(expect, HotkeyService.ToDisplay(mods, vk));
    }
}