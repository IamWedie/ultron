using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

public class MemoryStoreTests
{
    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"ultron_test_{Guid.NewGuid():N}.db");

    private static void Cleanup(string db)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                File.Delete(db);
                File.Delete(db + "-wal");
                File.Delete(db + "-shm");
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public async Task LogAndRecent_RoundTripsInOrder()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.LogAsync("user", "first");
            await store.LogAsync("assistant", "second");
            await store.LogAsync("user", "third");
            store.Dispose();

            var store2 = new MemoryStore(db);
            var rows = await store2.RecentConversationsAsync(10);
            store2.Dispose();

            Assert.Equal(3, rows.Count);
            Assert.Equal("user", rows[0].Role);
            Assert.Equal("first", rows[0].Text);
            Assert.Equal("assistant", rows[1].Role);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task SearchConversations_FindsSubstring()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.LogAsync("user", "remember the code 42 guys");
            await store.LogAsync("user", "totally unrelated");
            var hits = await store.SearchConversationsAsync("42 guys", 10);
            store.Dispose();
            Assert.Single(hits);
            Assert.Contains("42 guys", hits[0].Text);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task AddAndListFacts_RoundTrips()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.AddFactAsync("user prefers dark mode");
            var facts = await store.ListFactsAsync();
            store.Dispose();
            Assert.Single(facts);
            Assert.Contains("dark mode", facts[0]);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task PurgeOlderThan_RemovesOldKeepsNew()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.LogAsync("user", "old entry");
            // Now() is second-precision (ts strings). A ≥1.2s wait plus a tiny
            // span guarantees the truncation of (now - span) lands in a strictly
            // later second than the insert, so the DELETE condition always fires.
            await Task.Delay(1500);
            await store.PurgeOlderThanAsync(TimeSpan.FromMilliseconds(20));
            var rows = await store.RecentConversationsAsync(10);
            store.Dispose();
            Assert.Empty(rows);
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task PurgeAll_EmptiesEverything()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.LogAsync("user", "x");
            await store.AddFactAsync("fact");
            await store.PurgeAllAsync();
            Assert.Empty(await store.RecentConversationsAsync(10));
            Assert.Empty(await store.ListFactsAsync());
            store.Dispose();
        }
        finally { Cleanup(db); }
    }

    [Fact]
    public async Task RedactSecrets_BeforeStore()
    {
        var db = TempDb();
        try
        {
            var store = new MemoryStore(db);
            await store.LogAsync("user", "my key is AIzaSyA1234567890abcdefgh_ijklmnopqrstuv keep");
            var rows = await store.RecentConversationsAsync(10);
            store.Dispose();
            Assert.Single(rows);
            Assert.DoesNotContain("AIzaSyA1234567890abcdefgh_ijklmnopqrstuv", rows[0].Text);
            Assert.Contains("***REDACTED***", rows[0].Text);
        }
        finally { Cleanup(db); }
    }
}