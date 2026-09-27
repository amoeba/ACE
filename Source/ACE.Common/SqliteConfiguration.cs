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
    /// Per-database settings for the SQLite backend. Each entry is a file path.
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
    }

    public class SqliteDatabaseConfiguration
    {
        /// <summary>
        /// Path to the SQLite database file. Relative paths resolve against the
        /// ACE.Server working directory.
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
