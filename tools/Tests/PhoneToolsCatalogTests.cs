using System.Collections.Generic;
using System.Linq;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The phone tool catalog is what the model is offered and what the router
/// dispatches on. A duplicated name means two identical declarations to the
/// model and an ambiguous dispatch, so uniqueness is a hard invariant.
/// </summary>
public class PhoneToolsCatalogTests
{
    [Fact]
    public void TheCatalogContainsNoDuplicateToolNames()
    {
        var names = PhoneTools.All.Select(t => t.Name).ToList();
        var duplicates = names
            .GroupBy(n => n)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} x{g.Count()}")
            .OrderBy(s => s)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "Duplicate phone tool names: " + string.Join(", ", duplicates));
    }

    [Fact]
    public void EveryToolNameIsNamespacedAndNonEmpty()
    {
        foreach (var tool in PhoneTools.All)
        {
            Assert.StartsWith("phone_", tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }

    /// <summary>
    /// The model is offered a curated subset (see the spec: exposing all ~122
    /// tools bloats model context and widens the risk surface). The curated set
    /// must be a real subset of the full catalog and stay unique.
    /// </summary>
    [Fact]
    public void TheCuratedSetIsAUniqueSubsetOfTheFullCatalog()
    {
        var all = PhoneTools.All.Select(t => t.Name).ToHashSet();
        Assert.NotEmpty(PhoneTools.Curated);

        foreach (var name in PhoneTools.Curated.Select(t => t.Name))
        {
            Assert.StartsWith("phone_", name);
            Assert.Contains(name, all);
        }

        var curatedNames = PhoneTools.Curated.Select(t => t.Name).ToList();
        Assert.Equal(curatedNames.Count, curatedNames.Distinct().Count());
    }

    [Fact]
    public void TheCuratedSetCoversTheSpecifiedAreas()
    {
        var names = PhoneTools.Curated.Select(t => t.Name).ToHashSet();

        // info / screen / input / apps / camera / files / calls+SMS / toggles
        Assert.Contains("phone_device_info", names);
        Assert.Contains("phone_screen_on", names);
        Assert.Contains("phone_screenshot", names);
        Assert.Contains("phone_open_app", names);
        Assert.Contains("phone_open_camera", names);
        Assert.Contains("phone_take_photo", names);
        Assert.Contains("phone_list_files", names);
        Assert.Contains("phone_send_sms", names);
        Assert.Contains("phone_wifi_on", names);

        Assert.InRange(names.Count, 20, 40);
    }
}
