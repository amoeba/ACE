using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;

using log4net;
using log4net.Appender;
using log4net.Config;
using log4net.Core;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Covers <see cref="SqlitePragmaInterceptor"/>, which applies and verifies the
    /// pragmas ACE depends on every time it opens a SQLite connection.
    /// </summary>
    /// <remarks>
    /// The interceptor previously swallowed every failure. A database that could not
    /// be put into WAL mode therefore produced a server that started cleanly, logged
    /// nothing about it, and then failed at random with "database is locked" -- which
    /// is <c>busy_timeout</c> never having been set either. These tests assert the
    /// opposite: the pragmas are applied, the resulting state is read back, and a
    /// pragma that cannot be applied is reported rather than ignored.
    /// </remarks>
    [TestClass]
    public class SqlitePragmaTests
    {
        private static string tempDir;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            tempDir = Path.Combine(Path.GetTempPath(), "ace-pragma-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        [ClassCleanup]
        public static void TestCleanup()
        {
            if (tempDir != null && Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }

        [TestMethod]
        public void ConnectionOpened_AppliesAndVerifiesPragmas()
        {
            var path = NewPath("applied");

            OpenWithPragmas($"Data Source={path}", connection =>
            {
                Assert.AreEqual("wal", Scalar(connection, "PRAGMA journal_mode;"), "journal_mode should be WAL");
                Assert.AreEqual(30000L, ToInt64(Scalar(connection, "PRAGMA busy_timeout;")), "busy_timeout should be 30s rather than the default of 0");
                Assert.AreEqual(1L, ToInt64(Scalar(connection, "PRAGMA synchronous;")), "synchronous should be NORMAL (1)");
                Assert.AreEqual(1L, ToInt64(Scalar(connection, "PRAGMA foreign_keys;")), "foreign_keys should be on");
            });

            Assert.IsTrue(File.Exists(path), "the database file should have been created");
        }

        /// <summary>
        /// A healthy database must produce no warning at all. The interceptor
        /// verifies each pragma by reading its value back, and a pragma whose SET
        /// form returns no rows -- <c>synchronous</c> and <c>foreign_keys</c> both do
        /// -- will read back as nothing and be reported as possibly not in effect on
        /// every single connection. Asserting the pragmas' effect from outside the
        /// interceptor does not catch that, because the pragmas really are in effect;
        /// only the log does. That regression shipped once, so it is guarded here.
        /// </summary>
        [TestMethod]
        public void ConnectionOpened_HealthyDatabase_LogsNoWarnings()
        {
            var path = NewPath("no-warnings");

            // LogManager.GetLogger(Type) keys loggers by the type's full name, so this
            // resolves to the same logger the interceptor writes to. The concrete
            // hierarchy type is needed because log4net.ILogger does not expose the
            // appender or the level-specific logging methods.
            var logger = (log4net.Repository.Hierarchy.Logger)LogManager.GetRepository()
                .GetLogger(typeof(SqlitePragmaInterceptor).FullName);

            EnsureLog4NetConfigured();

            var appender = new MemoryAppender();
            logger.AddAppender(appender);

            List<LoggingEvent> captured;

            try
            {
                // Sentinel, so a test that captured nothing because the appender was
                // never wired up fails loudly instead of passing vacuously.
                logger.Log(typeof(SqlitePragmaInterceptor), Level.Warn, "sentinel", null);

                OpenWithPragmas($"Data Source={path}");
            }
            finally
            {
                logger.RemoveAppender(appender);
                captured = appender.GetEvents().ToList();
            }

            var warnings = captured
                .Where(e => Level.Compare(e.Level, Level.Warn) >= 0)
                .Select(e => e.RenderedMessage)
                .ToList();

            // Only the sentinel is allowed. Repeated identical lines are collapsed by
            // the interceptor itself, so a single stray warning still means a real one.
            CollectionAssert.AreEqual(new[] { "sentinel" }, warnings, "unexpected log output while opening a healthy database");
        }

        [TestMethod]
        public void ConnectionOpened_PragmaThatCannotBeApplied_Throws()
        {            // A read-only connection to a database that is not yet in WAL mode cannot
            // change the journal mode, and SQLite reports that as SQLITE_READONLY.
            // This is the shape of a misconfigured deployment: a read-only mount, a
            // permissions mistake, or a network filesystem. Everything except
            // journal_mode still applies on such a connection, so only journal_mode
            // tells you the database is not in the state ACE requires.
            var path = NewPath("readonly-connection");
            CreateRollbackJournalledDatabase(path);

            var ex = Assert.Throws<InvalidOperationException>(() => OpenWithPragmas($"Data Source={path};Mode=ReadOnly"));

            // The message is the only thing a developer sees before the server gives
            // up, so it has to name both the pragma that failed and the file.
            StringAssert.Contains(ex.Message, "journal_mode");
            StringAssert.Contains(ex.Message, path);
        }

        /// <summary>
        /// The counterpart to the above, and the reason the check is on the journal
        /// mode rather than on writability. A database already in WAL mode can be
        /// opened read-only and read from perfectly well, because reads touch neither
        /// the main file nor the -wal. Rejecting that would refuse to start against a
        /// world database that is working exactly as intended.
        /// </summary>
        [TestMethod]
        public void ConnectionOpened_AlreadyWalOnAReadOnlyConnection_IsAccepted()
        {
            var path = NewPath("already-wal");

            OpenWithPragmas($"Data Source={path}");
            Assert.AreEqual("wal", CurrentJournalMode(path));

            // No exception is the assertion.
            OpenWithPragmas($"Data Source={path};Mode=ReadOnly", connection =>
            {
                Assert.AreEqual("wal", Scalar(connection, "PRAGMA journal_mode;"));
                Assert.AreEqual(30000L, ToInt64(Scalar(connection, "PRAGMA busy_timeout;")));
            });
        }

        /// <summary>
        /// Opens the given connection string the way production does -- through a
        /// context carrying the interceptor -- and optionally inspects the
        /// connection while it is still open.
        /// </summary>
        private static void OpenWithPragmas(string connectionString, Action<DbConnection> inspect = null)
        {
            var options = new DbContextOptionsBuilder<ProbeContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new SqlitePragmaInterceptor())
                .Options;

            using var context = new ProbeContext(options);

            context.Database.OpenConnection();

            inspect?.Invoke(context.Database.GetDbConnection());
        }

        /// <summary>
        /// Creates a database in the default rollback-journalling mode. Re-issuing
        /// journal_mode=WAL against a database that is already in WAL mode is a no-op
        /// that succeeds, so a WAL fixture would not exercise the failure at all.
        /// </summary>
        private static void CreateRollbackJournalledDatabase(string path)
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY);";
            command.ExecuteNonQuery();

            Assert.AreEqual("delete", CurrentJournalMode(path), "fixture should not already be in WAL mode");
        }

        private static string CurrentJournalMode(string path)
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";

            return Convert.ToString(command.ExecuteScalar());
        }

        private static void EnsureLog4NetConfigured()
        {
            // Other test classes initialise log4net from ACE.Server's config, but test
            // class order is not guaranteed. Without a configured repository an
            // appender attached to a logger receives nothing at all.
            if (!LogManager.GetRepository().Configured)
                BasicConfigurator.Configure();
        }

        private static string NewPath(string name)
        {
            return Path.Combine(tempDir, name + ".db");
        }

        private static object Scalar(DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        private static long ToInt64(object value)
        {
            Assert.IsNotNull(value, "pragma readback returned nothing");
            return Convert.ToInt64(value);
        }

        /// <summary>
        /// A context with no entities. Only its connection is used, which is the part
        /// the interceptor acts on.
        /// </summary>
        private sealed class ProbeContext : DbContext
        {
            public ProbeContext(DbContextOptions options) : base(options) { }
        }
    }
}
