using System;
using System.IO;
using System.Text.Json.Nodes;
using Ultron.Services;
using Ultron.Services.Tools.Phone;
using Xunit;

namespace Ultron.Tests;

public sealed class PhonePushGuardTests : IDisposable
{
    private readonly string _root;
    private readonly UserProfilePathPolicy _policy;

    public PhonePushGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ultron-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _policy = new UserProfilePathPolicy(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private JsonObject Args(string local) => new() { ["local"] = local };

    [Fact]
    public void AFileInsideTheRootIsRewrittenToItsCanonicalPath()
    {
        var file = Path.Combine(_root, "note.txt");
        File.WriteAllText(file, "hi");
        var args = Args(Path.Combine(_root, ".", "note.txt"));

        var result = PhonePushGuard.ResolveLocal(args, _policy);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.GetFullPath(file), args["local"]!.GetValue<string>());
    }

    [Fact]
    public void AFileOutsideTheRootIsDenied()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ultron-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        try
        {
            var result = PhonePushGuard.ResolveLocal(Args(outside), _policy);

            Assert.False(result.Success);
            Assert.Equal(ToolError.Denied, result.Error);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void AMissingFileIsNotFound()
    {
        var result = PhonePushGuard.ResolveLocal(Args(Path.Combine(_root, "missing.txt")), _policy);

        Assert.False(result.Success);
        Assert.Equal(ToolError.NotFound, result.Error);
    }

    [Fact]
    public void NonObjectArgumentsAreInvalid()
    {
        var result = PhonePushGuard.ResolveLocal(null, _policy);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }
}
