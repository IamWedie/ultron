using System.Text.Json;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// The file service is the one place a model-supplied string reaches the disk,
/// so the tests exercise real files in a sandboxed profile root.
/// </summary>
public class FileToolServiceTests : IDisposable
{
    private readonly string _root;
    private readonly UserProfilePathPolicy _policy;
    private readonly UndoLedger _undo = new();
    private readonly FileToolService _files;

    public FileToolServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ultron-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _policy = new UserProfilePathPolicy(_root);
        _files = new FileToolService(_policy, _undo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ToolCall Call(string action, object? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action };
        if (extra is not null)
            foreach (var p in JsonSerializer.SerializeToElement(extra).EnumerateObject())
                payload[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString()
                    : p.Value.GetRawText();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        return new ToolCall("c1", "file_processor", doc.RootElement);
    }

    private string Path_(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private async Task<ToolResult> RunAsync(string action, object? extra = null) =>
        await _files.ExecuteAsync(Call(action, extra), CancellationToken.None);

    [Fact]
    public void NameMatchesTheToolTheModelCalls()
    {
        Assert.Equal("file_processor", _files.Name);
    }

    [Fact]
    public async Task ReadReturnsFileContents()
    {
        var path = Path_("hello.txt");
        await File.WriteAllTextAsync(path, "hi there");

        var result = await RunAsync("read", new { file_path = path });

        Assert.True(result.Success);
        Assert.Equal("hi there", result.Message);
    }

    [Fact]
    public async Task ReadMissingFileIsNotFoundRatherThanSuccess()
    {
        var result = await RunAsync("read", new { file_path = Path_("nope.txt") });

        Assert.False(result.Success);
        Assert.Equal(ToolError.NotFound, result.Error);
    }

    [Fact]
    public async Task ReadOutsideTheProfileIsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "outside.txt");
        await File.WriteAllTextAsync(outside, "secret");

        var result = await RunAsync("read", new { file_path = outside });

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
        Assert.DoesNotContain("secret", result.Message);
    }

    [Fact]
    public async Task TraversalOutsideTheProfileIsRefused()
    {
        var result = await RunAsync("read", new { file_path = Path_("..", "..", "Windows", "win.ini") });

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task WriteCreatesTheFileAndIsUndoable()
    {
        var path = Path_("sub", "new.txt");

        var written = await RunAsync("write", new { file_path = path, content = "hello" });

        Assert.True(written.Success);
        Assert.Equal("hello", await File.ReadAllTextAsync(path));

        var undone = _undo.Undo();
        Assert.Contains("Undid write new.txt", undone);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteOverAnExistingFileRestoresItOnUndo()
    {
        var path = Path_("doc.txt");
        await File.WriteAllTextAsync(path, "original");

        await RunAsync("write", new { file_path = path, content = "replaced" });
        Assert.Equal("replaced", await File.ReadAllTextAsync(path));

        _undo.Undo();
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CopyThenUndoRemovesOnlyTheCopy()
    {
        var source = Path_("src.txt");
        var destination = Path_("dst.txt");
        await File.WriteAllTextAsync(source, "payload");

        Assert.True((await RunAsync("copy", new { file_path = source, destination })).Success);
        Assert.True(File.Exists(destination));

        _undo.Undo();
        Assert.True(File.Exists(source), "undo must not touch the original");
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task MoveThenUndoPutsTheFileBack()
    {
        var source = Path_("a.txt");
        var destination = Path_("b.txt");
        await File.WriteAllTextAsync(source, "payload");

        Assert.True((await RunAsync("move", new { file_path = source, destination })).Success);
        Assert.False(File.Exists(source));

        _undo.Undo();
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task DeleteThenUndoRestoresTheBytes()
    {
        var path = Path_("gone.txt");
        await File.WriteAllTextAsync(path, "still here");

        var deleted = await RunAsync("delete", new { file_path = path });
        Assert.True(deleted.Success);
        Assert.False(File.Exists(path));

        _undo.Undo();
        Assert.Equal("still here", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeleteDirectoryThenUndoRestoresTheTree()
    {
        var dir = Path_("folder");
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dir, "nested", "a.txt"), "deep");

        Assert.True((await RunAsync("delete", new { file_path = dir })).Success);
        Assert.False(Directory.Exists(dir));

        _undo.Undo();
        Assert.Equal("deep", await File.ReadAllTextAsync(Path.Combine(dir, "nested", "a.txt")));
    }

    [Fact]
    public async Task DeleteIsUndoableInReverseOrder()
    {
        var first = Path_("one.txt");
        var second = Path_("two.txt");
        await File.WriteAllTextAsync(first, "1");
        await File.WriteAllTextAsync(second, "2");

        await RunAsync("delete", new { file_path = first });
        await RunAsync("delete", new { file_path = second });

        Assert.Contains("two.txt", _undo.Undo());
        Assert.Contains("one.txt", _undo.Undo());
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task CopyRefusesToOverwriteAnExistingDestination()
    {
        var source = Path_("src.txt");
        var destination = Path_("exists.txt");
        await File.WriteAllTextAsync(source, "a");
        await File.WriteAllTextAsync(destination, "b");

        var result = await RunAsync("copy", new { file_path = source, destination });

        Assert.False(result.Success);
        Assert.Equal("b", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task DestinationOutsideTheProfileIsRefused()
    {
        var source = Path_("src.txt");
        await File.WriteAllTextAsync(source, "a");

        var result = await RunAsync("copy", new
        {
            file_path = source,
            destination = Path.Combine(Path.GetTempPath(), "escape.txt"),
        });

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task UnknownActionIsUnsupported()
    {
        var result = await RunAsync("exfiltrate", new { file_path = Path_("a.txt") });

        Assert.False(result.Success);
        Assert.Equal(ToolError.Unsupported, result.Error);
    }

    [Fact]
    public async Task MissingActionIsInvalidArguments()
    {
        var result = await _files.ExecuteAsync(Call("", null), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task ListShowsFolderContents()
    {
        await File.WriteAllTextAsync(Path_("listed.txt"), "x");

        var result = await RunAsync("list", new { file_path = _root });

        Assert.True(result.Success);
        Assert.Contains("listed.txt", result.Message);
    }

    [Fact]
    public async Task SearchFindsFilesByName()
    {
        await File.WriteAllTextAsync(Path_("needle.txt"), "x");

        var result = await RunAsync("search", new { file_path = _root, query = "needle" });

        Assert.True(result.Success);
        Assert.Contains("needle.txt", result.Message);
    }

    [Fact]
    public async Task SearchWithoutQueryIsInvalid()
    {
        var result = await RunAsync("search", new { });

        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task MalformedArgumentsBecomeInvalidArguments()
    {
        // The handler throws ToolArgumentException; the router is what turns that
        // into a single InvalidArguments result, so exercise the real path.
        var router = new ToolRouter(
            new IToolHandler[] { _files },
            new PolicyEngine(),
            new FakeApprovalService { Answer = true });
        using var doc = JsonDocument.Parse("""{"action":{"nested":true}}""");
        var call = new ToolCall("c1", "file_processor", doc.RootElement);

        var result = await router.ExecuteAsync(call);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task DeleteRefusesTheProfileFolderItself()
    {
        var result = await RunAsync("delete", new { file_path = _root });

        Assert.False(result.Success);
        Assert.Contains("profile folder itself", result.Message);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task DeleteIsPromptedForButReadIsNot()
    {
        // The risk ladder, verified end to end through the policy.
        var approvals = new FakeApprovalService { Answer = true };
        var router = new ToolRouter(new IToolHandler[] { _files }, new PolicyEngine(), approvals);
        await File.WriteAllTextAsync(Path_("victim.txt"), "x");

        await router.ExecuteAsync(Call("read", new { file_path = Path_("victim.txt") }));
        await router.ExecuteAsync(Call("delete", new { file_path = Path_("victim.txt") }));

        Assert.Single(approvals.Requests);
        var request = approvals.Requests[0];
        Assert.Equal("file_processor", request.Tool);
        Assert.Equal(RiskLevel.High, request.Decision.Risk);
        // The prompt must make clear it is the destructive variant, and name the target.
        Assert.Contains("victim.txt", request.Decision.Summary);
    }

    [Fact]
    public async Task BinaryFilesAreDescribedNotDumped()
    {
        var path = Path_("blob.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x00 });

        var result = await RunAsync("read", new { file_path = path });

        Assert.True(result.Success);
        Assert.Contains("Binary file", result.Message);
    }

    [Fact]
    public async Task LargeFilesAreRefusedRatherThanRead()
    {
        var path = Path_("big.bin");
        await File.WriteAllBytesAsync(path, new byte[11 * 1024 * 1024]);

        var result = await RunAsync("read", new { file_path = path });

        Assert.False(result.Success);
        Assert.Contains("10 MB", result.Message);
    }

    [Fact]
    public async Task UndoLedgerIsEmptyAfterAFailedOperation()
    {
        await RunAsync("delete", new { file_path = Path_("missing.txt") });

        Assert.Equal(0, _undo.Count);
    }

    [Fact]
    public async Task DestinationAliasesAreAllAccepted()
    {
        var source = Path_("src.txt");
        await File.WriteAllTextAsync(source, "a");

        Assert.True((await RunAsync("copy", new { file_path = source, new_path = Path_("via_new_path.txt") })).Success);
        Assert.True((await RunAsync("copy", new { file_path = source, target = Path_("via_target.txt") })).Success);
    }
}
