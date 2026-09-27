using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Server.DatabaseUpdate;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The automatic update pipelines resolve to a different implementation per
    /// provider, and only one of them can actually do the work.
    /// </summary>
    /// <remarks>
    /// These are worth pinning for two reasons. MySQL is the default and the
    /// behaviour that must not change, so a resolver that ever returned the wrong
    /// implementation would silently stop a MySQL shard from being patched. And the
    /// SQLite implementation has to keep refusing loudly: the alternative is a
    /// method that quietly does nothing, which is how a shard ends up years behind
    /// with nothing in the log to say so.
    /// </remarks>
    [TestClass]
    public class DatabaseUpdateProviderTests
    {
        [TestMethod]
        public void For_MySql_ResolvesToTheMySqlImplementation()
        {
            var provider = DatabaseUpdates.For(DatabaseProvider.MySql);

            Assert.IsInstanceOfType(provider, typeof(MySqlDatabaseUpdateProvider));
            Assert.IsTrue(provider.IsSupported);
        }

        [TestMethod]
        public void For_Sqlite_ResolvesToTheSqliteImplementation()
        {
            var provider = DatabaseUpdates.For(DatabaseProvider.Sqlite);

            Assert.IsInstanceOfType(provider, typeof(SqliteDatabaseUpdateProvider));
            Assert.IsFalse(provider.IsSupported);
        }

        [TestMethod]
        public void For_UnknownProvider_FallsBackToMySql()
        {
            // DatabaseProvider is an enum, but the value that reaches Resolve() comes
            // from a config string, so the fallback is the load-bearing path rather
            // than a theoretical one.
            Assert.IsInstanceOfType(DatabaseUpdates.For((DatabaseProvider)999), typeof(MySqlDatabaseUpdateProvider));
        }

        [TestMethod]
        public void MySql_ReportsNoUnsupportedReason()
        {
            Assert.AreEqual(0, DatabaseUpdates.For(DatabaseProvider.MySql).UnsupportedReason.Count);
        }

        [TestMethod]
        public void Sqlite_ExplainsWhyItCannotRun()
        {
            var reason = DatabaseUpdates.For(DatabaseProvider.Sqlite).UnsupportedReason;

            Assert.IsTrue(reason.Count > 0, "an unsupported provider has to say why");

            var text = string.Join(" ", reason);

            // The three things an operator needs in order to act on this.
            StringAssert.Contains(text, "MySQL");
            StringAssert.Contains(text, "not receive schema or data patches");
            StringAssert.Contains(text, "WorldDatabaseUrl");
        }

        [TestMethod]
        public void Sqlite_ReasonIsOneSentencePerLogLine()
        {
            // These are logged a line at a time, so an entry containing a newline
            // would render as a broken multi-line log record.
            foreach (var line in DatabaseUpdates.For(DatabaseProvider.Sqlite).UnsupportedReason)
            {
                Assert.IsFalse(line.Contains('\n') || line.Contains('\r'), $"reason line contains a newline: {line}");
                Assert.IsTrue(line.Trim().Length > 0, "reason line is blank");
            }
        }

        [TestMethod]
        public void Sqlite_RefusesToRunThePipelines()
        {
            // The caller is expected to check IsSupported first, so reaching these is
            // a wiring bug. Throwing beats returning quietly: a no-op that reports
            // success is exactly the failure mode this class exists to prevent.
            var provider = DatabaseUpdates.For(DatabaseProvider.Sqlite);

            Assert.Throws<NotSupportedException>(() => provider.CheckForWorldDatabaseUpdate());
            Assert.Throws<NotSupportedException>(() => provider.AutoApplyWorldCustomizations());
            Assert.Throws<NotSupportedException>(() => provider.AutoApplyDatabaseUpdates());
        }

        [TestMethod]
        public void Active_MatchesTheConfiguredProvider()
        {
            // The resolver's one-line remainder, and the thing that actually runs at
            // startup.
            //
            // Asserting that Active agrees with itself proves nothing, because
            // DatabaseUpdates.Active is defined as For(DbProvider.Active). What can
            // fail is the case where the two silently agree for the wrong reason:
            // DbProvider.Active swallows an uninitialised ConfigManager and returns
            // MySQL, so a run that never loaded a config agrees with a run whose
            // config says MySQL -- and a filtered run in this project is exactly the
            // former, because nothing here calls ConfigManager.Initialize.
            //
            // So pin the precondition: either a config was actually read, or say so
            // and skip. A test that cannot distinguish "MySQL because configured" from
            // "MySQL because unconfigured" is not evidence for the SQLite provider.
            if (ACE.Database.DbProvider.ConfigUnavailable)
            {
                Assert.Inconclusive(
                    "No Config.js was loaded, so DbProvider.Active is falling back to MySQL. " +
                    "Initialise ConfigManager against a config with Database.Provider set before " +
                    "using this test as evidence about provider resolution.");
                return;
            }

            var configured = ConfigManager.Config.Database.Resolve();

            Assert.AreEqual(configured, ACE.Database.DbProvider.Active,
                "DbProvider.Active disagrees with the configured provider.");

            Assert.IsTrue(
                DatabaseUpdates.Active is SqliteDatabaseUpdateProvider == (configured == DatabaseProvider.Sqlite),
                $"DatabaseUpdates.Active did not resolve to the implementation for {configured}.");
        }
    }
}
