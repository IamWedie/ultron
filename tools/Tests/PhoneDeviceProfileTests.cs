using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// ULTRON must know which phone it is attached to before it can choose the
/// right vendor operations. The profile is parsed from `getprop`/`wm` output,
/// so the parsing is pure and belongs behind tests rather than adb calls.
/// </summary>
public class PhoneDeviceProfileTests
{
    private const string GetpropDump = """
        [ro.product.brand]: [HONOR]
        [ro.product.model]: [ANY-LX1]
        [ro.product.manufacturer]: [HONOR]
        [ro.build.version.release]: [13]
        [ro.build.version.sdk]: [33]
        [ro.serialno]: [ABC123]
        """;

    [Fact]
    public void ParsesBrandModelAndroidAndSdkFromGetprop()
    {
        var profile = DeviceProfile.FromGetprop(GetpropDump);

        Assert.Equal("HONOR", profile.Brand);
        Assert.Equal("ANY-LX1", profile.Model);
        Assert.Equal("13", profile.AndroidRelease);
        Assert.Equal(33, profile.Sdk);
    }

    [Fact]
    public void ParsesPhysicalScreenSize()
    {
        var profile = DeviceProfile.FromGetprop(GetpropDump, "Physical size: 1080x2340");

        Assert.Equal(1080, profile.ScreenWidth);
        Assert.Equal(2340, profile.ScreenHeight);
    }

    [Fact]
    public void PrefersOverrideSizeWhenPresent()
    {
        var dump = GetpropDump;
        var wm = """
            Physical size: 1080x2340
            Override size: 720x1560
            """;

        var profile = DeviceProfile.FromGetprop(dump, wm);

        Assert.Equal(720, profile.ScreenWidth);
        Assert.Equal(1560, profile.ScreenHeight);
    }

    [Fact]
    public void ParsesPhysicalDensity()
    {
        var profile = DeviceProfile.FromGetprop(GetpropDump, null, "Physical density: 480");

        Assert.Equal(480, profile.Density);
    }

    [Fact]
    public void MissingValuesBecomeEmptyRatherThanThrowing()
    {
        var profile = DeviceProfile.FromGetprop("");

        Assert.Equal("", profile.Brand);
        Assert.Equal("", profile.Model);
        Assert.Equal(0, profile.Sdk);
        Assert.Equal(0, profile.ScreenWidth);
        Assert.Equal(0, profile.ScreenHeight);
        Assert.Equal(0, profile.Density);
    }

    [Fact]
    public void BrandComparisonIsCaseAndWhitespaceInsensitive()
    {
        var profile = DeviceProfile.FromGetprop(GetpropDump);

        Assert.True(profile.BrandMatches("honor"));
        Assert.True(profile.BrandMatches("  HONOR "));
        Assert.False(profile.BrandMatches("samsung"));
    }
}
