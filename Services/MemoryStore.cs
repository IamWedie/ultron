using Microsoft.Data.Sqlite;

namespace Ultron.Services;

public sealed record ConversationRow(string Ts, string Role, string Text, string User);

/// <summary>SQLite-backed conversation + fact memory.
///
/// Performance/safety choices:
///  - WAL journal + one re-used connection: minimal fopen/PRAGMA overhead per call.
///  - Everything is serialized through a single semaphore; prepared INSERT commands
///    are cached so the hot Log/AddFact paths allocate almost nothing per call.
///  - Multi-row mutating ops (purge/clear) run inside one explicit transaction so a
///    partial failure can never leave a torn state.
///  - Secrets (API keys, tokens) are redacted before any text is stored when
///    AppSettings.MemoryRedactSecrets is set (default true).</summary>
public sealed class MemoryStore : IDisposable
{
    private readonly string _dbPath;
    private readonly bool _redact;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private SqliteConnection? _cn;
    private SqliteCommand? _insConversation;
    private SqliteCommand? _insFact;

    public MemoryStore()
        : this(Path.Combine(AppSettings.DataDir(), "memory.db"), UltronOptions.Instance.Value.MemoryRedactSecrets)
    {
    }

    internal MemoryStore(string dbPath, bool redact = true)
    {
        _dbPath = dbPath;
        _redact = redact;
        InitSchema();
    }

    private SqliteConnection Conn()
    {
        if (_cn is not null) return _cn;
        _cn = Open();
        PrepareCommands(_cn);
        return _cn;
    }

    private void PrepareCommands(SqliteConnection conn)
    {
        _insConversation = conn.CreateCommand();
        _insConversation.CommandText =
            "INSERT INTO conversations (ts, role, text, user) VALUES ($ts, $role, $text, $user)";
        _insConversation.Parameters.Add("$ts", SqliteType.Text);
        _insConversation.Parameters.Add("$role", SqliteType.Text);
        _insConversation.Parameters.Add("$text", SqliteType.Text);
        _insConversation.Parameters.Add("$user", SqliteType.Text);
        _insConversation.Prepare();

        _insFact = conn.CreateCommand();
        _insFact.CommandText =
            "INSERT INTO facts (ts, topic, fact, user) VALUES ($ts, $topic, $fact, $user)";
        _insFact.Parameters.Add("$ts", SqliteType.Text);
        _insFact.Parameters.Add("$topic", SqliteType.Text);
        _insFact.Parameters.Add("$fact", SqliteType.Text);
        _insFact.Parameters.Add("$user", SqliteType.Text);
        _insFact.Prepare();
    }

    private void InitSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                user TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS facts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                topic TEXT NOT NULL DEFAULT '',
                fact TEXT NOT NULL,
                user TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_conv_ts ON conversations(ts);
            CREATE INDEX IF NOT EXISTS idx_facts_topic ON facts(topic);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        // WAL journal mode: concurrent readers + a writer without self-locking.
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        conn.Open();
        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            pragma.ExecuteNonQuery();
        }
        catch (Exception)
        {
            // WAL may be unavailable on exotic FS; read/write still works in DELETE mode.
        }
        return conn;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private string Scrub(string text) =>
        _redact ? Secrets.Redact(text) : text;

    public async ValueTask LogAsync(string role, string text, string user = "")
    {
        text = Scrub(text.Trim());
        if (text.Length == 0) return;
        await _gate.WaitAsync();
        try
        {
            Conn();
            _insConversation!.Parameters["$ts"].Value = Now();
            _insConversation!.Parameters["$role"].Value = role;
            _insConversation!.Parameters["$text"].Value = text;
            _insConversation!.Parameters["$user"].Value = user ?? "";
            await _insConversation!.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<List<ConversationRow>> RecentConversationsAsync(int limit = 14)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT ts, role, text, user FROM conversations ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<ConversationRow>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add(new ConversationRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            rows.Reverse();
            return rows;
        }
        finally { _gate.Release(); }
    }

    public async Task<List<ConversationRow>> SearchConversationsAsync(string query, int limit = 6)
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT ts, role, text, user FROM conversations WHERE text LIKE $q ORDER BY id DESC LIMIT $n";
            cmd.Parameters.AddWithValue("$q", $"%{Scrub(query)}%");
            cmd.Parameters.AddWithValue("$n", limit);
            var rows = new List<ConversationRow>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                rows.Add(new ConversationRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            rows.Reverse();
            return rows;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask AddFactAsync(string fact, string topic = "", string user = "")
    {
        fact = Scrub(fact.Trim());
        if (fact.Length == 0) return;
        await _gate.WaitAsync();
        try
        {
            Conn();
            _insFact!.Parameters["$ts"].Value = Now();
            _insFact!.Parameters["$topic"].Value = topic ?? "";
            _insFact!.Parameters["$fact"].Value = fact;
            _insFact!.Parameters["$user"].Value = user ?? "";
            await _insFact!.ExecuteNonQueryAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task<List<string>> ListFactsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT fact FROM facts ORDER BY id";
            var facts = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) facts.Add(r.GetString(0));
            return facts;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Delete stored rows older than the given age (single transaction,
    /// so a crash cannot leave only one table partially purged).</summary>
    public async ValueTask PurgeOlderThanAsync(TimeSpan age)
    {
        var cutoff = DateTime.Now.Subtract(age).ToString("yyyy-MM-dd HH:mm:ss");
        await _gate.WaitAsync();
        try
        {
            await using var tx = Conn().BeginTransaction();
            await using var cmd = Conn().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM conversations WHERE ts < $cut; DELETE FROM facts WHERE ts < $cut;";
            cmd.Parameters.AddWithValue("$cut", cutoff);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public async ValueTask PurgeAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var tx = Conn().BeginTransaction();
            await using var cmd = Conn().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM conversations; DELETE FROM facts;";
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _insConversation?.Dispose();
            _insFact?.Dispose();
            _cn?.Dispose();
            _insConversation = null;
            _insFact = null;
            _cn = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}