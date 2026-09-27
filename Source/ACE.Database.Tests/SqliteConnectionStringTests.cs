using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;

namespace ACE.Database.Tests
{
    /// <summary>
    /// The SQLite connection string that production actually uses.
    /// <para>
    /// The pragma tests deliberately open connections with a hand-built connection
    /// string, because that is the only way to assert on a specific pragma outcome
    /// such as "cannot set WAL on a read-only file". The consequence is that the
    /// string <em>ACE itself</em> builds is never asserted on anywhere -- which is
    /// how a default that quietly defeats WAL survived review, CI and a documented
    /// P1 defect report. These tests close that gap.
    /// </para>
    /// <para>
    /// No database and no Config.js: the default lives in
    /// <see cref="SqliteDatabaseConfiguration"/>, so this runs in the suite CI
    /// actually executes.
    /// </para>
    /// </summary>
    [TestClass]
    public class SqliteConnectionStringTests
    {
        private static string Default => new SqliteDatabaseConfiguration().ConnectionOptions;

        [TestMethod]
        public void Default_DoesNotUseSharedCache()
        {
            // Shared cache makes every connection to a file share one page cache, and
            // shared cache implies table-level locking between those connections. In
            // WAL that means a write on one connection blocks on any other connection
            // merely reading the table, for the whole busy timeout, and then fails
            // with SQLITE_LOCKED ("database table is locked") -- which busy_timeout
            // does not retry. That is the reader/writer concurrency WAL exists to
            // provide, and it is what ACE generates: a DbContext per operation plus
            // several background workers.
            //
            // Measured with Microsoft.Data.Sqlite 9.0.20, journal_mode=WAL:
            //   Cache=Shared  write while another connection holds a reader
            //                 -> "database table is locked" after 30078ms
            //   Pooling=True  same operation
            //                 -> succeeds in 0ms
            Assert.IsFalse(
                Default.IndexOf("Cache=Shared", StringComparison.OrdinalIgnoreCase) >= 0,
                $"ConnectionOptions enables shared cache: '{Default}'. See SQLITE_PRODUCTION.md 3.2.");
        }

        [TestMethod]
        public void Default_UsesPooling()
        {
            // Pooling gives back what shared cache was reaching for -- reuse across
            // ACE's many short-lived contexts -- without the shared page cache.
            Assert.IsTrue(
                Default.IndexOf("Pooling", StringComparison.OrdinalIgnoreCase) >= 0,
                $"ConnectionOptions does not enable pooling: '{Default}'.");
        }

        [TestMethod]
        public void Default_SetsABusyTimeoutMatchingTheInterceptor()
        {
            // SqlitePragmaInterceptor applies and verifies busy_timeout=30000 on every
            // connection. A Default Timeout that disagreed with it would mean the
            // connection string and the pragma fight each other, and which one wins
            // depends on ordering.
            Assert.IsTrue(
                Default.IndexOf("Default Timeout=30", StringComparison.OrdinalIgnoreCase) >= 0,
                $"ConnectionOptions does not set Default Timeout=30 to match the interceptor: '{Default}'.");
        }

        [TestMethod]
        public void Default_EnablesForeignKeys()
        {
            // SQLite ignores FK constraints unless asked. The world database carries
            // none, but the auth and shard schemas that SqliteBootstrapper generates
            // from the EF model do.
            Assert.IsTrue(
                Default.IndexOf("Foreign Keys=True", StringComparison.OrdinalIgnoreCase) >= 0,
                $"ConnectionOptions does not enable foreign keys: '{Default}'.");
        }

        [TestMethod]
        public void ConnectionOptions_IsOverridableFromConfig()
        {
            // It used to be get-only with [JsonIgnore], which meant the one setting
            // that turned out to be wrong could not be corrected without a rebuild.
            // It is a plain settable property now, so assert that stays true.
            var cfg = new SqliteDatabaseConfiguration { ConnectionOptions = "Pooling=True;Default Timeout=5" };

            Assert.AreEqual("Pooling=True;Default Timeout=5", cfg.ConnectionOptions,
                "ConnectionOptions is not settable, so an operator cannot override the default.");
        }

        [TestMethod]
        public void ConnectionOptions_IsNotJsonIgnored()
        {
            // [JsonIgnore] would keep the value out of the config file the operator
            // edits, which is the same problem by a different route.
            var ignored = typeof(SqliteDatabaseConfiguration)
                .GetProperty(nameof(SqliteDatabaseConfiguration.ConnectionOptions))!
                .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), false);

            Assert.AreEqual(0, ignored.Length,
                "ConnectionOptions is [JsonIgnore], so it cannot be set from Config.js.");
        }

        [TestMethod]
        public void Resolve_FallsBackToMySqlForAnythingButSqlite()
        {
            // MySQL is the default so that every config predating this feature, and
            // every typo, behaves as it always did.
            Assert.AreEqual(DatabaseProvider.MySql, new DatabaseProviderConfiguration().Resolve(),
                "an absent Provider should resolve to MySQL");
            Assert.AreEqual(DatabaseProvider.MySql, new DatabaseProviderConfiguration { Provider = "mysql" }.Resolve());
            Assert.AreEqual(DatabaseProvider.MySql, new DatabaseProviderConfiguration { Provider = "MySQL" }.Resolve(),
                "Provider should be parsed case-insensitively");
            Assert.AreEqual(DatabaseProvider.MySql, new DatabaseProviderConfiguration { Provider = "postgres" }.Resolve(),
                "an unrecognised Provider should resolve to MySQL");
            Assert.AreEqual(DatabaseProvider.MySql, new DatabaseProviderConfiguration { Provider = "" }.Resolve(),
                "an empty Provider should resolve to MySQL");
            Assert.AreEqual(DatabaseProvider.Sqlite, new DatabaseProviderConfiguration { Provider = "sqlite" }.Resolve());
            Assert.AreEqual(DatabaseProvider.Sqlite, new DatabaseProviderConfiguration { Provider = "SQLite" }.Resolve(),
                "Provider should be parsed case-insensitively");
        }
    }
}
