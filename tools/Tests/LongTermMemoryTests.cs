using Ultron.Services;
using Xunit;

namespace Ultron.Tests;

/// <summary>
/// long_term.json is the single authority for everything ULTRON remembers. These
/// tests cover the properties that were previously missing and silently cost the
/// user data: atomic writes, refusing to destroy a corrupt store, real upsert
/// semantics, and the ranked search the model tool depends on.
/// </summary>
public sealed class LongTermMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ultron-ltm-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public LongTermMemoryTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "long_term.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    private LongTermMemory Store(bool redact = true) => new(_path, redact);

    private void Write(string json) => File.WriteAllText(_path, json);

    [Fact]
    public void Upsert_RoundTripsCategoryKeyValue()
    {
        var m = Store();
        m.Upsert("preferences", "favorite_color", "blue");
        var fact = Assert.Single(m.Entries());
        Assert.Equal("preferences", fact.Category);
        Assert.Equal("favorite_color", fact.Key);
        Assert.Equal("blue", fact.Value);
        Assert.False(string.IsNullOrEmpty(fact.Updated));
    }

    [Fact]
    public void Upsert_SameCategoryAndKey_ReplacesInsteadOfDuplicating()
    {
        // The retired SQLite fact log was append-only, so a second save_memory
        // for the same key produced two rows and recall returned both. This is the
        // regression that made the store look like it had forgotten an update.
        var m = Store();
        m.Upsert("preferences", "favorite_color", "blue");
        m.Upsert("preferences", "favorite_color", "green");
        var fact = Assert.Single(m.Entries());
        Assert.Equal("green", fact.Value);
    }

    [Fact]
    public void Upsert_IsCaseInsensitiveOnCategoryAndKey()
    {
        var m = Store();
        m.Upsert("Preferences", "Favorite_Color", "blue");
        m.Upsert("preferences", "favorite_color", "green");
        Assert.Single(m.Entries());
    }

    [Fact]
    public void Upsert_PersistsAcrossInstances()
    {
        Store().Upsert("identity", "name", "Wadia");
        var reopened = Store();
        Assert.Equal("Wadia", Assert.Single(reopened.Entries()).Value);
    }

    [Fact]
    public void Upsert_ClampsOverlongFields()
    {
        var m = Store();
        m.Upsert(new string('c', LongTermMemory.MaxCategoryLength + 50),
                 new string('k', LongTermMemory.MaxKeyLength + 50),
                 new string('v', LongTermMemory.MaxValueLength + 50));
        var fact = Assert.Single(m.Entries());
        Assert.Equal(LongTermMemory.MaxCategoryLength, fact.Category.Length);
        Assert.Equal(LongTermMemory.MaxKeyLength, fact.Key.Length);
        Assert.Equal(LongTermMemory.MaxValueLength, fact.Value.Length);
    }

    [Fact]
    public void Upsert_RedactsSecretsBeforeTheyReachDisk()
    {
        Store().Upsert("notes", "key", "AIzaSyA1234567890abcdefgh_ijklmnopqrstuv");
        Assert.DoesNotContain("AIzaSyA1234567890abcdefgh_ijklmnopqrstuv", File.ReadAllText(_path));
    }

    [Fact]
    public void Upsert_DefaultsMissingCategoryToNotes()
    {
        var m = Store();
        m.Upsert("", "k", "v");
        Assert.Equal("notes", Assert.Single(m.Entries()).Category);
    }

    [Fact]
    public void Delete_RemovesEntryAndEmptyCategory()
    {
        var m = Store();
        m.Upsert("preferences", "a", "1");
        m.Upsert("preferences", "b", "2");

        Assert.True(m.Delete("preferences", "a"));
        Assert.Equal("b", Assert.Single(m.Entries()).Key);

        Assert.True(m.Delete("preferences", "b"));
        Assert.Empty(m.Entries());
        Assert.False(m.Delete("preferences", "b"));
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var m = Store();
        m.Upsert("a", "k1", "v");
        m.Upsert("b", "k2", "v");
        m.Clear();
        Assert.Empty(m.Entries());
        Assert.Equal(0, Store().Count());
    }

    [Fact]
    public void Recall_RanksKeyHitsAboveValueHits()
    {
        // Scores follow the scorer the Python side used to own: whole-query match
        // on the key is worth more than a whole-query match in the value.
        var m = Store();
        m.Upsert("preferences", "editor", "I use neovim");
        m.Upsert("preferences", "shell", "I use fish as my editor");
        var hits = m.Recall("editor");
        Assert.Equal(2, hits.Count);
        Assert.Equal("editor", hits[0].Key);
    }

    [Fact]
    public void Recall_MatchesPartialWords()
    {
        var m = Store();
        m.Upsert("projects", "ultron_rewrite", "port to winui");
        var hit = Assert.Single(m.Recall("rewrite"));
        Assert.Equal("ultron_rewrite", hit.Key);
    }

    [Fact]
    public void Recall_ReturnsNothingForEmptyOrUnmatchedQuery()
    {
        var m = Store();
        m.Upsert("preferences", "editor", "neovim");
        Assert.Empty(m.Recall(""));
        Assert.Empty(m.Recall("helicopter"));
    }

    [Fact]
    public void Recall_HonoursLimit()
    {
        var m = Store();
        for (var i = 0; i < 20; i++) m.Upsert("notes", "note" + i, "value");
        Assert.Equal(3, m.Recall("value", 3).Count);
    }

    [Fact]
    public void FormatForPrompt_IncludesUserDefinedCategories()
    {
        // The old Python formatter only walked six hardcoded category names, so
        // anything the user typed into the panel was stored but invisible to the
        // model. Any category must now appear.
        var m = Store();
        m.Upsert("preferences", "editor", "neovim");
        m.Upsert("gym", "routine", "mon/wed/fri");
        var block = m.FormatForPrompt();
        Assert.Contains("neovim", block);
        Assert.Contains("gym:", block);
        Assert.Contains("mon/wed/fri", block);
    }

    [Fact]
    public void FormatForPrompt_OrdersPreferredCategoriesFirst()
    {
        var m = Store();
        m.Upsert("notes", "n", "1");
        m.Upsert("identity", "name", "Wadia");
        var block = m.FormatForPrompt();
        Assert.True(block.IndexOf("identity:") < block.IndexOf("notes:"));
    }

    [Fact]
    public void FormatForPrompt_TruncatesLongValues()
    {
        var m = Store();
        m.Upsert("notes", "k", new string('x', LongTermMemory.MaxValueLength));
        var block = m.FormatForPrompt();
        Assert.Contains('…', block);
        Assert.True(block.Length < LongTermMemory.MaxValueLength + 200);
    }

    [Fact]
    public void FormatForPrompt_IsEmptyWhenNothingStored()
    {
        Assert.Equal("", Store().FormatForPrompt());
    }

    [Fact]
    public void CorruptFile_IsReportedAndNeverOverwritten()
    {
        // Regression: the old panel wrote with a bare File.WriteAllText, so the
        // first Add/Edit/Delete after a corruption silently replaced the file
        // with {}. Nothing may be written until the user explicitly resets.
        Write("{ this is not json");
        var m = Store();
        Assert.True(m.IsCorrupt);
        Assert.Empty(m.Entries());

        Assert.Throws<InvalidOperationException>(() => m.Upsert("notes", "k", "v"));
        Assert.Throws<InvalidOperationException>(() => m.Delete("notes", "k"));
        Assert.Throws<InvalidOperationException>(() => m.Clear());
        Assert.Throws<InvalidOperationException>(() => m.Import([new MemoryFact("n", "k", "v", "")]));

        Assert.Equal("{ this is not json", File.ReadAllText(_path));
    }

    [Fact]
    public void DiscardAndReset_QuarantinesTheUnreadableFile()
    {
        Write("{ broken");
        var m = Store();
        m.DiscardAndReset();
        Assert.False(m.IsCorrupt);

        m.Upsert("notes", "k", "v");
        Assert.Equal("v", Assert.Single(m.Entries()).Value);

        var backups = Directory.GetFiles(_dir, "long_term.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal("{ broken", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void ReadsLegacyBareStringValues()
    {
        // Older files stored a bare string instead of {value, updated}.
        Write("""{"preferences":{"editor":"neovim"}}""");
        var fact = Assert.Single(Store().Entries());
        Assert.Equal("neovim", fact.Value);
    }

    [Fact]
    public void ReadsFilesMissingTheUpdatedField()
    {
        Write("""{"notes":{"k":{"value":"v"}}}""");
        Assert.Equal("v", Assert.Single(Store().Entries()).Value);
    }

    [Fact]
    public void NonObjectCategoryIsSkippedRatherThanThrowing()
    {
        Write("""{"good":{"k":{"value":"v"}},"bad":"not an object"}""");
        var fact = Assert.Single(Store().Entries());
        Assert.Equal("k", fact.Key);
    }

    [Fact]
    public void Import_MergesInOneWrite()
    {
        var m = Store();
        var n = m.Import([
            new MemoryFact("identity", "name", "Wadia", ""),
            new MemoryFact("preferences", "editor", "neovim", "2026-01-01"),
        ]);
        Assert.Equal(2, n);
        Assert.Equal(2, Store().Count());
    }

    [Fact]
    public void Import_FillsInMissingUpdatedDate()
    {
        var m = Store();
        m.Import([new MemoryFact("identity", "name", "Wadia", "")]);
        Assert.False(string.IsNullOrEmpty(Assert.Single(m.Entries()).Updated));
    }

    [Fact]
    public void Reload_PicksUpAnExternalWrite()
    {
        // The Memory panel and the model tool both mutate the same file, so a
        // cached read would show the user stale data.
        var m = Store();
        m.Upsert("notes", "k", "first");
        Write("""{"notes":{"k":{"value":"second","updated":"2026-01-01"}}}""");
        m.Reload();
        Assert.Equal("second", Assert.Single(m.Entries()).Value);
    }

    [Fact]
    public void WireFormat_UsesLowercaseKeysThePythonReaderExpects()
    {
        // Cross-language contract: backend/gemini_backend.py reads this file and
        // looks up entry["value"] literally. C#'s default serializer would write
        // "Value"/"Updated" and silently blank every fact on the next prompt build.
        Store().Upsert("preferences", "editor", "neovim");
        var json = File.ReadAllText(_path);
        Assert.Contains("\"value\"", json);
        Assert.Contains("\"updated\"", json);
        Assert.DoesNotContain("\"Value\"", json);
        Assert.DoesNotContain("\"Updated\"", json);
    }

    [Fact]
    public void WriteIsAtomic_NoTempFilesLeftBehind()
    {
        var m = Store();
        m.Upsert("notes", "k", "v");
        m.Upsert("notes", "k2", "v2");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.Single(Directory.GetFiles(_dir, "long_term.json"));
    }
}
