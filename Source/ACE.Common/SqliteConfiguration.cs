namespace ACE.Common
{
    /// <summary>
    /// Which relational backend ACE should use.
    /// </summary>
    public enum DatabaseProvider
    {
        /// <summary>MySQL / MariaDB via Pomelo. The original, fully-supported backend.</summary>
        MySql = 0,

        /// <summary>SQLite via Microsoft.Data.Sqlite. Intended for local development.</summary>
        Sqlite = 1
    }

    /// <summary>
    /// Selects the active database backend. Kept separate from the per-database
    /// settings so that a single flag switches all three databases at once.
    /// </summary>
    public class DatabaseProviderConfiguration
    {
        /// <summary>
        /// "mysql" (default) or "sqlite". Parsed case-insensitively; an
        /// unrecognised value falls back to <see cref="DatabaseProvider.MySql"/>.
        /// </summary>
        public string Provider { get; set; } = "mysql";

        /// <summary>
        /// On startup, if the SQLite database files are missing, create them and
        /// populate the world database. Set to false to manage the files yourself.
        /// </summary>
        public bool AutoCreate { get; set; } = true;

        /// <summary>
        /// Where to fetch the pre-converted SQLite world database from when the
        /// local world file is missing. This is the same artifact that backs
        /// https://acedb.treestats.net, produced by amoeba/ace-to-sqlite.
        /// </summary>
        public string WorldDatabaseUrl { get; set; } = "https://github.com/amoeba/ace-to-sqlite/releases/download/latest/ace_world_patches.db";

        public DatabaseProvider Resolve()
        {
            if (string.Equals(Provider, "sqlite", System.StringComparison.OrdinalIgnoreCase))
                return DatabaseProvider.Sqlite;

            return DatabaseProvider.MySql;
        }
    }

    /// <summary>
    /// Settings for the SQLite backend: the three per-database file paths and the
    /// connection-level pragmas applied to every connection.
    /// </summary>
    public class SqliteConfiguration
    {
        public SqliteDatabaseConfiguration Authentication { get; set; } = new SqliteDatabaseConfiguration()
        {
            Database = "db/ace_auth.db"
        };

        public SqliteDatabaseConfiguration Shard { get; set; } = new SqliteDatabaseConfiguration()
        {
            Database = "db/ace_shard.db"
        };

        public SqliteDatabaseConfiguration World { get; set; } = new SqliteDatabaseConfiguration()
        {
            Database = "db/ace_world.db"
        };

        /// <summary>
        /// <c>PRAGMA synchronous</c> applied to every connection, using SQLite's
        /// numeric codes: 0=OFF, 1=NORMAL, 2=FULL, 3=EXTRA.
        /// <para>
        /// Defaults to 1 (NORMAL), which is safe against application crashes and only
        /// risks the last few transactions on OS or power failure -- the right trade
        /// for a local development database, and unchanged from the previous
        /// hardcoded behaviour. A developer on a laptop who would rather not lose
        /// writes to a closed lid can set this to 2 (FULL); it measures at +4%,
        /// which is not a real cost at development volumes.
        /// </para>
        /// </summary>
        public int Synchronous { get; set; } = 1;

        /// <summary>
        /// <c>PRAGMA cache_size</c> applied to every connection, in KiB when negative
        /// (SQLite's convention) and pages when positive.
        /// <para>
        /// Defaults to -2000 (2 MiB), which is SQLite's own default. This bounds the
        /// read working set. It is not a performance knob -- raising it measured no
        /// speedup -- so the default is about bounding worst cases, not speed.
        /// </para>
        /// </summary>
        public int CacheSize { get; set; } = -2000;

        /// <summary>
        /// <c>PRAGMA journal_size_limit</c> applied to every connection, in bytes.
        /// Bounds how far the write-ahead log can grow before a checkpoint truncates
        /// it, which matters for a long-running process.
        /// <para>
        /// Defaults to 100 MiB. Set to -1 for no limit, which is SQLite's default.
        /// </para>
        /// </summary>
        public int JournalSizeLimit { get; set; } = 104857600;

        /// <summary>
        /// The schema version stamped into each database via <c>PRAGMA user_version</c>
        /// on creation and verified on startup.
        /// <para>
        /// This records which state a given <c>db/</c> directory is in, so a reported
        /// problem can be tied to a version. A migration would bump this and drive its
        /// changes, stamping <c>user_version</c> as it goes.
        /// </para>
        /// </summary>
        public int SchemaVersion { get; set; } = 1;
    }

    public class SqliteDatabaseConfiguration
    {
        /// <summary>
        /// Path to the SQLite database file. A relative path resolves against the
        /// directory holding the executable, which is also the directory Config.js is
        /// read from -- not the working directory. Absolute paths are used as given.
        /// </summary>
        public string Database { get; set; } = "";

        public bool EnableDetailedErrors { get; set; } = false;

        public bool EnableSensitiveDataLogging { get; set; } = false;

        /// <summary>
        /// Microsoft.Data.Sqlite connection-string fragment appended to the generated
        /// connection string. Settable so an operator can override it without a
        /// rebuild; the default is what the backend is validated against.
        /// <para>
        /// <c>Pooling=True</c> is deliberate and load-bearing. An earlier revision
        /// used <c>Cache=Shared</c>, which shares one page cache between connections
        /// and therefore imposes table-level locks between them: a write on one
        /// connection stalls for the whole busy timeout and then fails with
        /// <c>SQLITE_LOCKED</c> ("database table is locked") whenever any other
        /// connection holds a read on that table. That is the reader/writer
        /// concurrency WAL exists to provide, and it is the access pattern ACE
        /// generates -- a <c>DbContext</c> per operation plus several background
        /// workers. Private cache with pooling restores it: the same write succeeds
        /// immediately, and a genuine writer collision surfaces as the retryable
        /// <c>SQLITE_BUSY</c> instead. See SQLITE_PRODUCTION.md 3.2.
        /// </para>
        /// <para>
        /// <c>Default Timeout</c> is the busy timeout in seconds, matching the
        /// <c>busy_timeout</c> that <see cref="ACE.Database.SqlitePragmaInterceptor"/>
        /// also applies and verifies.
        /// </para>
        /// </summary>
        public string ConnectionOptions { get; set; } = "Pooling=True;Foreign Keys=True;Default Timeout=30";
    }
}
