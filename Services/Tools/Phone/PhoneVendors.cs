namespace Ultron.Services.Tools.Phone;

/// <summary>
/// Resolves phone brand → vendor-specific operations (camera package, flash
/// broadcast). Pure mapping: no adb, no UI, fully unit-testable.
/// </summary>
/// <remarks>
/// The camera cannot be opened blindly with <c>am start -a STILL_IMAGE_CAMERA</c>
/// on every phone: some vendors keep a single extra-camera action that triggers
/// the wrong thing, and launcher resolution by package is the most reliable path.
/// Each entry is the best-known launcher package for that brand; an unknown brand
/// falls back to the generic camera intent.
/// </remarks>
public static class PhoneVendors
{
    /// <summary>Generic intent used when no brand-specific package is known.</summary>
    public const string GenericCameraIntent = "android.media.action.STILL_IMAGE_CAMERA";

    /// <summary>Maps a device profile to the camera launcher package, or null
    /// when the brand is unknown and the caller should use the generic intent.</summary>
    public static string? CameraPackage(DeviceProfile profile)
    {
        if (profile.BrandMatches("honor"))
            return "com.hihonor.camera";
        if (profile.BrandMatches("huawei"))
            return "com.huawei.camera";
        if (profile.BrandMatches("samsung"))
            return "com.sec.android.app.camera";
        if (profile.BrandMatches("google", "pixel"))
            return "com.google.android.GoogleCamera";
        if (profile.BrandMatches("xiaomi", "redmi", "poco"))
            return "com.android.camera";
        if (profile.BrandMatches("oneplus"))
            return "com.oneplus.camera";
        if (profile.BrandMatches("nothing"))
            return "com.nothing.camera";
        if (profile.BrandMatches("motorola"))
            return "com.motorola.camera2";
        if (profile.BrandMatches("oppo", "realme"))
            return "com.oppo.camera";
        if (profile.BrandMatches("vivo"))
            return "com.vivo.camera";
        return null;
    }

    /// <summary>
    /// Flash toggle broadcast, or null when the brand has no known broadcast
    /// (callers then fall back to the generic keycode / camera controls path).
    /// </summary>
    public static string? FlashToggleAction(DeviceProfile profile) =>
        profile.BrandMatches("honor")
            ? "com.hihonor.camera.FLASH_TOGGLE"
            : null;
}