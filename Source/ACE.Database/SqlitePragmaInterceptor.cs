using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;

using log4net;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

using ACE.Common;

namespace ACE.Database
{
    /// <summary>
    /// Applies the connection-level pragmas SQLite needs to behave sanely under
    /// ACE's access pattern, and fails loudly when one of them cannot be applied.
    /// </summary>
    /// <remarks>
    /// <para>Each pragma is applied and then read back, one at a time, rather than
    /// being sent as a batch. A batch hides its own failure: if the first statement
    /// throws, nothing records which pragmas did or did not take effect, and the
    /// connection carries on in whatever state the first failure left behind.</para>
    /// <para>The read back is not redundant. A pragma's SET form does not reliably
    /// return the resulting value -- <c>journal_mode</c> and <c>busy_timeout</c> do,
    /// but <c>synchronous</c> and <c>foreign_keys</c> return no rows at all -- so
    /// reading the SET statement's own result silently verifies nothing for half of
    /// them. Each pragma is therefore set with one statement and queried with a
    /// separate one, which also catches the case where SQLite accepted the request
    /// but declined to act on it.</para>
    /// <para><b>journal_mode=WAL</b> -- ACE reads the world database heavily while the
    /// shard is being written. WAL lets readers proceed against a snapshot instead of
    /// blocking on the writer, which matters because ACE opens a separate
    /// <c>DbContext</c> per operation and has several background workers running.</para>
    /// <para><b>busy_timeout</b> -- instead of failing immediately with SQLITE_BUSY when
    /// two writers collide, wait for the lock. The default is zero, which surfaces
    /// errors under normal concurrent use.</para>
    /// <para><b>synchronous=NORMAL</b> -- the usual WAL companion setting. Safe against
    /// application crashes; only risks the last few transactions on OS/power failure,
    /// which is an acceptable trade for a local development database. Configurable
    /// via <c>Sqlite.Synchronous</c> (0=OFF, 1=NORMAL, 2=FULL, 3=EXTRA), defaulting
    /// to NORMAL so behaviour is unchanged for anyone not asking for it.</para>
    /// <para><b>cache_size</b> and <b>journal_size_limit</b> -- bound the read working
    /// set and the write-ahead log's growth. Both are configurable via
    /// <c>Sqlite.CacheSize</c> and <c>Sqlite.JournalSizeLimit</c>. Neither measured as a
    /// speedup, so these are about bounding worst cases for a long-running process
    /// rather than performance.</para>
    /// <para><b>foreign_keys=ON</b> -- SQLite ignores FK constraints unless asked. The
    /// world database from ace-to-sqlite carries no FKs at all, so this only affects
    /// the auth/shard schemas that ACE creates itself.</para>
    /// </remarks>
    internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(SqlitePragmaInterceptor));

        private const string JournalModeWAL = "wal";

        /// <summary>SQLite reports busy_timeout in milliseconds.</summary>
        private const int BusyTimeoutMs = 30000;

        /// <summary>SQLite reports foreign_keys as 0 or 1.</summary>
        private const int ForeignKeysOn = 1;

        /// <summary>
        /// Pragmas already reported as failing, keyed by database and pragma name.
        /// ACE opens a fresh connection per operation, so a genuine misconfiguration
        /// would otherwise repeat the same warning thousands of times in a boot log
        /// and bury the one line that matters. Bounded by four pragmas times three
        /// databases.
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> reported = new ConcurrentDictionary<string, bool>();

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (connection is not SqliteConnection sqlite)
                return;

            var path = Describe(sqlite);

            // The pragma settings are read per connection rather than cached, because
            // they are process-wide and a test that changes them between connections
            // would otherwise see a stale value. Falling back to a default config when
            // ConfigManager has not been initialised keeps the interceptor usable from
            // design-time and tooling, and the defaults are the previous hardcoded
            // values, so behaviour is unchanged there.
            var config = ConfigManager.Config?.Sqlite ?? new SqliteConfiguration();

            // journal_mode is the one that must not be tolerated. The deployments
            // where it cannot be set -- a read-only mount, a permissions mistake,
            // some network filesystems -- are precisely the ones where quietly
            // continuing on rollback journalling with a zero busy_timeout leads to
            // corruption rather than just slowness. Failing here is the only
            // mitigation, so it fails here.
            //
            // Note this is a check on the journal mode, not on writability. A
            // database already in WAL mode can be opened read-only and read from
            // perfectly well, because reads touch neither the main file nor the
            // -wal. Rejecting that would refuse to start against a world database
            // that is working exactly as intended.
            var mode = Apply(sqlite, path, "journal_mode", "PRAGMA journal_mode=WAL;", "PRAGMA journal_mode;");

            if (!string.Equals(mode, JournalModeWAL, StringComparison.OrdinalIgnoreCase))
                throw PragmaFailure(path, mode);

            // The remainder are degradations rather than corruption risks, so a
            // failure is reported once and the connection stays usable. That is still
            // a real change from ignoring them: busy_timeout in particular defaults
            // to zero, so losing it turns ordinary writer contention into
            // SQLITE_BUSY errors that surface to players as failed operations.
            Expect(path, "busy_timeout", Apply(sqlite, path, "busy_timeout", "PRAGMA busy_timeout=30000;", "PRAGMA busy_timeout;"), BusyTimeoutMs);
            Expect(path, "synchronous", Apply(sqlite, path, "synchronous", $"PRAGMA synchronous={config.Synchronous};", "PRAGMA synchronous;"), config.Synchronous);
            Expect(path, "cache_size", Apply(sqlite, path, "cache_size", $"PRAGMA cache_size={config.CacheSize};", "PRAGMA cache_size;"), config.CacheSize);
            Expect(path, "journal_size_limit", Apply(sqlite, path, "journal_size_limit", $"PRAGMA journal_size_limit={config.JournalSizeLimit};", "PRAGMA journal_size_limit;"), config.JournalSizeLimit);
            Expect(path, "foreign_keys", Apply(sqlite, path, "foreign_keys", "PRAGMA foreign_keys=ON;", "PRAGMA foreign_keys;"), ForeignKeysOn);
        }

        /// <summary>
        /// Applies a pragma and then reads its value back, returning the value as
        /// text, or null if either statement failed.
        /// </summary>
        private static string Apply(SqliteConnection connection, string path, string name, string apply, string query)
        {
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = apply;
                    command.ExecuteNonQuery();
                }
            }
            catch (SqliteException ex)
            {
                // This log line's absence is what made the original implementation
                // hard to diagnose. A development server whose database cannot be
                // written used to start cleanly, say nothing, and then fail at
                // random with "database is locked".
                ReportOnce(path, name, $"Could not apply PRAGMA {name} to {path}: {ex.SqliteErrorCode} {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                ReportOnce(path, name, $"Could not apply PRAGMA {name} to {path}: {ex.GetType().Name} {ex.Message}");
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = query;
                return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                ReportOnce(path, name, $"Could not read back PRAGMA {name} from {path}: {ex.GetType().Name} {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Confirms a non-fatal pragma took the value it was asked for. A null actual
        /// means <see cref="Apply"/> already reported the failure, so there is nothing
        /// useful to add here.
        /// </summary>
        private static void Expect(string path, string name, string actual, int expected)
        {
            if (actual == null)
                return;

            if (actual != expected.ToString(CultureInfo.InvariantCulture))
                ReportOnce(path, name, $"PRAGMA {name} on {path} is '{actual}', expected '{expected}'; it may not be in effect.");
        }

        private static void ReportOnce(string path, string name, string message)
        {
            if (reported.TryAdd($"{path}|{name}", true))
                log.Warn($"[SQLITE] {message}");
        }

        private static InvalidOperationException PragmaFailure(string path, string mode)
        {
            var detail = mode is null
                ? "it could not be applied"
                : $"the database reports '{mode}'";

            var message =
                $"[SQLITE] PRAGMA journal_mode could not be set to WAL on {path}: {detail}. " +
                "ACE requires WAL journalling on its SQLite databases. A database that " +
                "cannot be written to is usually a permissions problem, a read-only mount, " +
                "or a network filesystem, and continuing without WAL risks corrupting it. " +
                "Set Database.Sqlite.* to a path on writable local disk, or switch back to " +
                "the mysql provider.";

            log.Error(message);

            return new InvalidOperationException(message);
        }

        /// <summary>
        /// The database file path, for error messages. Reported as given rather than as
        /// a full path because the connection string may not have one to give.
        /// </summary>
        private static string Describe(SqliteConnection connection)
        {
            var source = connection.DataSource;

            return string.IsNullOrWhiteSpace(source) ? "(in-memory)" : source;
        }
    }
}
