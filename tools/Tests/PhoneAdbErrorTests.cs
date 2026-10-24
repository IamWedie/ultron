using System;
using System.IO;
using System.Threading;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// AdbClient.Run used to swallow every failure (missing binary, timeout,
/// exception) and return "". A failure that walks and talks like an empty
/// success sends the model "done" for an operation that never happened.
/// These tests pin the failure signaling that replaces that behaviour.
/// </summary>
public class PhoneAdbErrorTests
{
    [Fact]
    public void MissingAdbBinaryReportsAnErrorRatherThanASilentEmptySuccess()
    {
        using var client = new AdbClient(Path.Combine(Path.GetTempPath(), "does-not-exist-adb.exe"), "10.0.0.5");

        var connected = client.Connect();

        Assert.False(connected);
        Assert.False(string.IsNullOrEmpty(client.LastError));
    }

    [Fact]
    public void DeviceInfoReturnsAnErrorMessageWhenNotReachable()
    {
        using var client = new AdbClient("", "10.0.0.5");
        var result = client.DeviceInfo();

        Assert.Contains("not connected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoPhoneConfiguredReturnsAnActionableMessage()
    {
        using var client = new AdbClient("", "");
        var result = client.Connected();

        Assert.Contains("PHONE_ADDR", result, StringComparison.OrdinalIgnoreCase);
    }
}