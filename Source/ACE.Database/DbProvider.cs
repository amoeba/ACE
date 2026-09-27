using System;
using System.IO;

using Microsoft.EntityFrameworkCore;

using ACE.Common;

namespace ACE.Database
{
    /// <summary>
    /// Identifies which of ACE's three databases is being configured.
    /// </summary>
    public enum DatabaseKind
    {
        Authentication,
        Shard,
        World
    }

    /// <summary>
    /// Single place where the active backend is resolved and applied. All three
    /// <c>DbContext</c> subclasses call <see cref="Configure"/> from
    /// <c>OnConfiguring</c> so that switching providers is a one-line config
    /// change rather than a code change.
    /// </summary>
    public static class DbProvider
    {
        /// <summary>
        /// Why <see cref="Active"/> returned the default, when it did.
        /// <para>
        /// The default is MySQL, which is the right answer when a config file simply
        /// has no <c>Database.Provider</c> key -- every deployment that predates this
        /// feature. It is the wrong answer when no config was ever read at all, because
        /// "the operator asked for MySQL" and "nothing was configured" then look
        /// identical to every caller. A filtered test run that skips
        /// <c>ConfigManager.Initialize</c> is exactly that case, and it silently
        /// exercises the MySQL path while appearing to test whichever provider the
        /// checkout names. Exposing the distinction is what lets a test assert it.
        /// </para>
        /// <para>
        /// Computed rather than cached. It used to be a settable flag that
        /// <see cref="Active"/> updated as a side effect, so it was only truthful
        /// once something had already read <see cref="Active"/> -- and a caller that
        /// checked it first saw the initial <c>false</c> and concluded a config had
        /// been loaded when none had. That is precisely the position
        /// <c>DatabaseUpdateProviderTests.Active_MatchesTheConfiguredProvider</c> is
        /// in, and it surfaced as a NullReferenceException instead of the
        /// inconclusive result the test was written to produce.
        /// </para>
        /// </summary>
        public static bool ConfigUnavailable => ConfigManager.Config?.Database == null;

        /// <summary>
        /// The backend selected by Config.js. Defaults to MySQL so that existing
        /// deployments (and any config file predating this feature) are unaffected.
        /// </summary>
        public static DatabaseProvider Active
        {
            get
            {
                try
                {
                    // A missing Database section is the normal case for any config
                    // predating this feature, and Resolve() defaults it to MySQL, so
                    // there is nothing to special-case here.
                    return ConfigManager.Config?.Database?.Resolve() ?? DatabaseProvider.MySql;
                }
                catch
                {
                    // ConfigManager not initialised yet (design-time, tooling, tests).
                    return DatabaseProvider.MySql;
                }
            }
        }

        public static bool IsSqlite => Active == DatabaseProvider.Sqlite;

        public static bool IsMySql => Active == DatabaseProvider.MySql;

        /// <summary>
        /// Why name comparisons in this codebase are written as
        /// <c>column.ToLower() == value.ToLower()</c> rather than <c>==</c>.
        /// <para>
        /// MySQL's default collations (<c>utf8mb4_general_ci</c> and friends) are
        /// case-insensitive, so <c>WHERE class_Name = 'Orb'</c> matches a stored
        /// <c>orb</c>. SQLite's <c>=</c> on TEXT is case-sensitive, so the same
        /// query silently matches nothing. That turns "log in as Bob" into a
        /// failure and lets two characters differ only by case, neither of which
        /// can happen on MySQL.
        /// </para>
        /// <para>
        /// Folding both sides restores MySQL's semantics on SQLite and is a no-op
        /// on MySQL. The cost is that the comparison stops being sargable, so a
        /// name lookup scans instead of using the index. The alternative -- naming
        /// the columns <c>COLLATE NOCASE</c> -- is unavailable for the pre-built
        /// world database, so one mechanism everywhere beats a mix of two.
        /// </para>
        /// <para>
        /// SQLite's built-in <c>lower()</c> folds ASCII only. ACE restricts
        /// account, character and weenie names to ASCII, so this agrees with
        /// MySQL for every name the server accepts.
        /// </para>
        /// <para>
        /// This has to be spelled out at each call site rather than hidden in a
        /// helper: EF Core translates a fixed set of methods inside a
        /// <c>Where</c> predicate and rejects anything else, so a
        /// <c>NameEquals(a, b)</c> wrapper would not be translatable.
        /// </para>
        /// </summary>
        internal const string CaseInsensitiveComparisonRationale = "see DbProvider";

        /// <summary>
        /// The loaded config, or a message explaining that there isn't one.
        /// <para>
        /// <see cref="Active"/> deliberately tolerates an uninitialised
        /// <see cref="ConfigManager"/>, but the connection-string builders below it
        /// cannot: they are only reached once a provider has actually been selected,
        /// so a null config here means the process is misconfigured or is running
        /// before initialisation. That deserves to say so rather than surfacing as a
        /// <see cref="NullReferenceException"/> from whichever field happened to be
        /// dereferenced first.
        /// </para>
        /// </summary>
        private static MasterConfiguration Config
        {
            get
            {
                var config = ConfigManager.Config;

                if (config == null)
                    throw new InvalidOperationException(
                        "ConfigManager has not been initialized, so no database connection string can be built. " +
                        "Call ConfigManager.Initialize() before touching any DbContext.");

                return config;
            }
        }

        /// <summary>
        /// Resolves the SQLite file path for a database, relative to the current
        /// working directory when the configured value is relative.
        /// </summary>
        public static string ResolveSqlitePath(DatabaseKind kind)
        {
            var configured = kind switch
            {
                DatabaseKind.Authentication => Config.Sqlite.Authentication.Database,
                DatabaseKind.Shard         => Config.Sqlite.Shard.Database,
                DatabaseKind.World         => Config.Sqlite.World.Database,
                _                          => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            if (string.IsNullOrWhiteSpace(configured))
                configured = $"db/ace_{kind.ToString().ToLowerInvariant()}.db";

            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured);
        }

        public static string SqliteConnectionString(DatabaseKind kind)
        {
            var cfg = kind switch
            {
                DatabaseKind.Authentication => Config.Sqlite.Authentication,
                DatabaseKind.Shard         => Config.Sqlite.Shard,
                DatabaseKind.World         => Config.Sqlite.World,
                _                          => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            return $"Data Source={ResolveSqlitePath(kind)};{cfg.ConnectionOptions}";
        }

        public static string MySqlConnectionString(DatabaseKind kind, bool includeDatabase = true)
        {
            var cfg = kind switch
            {
                DatabaseKind.Authentication => Config.MySql.Authentication,
                DatabaseKind.Shard         => Config.MySql.Shard,
                DatabaseKind.World         => Config.MySql.World,
                _                          => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            var database = includeDatabase ? $"database={cfg.Database};" : "";

            return $"server={cfg.Host};port={cfg.Port};user={cfg.Username};password={cfg.Password};{database}{cfg.ConnectionOptions}";
        }

        /// <summary>
        /// Applies the active provider to <paramref name="optionsBuilder"/>. Mirrors the
        /// per-database diagnostics flags that each context used to set inline.
        /// </summary>
        public static void Configure(DbContextOptionsBuilder optionsBuilder, DatabaseKind kind)
        {
            if (IsSqlite)
            {
                optionsBuilder.UseSqlite(SqliteConnectionString(kind), sqlite =>
                {
                    // SQLite has no server-side retry policy. Concurrency is handled by
                    // WAL (readers don't block the writer) plus busy_timeout, applied by
                    // the interceptor below.
                });

                optionsBuilder.AddInterceptors(new SqlitePragmaInterceptor());

                var cfg = kind switch
                {
                    DatabaseKind.Authentication => Config.Sqlite.Authentication,
                    DatabaseKind.Shard         => Config.Sqlite.Shard,
                    _                          => Config.Sqlite.World
                };

                if (cfg.EnableDetailedErrors)
                    optionsBuilder.EnableDetailedErrors();

                if (cfg.EnableSensitiveDataLogging)
                    optionsBuilder.EnableSensitiveDataLogging();
            }
            else
            {
                var cfg = kind switch
                {
                    DatabaseKind.Authentication => Config.MySql.Authentication,
                    DatabaseKind.Shard         => Config.MySql.Shard,
                    _                          => Config.MySql.World
                };

                var connectionString = MySqlConnectionString(kind);

                optionsBuilder.UseMySql(connectionString, DatabaseManager.CachedServerVersionAutoDetect(cfg.Database, connectionString), builder =>
                {
                    builder.EnableRetryOnFailure(10);
                });

                if (cfg.EnableDetailedErrors)
                    optionsBuilder.EnableDetailedErrors();

                if (cfg.EnableSensitiveDataLogging)
                    optionsBuilder.EnableSensitiveDataLogging();
            }
        }
    }
}
