using System.Text.RegularExpressions;

namespace Ultron.Services.Tools.Phone;

/// <summary>
/// What ULTRON knows about the phone it is attached to, parsed from
/// <c>getprop</c> and <c>wm</c> output. Pure data: no adb, no UI.
/// </summary>
/// <remarks>
/// The vendor-specific operations (which camera package to launch, how to toggle
/// the flash) depend on the brand, so the brand has to be read from the device at
/// runtime rather than assumed. Parsing is separated from execution so it can be
/// tested against captured fixtures.
/// </remarks>
public sealed record DeviceProfile
{
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string AndroidRelease { get; init; } = "";
    public int Sdk { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public int Density { get; init; }

    /// <summary>True when a brand was actually read - a device is identifiable.</summary>
    public bool IsIdentified => !string.IsNullOrWhiteSpace(Brand);

    /// <summary>
    /// Case/whitespace-insensitive brand comparison. <c>getprop</c> casing varies
    /// by vendor ("HONOR", "samsung", "Google"), so a raw string compare would
    /// silently miss most devices.
    /// </summary>
    public bool BrandMatches(params string[] candidates)
    {
        var brand = Brand.Trim();
        foreach (var candidate in candidates)
            if (string.Equals(brand, candidate.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Builds a profile from raw command output. Any of the inputs may be empty;
    /// missing values become empty strings / zero rather than throwing.
    /// </summary>
    public static DeviceProfile FromGetprop(string getpropDump, string? wmSize = null, string? wmDensity = null)
    {
        var props = ParseGetprop(getpropDump);
        var (width, height) = ParseSize(wmSize);
        return new DeviceProfile
        {
            Brand = Get(props, "ro.product.brand"),
            Model = Get(props, "ro.product.model"),
            Manufacturer = Get(props, "ro.product.manufacturer"),
            AndroidRelease = Get(props, "ro.build.version.release"),
            Sdk = int.TryParse(Get(props, "ro.build.version.sdk"), out var sdk) ? sdk : 0,
            ScreenWidth = width,
            ScreenHeight = height,
            Density = ParseDensity(wmDensity),
        };
    }

    /// <summary>
    /// Parses <c>getprop</c> (a dump of <c>[key]: [value]</c> lines) into a map.
    /// Non-matching lines are ignored.
    /// </summary>
    public static Dictionary<string, string> ParseGetprop(string? dump)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dump)) return result;

        foreach (var line in dump.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*\[(?<key>[^\]]+)\]\s*:\s*\[(?<value>.*)\]\s*$");
            if (!match.Success) continue;
            result[match.Groups["key"].Value] = match.Groups["value"].Value;
        }
        return result;
    }

    /// <summary>
    /// Reads <c>wm size</c> output. An "Override size" line wins over "Physical
    /// size", since it reflects what the device is actually rendering.
    /// </summary>
    public static (int Width, int Height) ParseSize(string? wmSize)
    {
        if (string.IsNullOrEmpty(wmSize)) return (0, 0);

        var width = 0;
        var height = 0;
        foreach (var line in wmSize.Split('\n'))
        {
            var match = Regex.Match(line, @"(?<w>\d+)\s*x\s*(?<h>\d+)");
            if (!match.Success) continue;
            var w = int.Parse(match.Groups["w"].Value);
            var h = int.Parse(match.Groups["h"].Value);
            if (line.Contains("Override", StringComparison.OrdinalIgnoreCase))
                return (w, h);
            width = w;
            height = h;
        }
        return (width, height);
    }

    /// <summary>Reads the first integer out of <c>wm density</c> output.</summary>
    public static int ParseDensity(string? wmDensity)
    {
        if (string.IsNullOrEmpty(wmDensity)) return 0;
        var match = Regex.Match(wmDensity, @"(?<dpi>\d+)");
        return match.Success ? int.Parse(match.Groups["dpi"].Value) : 0;
    }

    private static string Get(Dictionary<string, string> props, string key) =>
        props.TryGetValue(key, out var value) ? value : "";
}
