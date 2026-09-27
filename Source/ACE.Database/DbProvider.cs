using System;
using System.IO;

using log4net;

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
        private static readonly ILog log = LogManager.GetLogger(typeof(DbProvider));

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
        /// The directory that relative SQLite database paths resolve against.
        /// </summary>
        /// <remarks>
        /// Internal and settable only so a test can observe the wiring. Under the test
        /// host <c>Environment.CurrentDirectory</c> and
        /// <see cref="AppContext.BaseDirectory"/> are the same path, so a test that
        /// only calls <see cref="ResolveSqlitePath(DatabaseKind)"/> cannot tell which
        /// of the two the production path used -- and choosing wrongly is precisely
        /// the defect this indirection exists to make detectable. Overriding it is
        /// what lets the regression test fail.
        /// </remarks>
        internal static string SqliteBaseDirectory { get; set; } = AppContext.BaseDirectory;

        /// <summary>
        /// Resolves the SQLite file path for a database. A relative configured value
        /// resolves against <see cref="SqliteBaseDirectory"/> -- the directory holding
        /// the executable -- and not against the working directory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The base is where ACE reads <c>Config.js</c> from: Program.cs builds
        /// <c>Path.Combine(exeLocation, "Config.js")</c>, so a relative database path
        /// means what a relative path means everywhere else in that file -- relative
        /// to the config that names it.
        /// </para>
        /// <para>
        /// This used to be <c>Path.GetFullPath(configured)</c>, which resolves a
        /// relative path against <c>Environment.CurrentDirectory</c>. The databases
        /// were therefore created somewhere other than the config that named them
        /// whenever the two directories differed. The difference is invisible in the
        /// two common cases, which is why it survived: Docker runs
        /// <c>dotnet ACE.Server.dll</c> with <c>WORKDIR /ace</c>, and <c>dotnet run</c>
        /// from the project directory sets the working directory to the output
        /// directory. It diverges under a service manager, a scheduled task, or a
        /// shell that has <c>cd</c>'d elsewhere -- and the databases then turn up in
        /// the current directory with nothing to say why.
        /// </para>
        /// <para>
        /// The project has already paid for this class of bug once, on the MySQL
        /// update scripts: issue #3886, and
        /// <c>MySqlDatabaseUpdateProvider.PatchDatabase</c>, which falls back to the
        /// executing assembly's location for the same reason.
        /// </para>
        /// <para>
        /// A rooted configured value is still returned verbatim, so a deployment that
        /// deliberately points the databases elsewhere is unaffected.
        /// </para>
        /// </remarks>
        public static string ResolveSqlitePath(DatabaseKind kind)
        {
            var baseDirectory = SqliteBaseDirectory;

            WarnIfWorkingDirectoryDiffers(baseDirectory);

            return ResolveSqlitePath(ConfiguredSqlitePath(kind), baseDirectory);
        }

        /// <summary>
        /// The configured path for a database, with the default applied when the
        /// configuration does not supply one.
        /// </summary>
        /// <remarks>
        /// Separated from the resolution so the rule below can be exercised without a
        /// config file, and so a test can supply a known configured value and observe
        /// what it turns into.
        /// </remarks>
        internal static string ConfiguredSqlitePath(DatabaseKind kind)
        {
            var configured = kind switch
            {
                DatabaseKind.Authentication => Config.Sqlite.Authentication.Database,
                DatabaseKind.Shard         => Config.Sqlite.Shard.Database,
                DatabaseKind.World         => Config.Sqlite.World.Database,
                _                          => throw new ArgumentOutOfRangeException(nameof(kind))
            };

            return string.IsNullOrWhiteSpace(configured)
                ? $"db/ace_{kind.ToString().ToLowerInvariant()}.db"
                : configured;
        }

        /// <summary>
        /// The resolution rule on its own: a rooted path is used exactly as given, a
        /// relative one is taken relative to <paramref name="baseDirectory"/>.
        /// </summary>
        /// <remarks>
        /// This never consults the working directory. That is the whole point, and it
        /// is asserted directly in the tests rather than inferred from the public
        /// overload, which cannot distinguish the two under the test host.
        /// </remarks>
        internal static string ResolveSqlitePath(string configured, string baseDirectory)
            => Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(configured, baseDirectory);

        /// <summary>
        /// Names both directories once, when they differ, so an operator whose
        /// databases used to appear somewhere else can read why from one log line
        /// instead of inferring it.
        /// </summary>
        /// <remarks>
        /// Nothing depends on the working directory any more, so this is not a
        /// warning about behaviour -- it is a breadcrumb. Emitted at most once per
        /// process, because the answer cannot change while the process runs.
        /// </remarks>
        private static bool warnedAboutWorkingDirectory;

        private static void WarnIfWorkingDirectoryDiffers(string baseDirectory)
        {
            if (warnedAboutWorkingDirectory)
                return;

            warnedAboutWorkingDirectory = true;

            var workingDirectory = Directory.GetCurrentDirectory();

            if (!SameDirectory(workingDirectory, baseDirectory))
                log.Warn($"The working directory '{workingDirectory}' is not the directory that relative database " +
                         $"paths resolve against, '{baseDirectory}'. Relative Sqlite paths are taken from the " +
                         "directory holding Config.js and the executable; set an absolute path in the Sqlite " +
                         "configuration to place the database files elsewhere.");
        }

        /// <summary>
        /// Compares two directories as paths rather than as strings, so a trailing
        /// separator or a redundant segment does not read as a difference. Case is
        /// ignored only where the platform's filesystem ignores it.
        /// </summary>
        private static bool SameDirectory(string left, string right)
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
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
