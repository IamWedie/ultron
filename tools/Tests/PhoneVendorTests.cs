using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Camera launch and flash handling vary by vendor. The hardware is driven
/// through adb, but *which* package/intent to use is a pure brand→ops decision,
/// so it belongs in a testable map rather than scattered if/else in the client.
/// </summary>
public class PhoneVendorTests
{
    private static DeviceProfile Profile(string brand) =>
        new() { Brand = brand, Model = "X", Manufacturer = brand };

    [Theory]
    [InlineData("HONOR", "com.hihonor.camera")]
    [InlineData("Honor", "com.hihonor.camera")]
    [InlineData("honor", "com.hihonor.camera")]
    [InlineData("HUAWEI", "com.huawei.camera")]
    [InlineData("samsung", "com.sec.android.app.camera")]
    [InlineData("Google", "com.google.android.GoogleCamera")]
    [InlineData("pixel", "com.google.android.GoogleCamera")]
    [InlineData("xiaomi", "com.android.camera")]
    [InlineData("Xiaomi", "com.android.camera")]
    [InlineData("redmi", "com.android.camera")]
    [InlineData("POCO", "com.android.camera")]
    [InlineData("oneplus", "com.oneplus.camera")]
    [InlineData("motorola", "com.motorola.camera2")]
    [InlineData("OPPO", "com.oppo.camera")]
    [InlineData("vivo", "com.vivo.camera")]
    [InlineData("realme", "com.oppo.camera")]
    public void KnownBrandsResolveToTheirCameraPackage(string brand, string expected)
    {
        Assert.Equal(expected, PhoneVendors.CameraPackage(Profile(brand)));
    }

    [Fact]
    public void UnknownBrandHasNoPackageAndUsesTheGenericIntent()
    {
        Assert.Null(PhoneVendors.CameraPackage(Profile("Zebra")));
        Assert.Null(PhoneVendors.CameraPackage(Profile("")));
        Assert.Equal("android.media.action.STILL_IMAGE_CAMERA", PhoneVendors.GenericCameraIntent);
    }

    [Theory]
    [InlineData("HONOR", "com.hihonor.camera.FLASH_TOGGLE")]
    [InlineData("Honor", "com.hihonor.camera.FLASH_TOGGLE")]
    public void HonorDefinesAFlashToggleBroadcast(string brand, string expected)
    {
        Assert.Equal(expected, PhoneVendors.FlashToggleAction(Profile(brand)));
    }

    [Fact]
    public void NonHonorBrandsReturnNullForFlashToggle()
    {
        Assert.Null(PhoneVendors.FlashToggleAction(Profile("samsung")));
        Assert.Null(PhoneVendors.FlashToggleAction(Profile("")));
    }
}