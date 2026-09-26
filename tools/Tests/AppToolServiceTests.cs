using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services;
using Ultron.Services.Apps;
using Ultron.Services.Processes;
using Ultron.Services.Tools;
using Ultron.Services.Tools.Apps;
using Xunit;

namespace Ultron.Tests;

public sealed class AppToolServiceTests
{
    private sealed class FakeResolver : IAppResolver
    {
        private readonly Dictionary<string, string> _paths;
        public FakeResolver(Dictionary<string, string> paths) => _paths = paths;
        public List<string> Lookups { get; } = new();
        public Task<string?> ResolveAsync(string appName)
        {
            Lookups.Add(appName);
            return Task.FromResult(_paths.TryGetValue(appName, out var path) ? path : null);
        }
        public void Invalidate() { }
    }

    private sealed class FakeProcess : IProcessHandle
    {
        public int ProcessId { get; set; } = 4242;
        public DateTime StartedUtc { get; set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public bool IsRunning { get; set; } = true;
        public bool Killed { get; private set; }
        public void KillTree() { Killed = true; IsRunning = false; }
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public FakeProcess? Process { get; set; } = new();
        public List<FakeProcess> ByName { get; } = new();
        public string? StartedPath { get; private set; }
        public IProcessHandle? Start(string executable)
        {
            StartedPath = executable;
            return Process;
        }
        public IReadOnlyList<IProcessHandle> FindByImageName(string imageName) => ByName.Cast<IProcessHandle>().ToList();
    }

    private sealed class FakeActivator : ILaunchedWindowActivator
    {
        public IntPtr Handle { get; set; } = new(0x1234);
        public TimeSpan Timeout { get; private set; }
        public string? ActivatedImage { get; private set; }
        public Task<IntPtr> ActivateAsync(string imageName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ActivatedImage = imageName;
            Timeout = timeout;
            return Task.FromResult(Handle);
        }
    }

    private static readonly Dictionary<string, string> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = @"C:\Windows\System32\notepad.exe",
    };

    private static AppToolService Build(
        out FakeResolver resolver, out FakeLauncher launcher, out FakeActivator activator, out UndoLedger undo)
    {
        resolver = new FakeResolver(new Dictionary<string, string>(Catalog));
        launcher = new FakeLauncher();
        activator = new FakeActivator();
        undo = new UndoLedger();
        return new AppToolService(resolver, launcher, activator, undo);
    }

    private static ToolCall Call(string action, object? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["action"] = action };
        if (extra is not null)
            foreach (var p in System.Text.Json.JsonSerializer.SerializeToElement(extra).EnumerateObject())
                payload[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.Value.GetString()
                    : p.Value.GetRawText();
        using var doc = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(payload));
        return new ToolCall("c1", AppToolService.ToolName, doc.RootElement);
    }

    [Fact]
    public async Task OpensAKnownAppAndActivatesItsWindow()
    {
        var service = Build(out _, out var launcher, out var activator, out _);

        var result = await service.ExecuteAsync(Call("open", new { app_name = "notepad" }), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", launcher.StartedPath);
        // The activator is what makes "open X then type into X" work.
        Assert.Equal("notepad", activator.ActivatedImage);
        Assert.True(result.Data!.Value.GetProperty("window_focused").GetBoolean());
    }

    [Fact]
    public async Task RegistersAnUndoThatClosesTheLaunchedProcess()
    {
        var service = Build(out _, out var launcher, out _, out var undo);
        var process = new FakeProcess { ProcessId = 777 };
        launcher.Process = process;

        await service.ExecuteAsync(Call("open", new { app_name = "notepad" }), CancellationToken.None);

        var description = undo.Undo();

        Assert.Contains("open_app(notepad)", description);
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task UndoRefusesToKillProcessesThatPredateTheLaunch()
    {
        // The dangerous regression: "undo open" must never terminate a window the
        // user opened themselves.
        var service = Build(out _, out var launcher, out _, out var undo);
        var launched = new FakeProcess { StartedUtc = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        launcher.Process = launched;
        var usersOwn = new FakeProcess
        {
            ProcessId = 999,
            StartedUtc = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        launcher.ByName.Add(usersOwn);

        await service.ExecuteAsync(Call("open", new { app_name = "notepad" }), CancellationToken.None);
        // The launched process has already exited, so the name-based fallback runs.
        launched.IsRunning = false;
        var result = undo.Undo();

        Assert.False(usersOwn.Killed);
        Assert.Contains("already closed or was not launched by ULTRON", result);
    }

    [Fact]
    public async Task UndoSkipsTheNameFallbackWhenTheStartTimeIsUnknown()
    {
        // A dead handle reports no start time; without a threshold we could not
        // tell the target from a sibling, so we must kill nothing.
        var service = Build(out _, out var launcher, out _, out var undo);
        var launched = new FakeProcess { StartedUtc = DateTime.MinValue, IsRunning = false };
        launcher.Process = launched;
        var sibling = new FakeProcess { StartedUtc = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc) };
        launcher.ByName.Add(sibling);

        await service.ExecuteAsync(Call("open", new { app_name = "notepad" }), CancellationToken.None);
        undo.Undo();

        Assert.False(sibling.Killed);
    }

    [Fact]
    public async Task RejectsAPathInsteadOfRunningIt()
    {
        var service = Build(out _, out var launcher, out _, out _);

        var result = await service.ExecuteAsync(
            Call("open", new { app_name = @"C:\Windows\System32\cmd.exe" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
        Assert.Null(launcher.StartedPath);
    }

    [Fact]
    public async Task UnknownAppIsNotFoundAndNeverLaunched()
    {
        var service = Build(out _, out var launcher, out _, out var undo);

        var result = await service.ExecuteAsync(Call("open", new { app_name = "definitely-not-installed" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.NotFound, result.Error);
        Assert.Null(launcher.StartedPath);
        Assert.Equal(0, undo.Count);
    }

    [Fact]
    public async Task UnknownActionIsRejected()
    {
        var service = Build(out _, out _, out _, out _);

        var result = await service.ExecuteAsync(
            Call("self_destruct", new { app_name = "notepad" }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }

    [Fact]
    public async Task MissingAppNameIsInvalidArguments()
    {
        var service = Build(out _, out _, out _, out _);

        var result = await service.ExecuteAsync(Call("open", new { }), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ToolError.InvalidArguments, result.Error);
    }
}

public sealed class AppResolverTests
{
    [Fact]
    public void NormalizeRejectsPathLikeNames()
    {
        Assert.Equal(string.Empty, AppResolver.Normalize(@"C:\Windows\notepad.exe"));
        Assert.Equal("notepad", AppResolver.Normalize("  notepad.  "));
    }

    [Fact]
    public async Task FindsAnExecutableInTheInstallIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "ultron-apps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "WidgetCo"));
        var exe = Path.Combine(root, "WidgetCo", "widget.exe");
        File.WriteAllText(exe, "x");
        try
        {
            var resolver = new AppResolver(
                startMenuDirectories: Array.Empty<string>(),
                installRoots: new[] { root },
                useRegistry: false);

            var found = await resolver.ResolveAsync("widget");

            Assert.Equal(exe, found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownNameResolvesToNullWithoutThrowing()
    {
        var resolver = new AppResolver(
            startMenuDirectories: Array.Empty<string>(),
            installRoots: Array.Empty<string>(),
            useRegistry: false);

        Assert.Null(await resolver.ResolveAsync("no-such-app-xyz"));
        Assert.Null(await resolver.ResolveAsync("   "));
    }
}
