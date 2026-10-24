using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services;
using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The phone bridge is exposed to the model only through the curated tool set,
/// and every bridge call goes through the <see cref="IAdbPhone"/> seam so the
/// router can be tested without a real device. This pins: curated names route,
/// non-curated catalog names are refused (never silently run), unknown names are
/// rejected, and dispatch failures become ToolResult failures.
/// </summary>
public class PhoneToolServiceTests
{
    private sealed class FakeAdbPhone : IAdbPhone
    {
        public List<string> Calls { get; } = [];

        public ToolResult Execute(string name, JsonNode? args)
        {
            Calls.Add(name);
            if (name == "phone_take_photo") return ToolResult.Fail("adb: device offline", ToolError.Failed);
            return ToolResult.Ok($"{name} ran.");
        }
    }

    private static PhoneToolService Build(FakeAdbPhone phone) => new(phone);

    private static ToolResult Run(PhoneToolService service, string name)
    {
        var call = new ToolCall("id-1", name, JsonDocument.Parse("{}").RootElement);
        var handler = service.Handlers.Single(h => h.Name == name);
        return handler.ExecuteAsync(call, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public void ProducesOneHandlerPerCuratedTool()
    {
        var service = Build(new FakeAdbPhone());

        var names = service.Handlers.Select(h => h.Name).OrderBy(n => n).ToList();
        var curated = PhoneTools.Curated.Select(t => t.Name).OrderBy(n => n).ToList();

        Assert.Equal(curated, names);
    }

    [Fact]
    public void ACuratedToolRoutesToThePhone()
    {
        var phone = new FakeAdbPhone();
        var service = Build(phone);

        var result = Run(service, "phone_device_info");

        Assert.True(result.Success);
        Assert.Equal(["phone_device_info"], phone.Calls);
        Assert.Equal("phone_device_info ran.", result.Message);
    }

    [Fact]
    public void ARealButNonCuratedToolIsRefusedAndNotExecuted()
    {
        var phone = new FakeAdbPhone();
        var service = Build(phone);

        // phone_system_info exists in the full catalog but is NOT in the curated set.
        var call = new ToolCall("id-2", "phone_system_info", JsonDocument.Parse("{}").RootElement);
        var result = service.ExecuteAsync(call, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.Unsupported, result.Error);
        Assert.Empty(phone.Calls);
    }

    [Fact]
    public void APhantomPhoneToolIsRejected()
    {
        var phone = new FakeAdbPhone();
        var service = Build(phone);

        var result = service.ExecuteAsync(
            new ToolCall("id-3", "phone_flying_car", JsonDocument.Parse("{}").RootElement),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.Unsupported, result.Error);
        Assert.Empty(phone.Calls);
    }

    [Fact]
    public void ADeviceFailureSurfacesAsAToolResultFailureNotAnEmptySuccess()
    {
        var phone = new FakeAdbPhone();
        var service = Build(phone);

        var result = Run(service, "phone_take_photo");

        Assert.False(result.Success);
        Assert.Equal(ToolError.Failed, result.Error);
        Assert.Equal("adb: device offline", result.Message);
    }
}