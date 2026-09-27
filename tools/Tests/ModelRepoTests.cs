using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// Model install is the one place the app trusts bytes it did not build, so these
/// tests cover verification succeeding, failing, and failing without destroying a
/// good download.
/// </summary>
public sealed class ModelRepoTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "ultron-models-" + Guid.NewGuid().ToString("N"));

    public ModelRepoTests() => Directory.CreateDirectory(_base);

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Serves a fixed payload once over loopback. Avoids HttpListener URL ACLs.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _body;
        public int Port { get; }
        public string Url { get; }

        public OneShotServer(byte[] body)
        {
            _body = body;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{Port}/model.bin";
            _ = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(_body);
                await stream.FlushAsync();
            }
            catch { }
        }

        public void Dispose() { try { _listener.Stop(); } catch { } }
    }

    private ModelRepo Repo(string url, string expectedSha, long expectedSize) =>
        new ModelRepo(_base, new Dictionary<string, ModelRepo.ModelInfo>
        {
            ["model.bin"] = new ModelRepo.ModelInfo(url, expectedSha, expectedSize),
        });

    [Fact]
    public async Task DownloadsAndVerifiesAModel()
    {
        var payload = Encoding.UTF8.GetBytes("a real model would go here");
        using var server = new OneShotServer(payload);
        var repo = Repo(server.Url, Sha256(payload), payload.Length);

        var path = await repo.EnsureAsync("model.bin");

        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        // The temp file must be promoted, not left behind.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task RejectsAModelWhoseDigestDoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("tampered bytes");
        using var server = new OneShotServer(payload);
        var repo = Repo(server.Url, Sha256(Encoding.UTF8.GetBytes("something else")), payload.Length);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => repo.EnsureAsync("model.bin"));

        // A model that failed verification must never be promoted into place.
        Assert.False(File.Exists(repo.PathFor("model.bin")));
    }

    [Fact]
    public async Task RejectsAModelWhoseSizeDoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("right hash, wrong length");
        using var server = new OneShotServer(payload);
        var repo = Repo(server.Url, Sha256(payload), payload.Length + 1);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => repo.EnsureAsync("model.bin"));
        Assert.False(File.Exists(repo.PathFor("model.bin")));
    }

    [Fact]
    public async Task QuarantineInsteadOfDeleteWhenAnExistingModelDoesNotMatch()
    {
        // Regression guard: deleting here meant a wrong expected digest threw away
        // a valid download and re-fetched it on every launch.
        var payload = Encoding.UTF8.GetBytes("the good model");
        var repo = Repo("http://127.0.0.1:1/never", Sha256(payload), payload.Length);
        await File.WriteAllBytesAsync(repo.PathFor("model.bin"), Encoding.UTF8.GetBytes("corrupt"));

        await Assert.ThrowsAnyAsync<Exception>(() => repo.EnsureAsync("model.bin"));

        // The bad bytes are preserved for inspection rather than destroyed.
        Assert.True(File.Exists(repo.PathFor("model.bin") + ".corrupt"));
        Assert.Equal("corrupt", await File.ReadAllTextAsync(repo.PathFor("model.bin") + ".corrupt"));
    }

    [Fact]
    public async Task KeepsAValidExistingModelWithoutDownloading()
    {
        var payload = Encoding.UTF8.GetBytes("already good");
        // Port 1 is never listening: a download attempt would fail.
        var repo = Repo("http://127.0.0.1:1/never", Sha256(payload), payload.Length);
        await File.WriteAllBytesAsync(repo.PathFor("model.bin"), payload);

        var path = await repo.EnsureAsync("model.bin");

        Assert.Equal(repo.PathFor("model.bin"), path);
        Assert.False(File.Exists(repo.PathFor("model.bin") + ".corrupt"));
    }

    [Fact]
    public void CatalogDigestsAreWellFormed()
    {
        // Guards against a placeholder ever being committed again: a real SHA-256
        // is 64 hex chars and a plausible size, not a round number.
        var field = typeof(ModelRepo).GetField("Catalog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var catalog = (IReadOnlyDictionary<string, ModelRepo.ModelInfo>)field!.GetValue(null)!;

        var seen = 0;
        foreach (var (key, model) in catalog)
        {
            Assert.Matches("^[0-9a-f]{64}$", model.ExpectedSha256);
            Assert.True(model.ExpectedSize > 100_000, $"{key} has an implausible size: {model.ExpectedSize}");
            // A branch reference would make a correct digest fail after any upstream commit.
            Assert.DoesNotContain("/resolve/main/", model.Url);
            Assert.DoesNotContain("/master/", model.Url);
            seen++;
        }
        Assert.Equal(4, seen);
    }
}
