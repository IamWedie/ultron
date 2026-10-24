using System;
using System.IO;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Two ADB hazards are pinned here: commands must be addressed to the configured
/// device (otherwise adb uses the default, or refuses when several are attached),
/// and a command that exits non-zero must not look like an empty success.
/// </summary>
public class PhoneTransportTests
{
    [Fact]
    public void DeviceCommandsArePinnedToTheConfiguredTransport()
    {
        using var client = new AdbClient("adb.exe", "192.168.1.50", 5555);

        Assert.Equal(
            ["-s", "192.168.1.50:5555", "shell", "getprop"],
            client.BuildArguments(["shell", "getprop"], targetDevice: true));
    }

    [Fact]
    public void ServerLevelCommandsAreNotPinned()
    {
        using var client = new AdbClient("adb.exe", "192.168.1.50", 5555);

        Assert.Equal(["connect", "192.168.1.50:5555"],
            client.BuildArguments(["connect", "192.168.1.50:5555"], targetDevice: false));
    }

    [Fact]
    public void ASerialIsUsedWhenNoWirelessAddressIsSet()
    {
        using var client = new AdbClient("adb.exe", "", serial: "ABC123");

        Assert.Equal(["shell", "getprop"],
            client.BuildArguments(["shell", "getprop"], targetDevice: false));
        Assert.Equal(["-s", "ABC123", "shell", "getprop"],
            client.BuildArguments(["shell", "getprop"], targetDevice: true));
    }

    [Fact]
    public void NoConfiguredDeviceLeavesTheCommandUnpinned()
    {
        using var client = new AdbClient("adb.exe", "");

        Assert.Equal(["devices"], client.BuildArguments(["devices"], targetDevice: true));
    }

    [Theory]
    [InlineData("192.168.1.50:5555", "List of devices attached\n192.168.1.50:5555\tdevice\n", true)]
    [InlineData("192.168.1.50:5555", "List of devices attached\n192.168.1.51:5555\tdevice\n", false)]
    [InlineData("192.168.1.50:5555", "List of devices attached\n192.168.1.50:5555\toffline\n", false)]
    [InlineData(null, "List of devices attached\n192.168.1.50:5555\tdevice\n", true)]
    [InlineData(null, "List of devices attached\n* daemon started successfully\n", false)]
    [InlineData("ABC123", "List of devices attached\nABC123\tdevice\n", true)]
    public void DeviceListParsingMatchesTheConfiguredId(string? deviceId, string output, bool expected)
    {
        Assert.Equal(expected, AdbClient.DeviceListContains(output, deviceId));
    }

    [Fact]
    public void ANonZeroExitWithNoOutputIsReportedAsAnError()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd)) return;

        using var client = new AdbClient(cmd, "");
        var output = client.Run(["/c", "exit", "3"]);

        Assert.Equal("", output);
        Assert.Contains("code 3", client.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APipelineThatFindsNothingIsNotAnError()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd)) return;

        using var client = new AdbClient(cmd, "");
        var output = client.Run(["/c", "echo", "hello", "|", "findstr", "zzz"]);

        Assert.Equal("", output);
        Assert.Equal("", client.LastError);
    }
}
