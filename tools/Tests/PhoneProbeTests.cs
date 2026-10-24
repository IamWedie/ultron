using System;
using System.IO;
using System.Linq;
using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// adb has to be found before a single phone command can run. The path comes
/// from three places with a defined priority: the configured Settings path, the
/// PATH entries, then well-known install locations. Discovery and missing-adb
/// messaging must be correct without needing a real device.
/// </summary>
public class PhoneProbeTests
{
    [Fact]
    public void ConfiguredPathWinsWhenItExists()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = MakeFakeExe(dir, "adb.exe");

            var found = PhoneProbe.ResolveAdb(exe, pathEnv: $"{dir}", Array.Empty<string>());

            Assert.Equal(exe, found);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ConfiguredPathIsRejectedWhenItDoesNotExist()
    {
        var dir = CreateTempDir();
        try
        {
            var phantom = Path.Combine(dir, "adb.exe");

            var found = PhoneProbe.ResolveAdb(phantom, pathEnv: $"{dir}", Array.Empty<string>());

            Assert.NotEqual(phantom, found); // falls through to PATH/common
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void PathEntriesAreSearchedInOrder()
    {
        var dir = CreateTempDir();
        try
        {
            var first = MakeFakeExe(dir, "adb.exe");
            var secondDir = CreateTempDir();
            try
            {
                var second = MakeFakeExe(secondDir, "adb.exe");

                var found = PhoneProbe.ResolveAdb("", pathEnv: $"{dir};{secondDir}", Array.Empty<string>());

                Assert.Equal(first, found);
                _ = second;
            }
            finally { Directory.Delete(secondDir, true); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CommonDirsAreTriedAfterPath()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = MakeFakeExe(dir, "adb.exe");

            var found = PhoneProbe.ResolveAdb("", pathEnv: "", new[] { dir });

            Assert.Equal(exe, found);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WindowsNamingAddsExeWhenScanningDirectories()
    {
        var dir = CreateTempDir();
        try
        {
            var exe = MakeFakeExe(dir, "adb.exe");

            var found = PhoneProbe.ResolveAdb("", pathEnv: "", new[] { dir });

            Assert.EndsWith("adb.exe", found, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(exe, found);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void MissingAdbProducesAnActionableMessage()
    {
        var message = PhoneProbe.MissingMessage();

        Assert.Contains("adb", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", message, StringComparison.Ordinal);
        Assert.Contains("platform-tools", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsEsqueCommonLocationsAreIncludedByDefault()
    {
        var defaults = PhoneProbe.DefaultCommonDirs;

        Assert.NotEmpty(defaults);
        Assert.Contains(defaults, d => d.Contains("platform-tools", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(defaults, d => d.Contains("platform-tools", StringComparison.OrdinalIgnoreCase) || d.Contains("adb", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ultron_probe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeFakeExe(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "fake");
        return path;
    }
}