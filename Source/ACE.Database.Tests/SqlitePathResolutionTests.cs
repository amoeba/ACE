using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Where a relative SQLite path actually lands.
    /// <para>
    /// This was <c>Path.GetFullPath(configured)</c>, which resolves against
    /// <c>Environment.CurrentDirectory</c>, so the database files were created
    /// somewhere other than the config that named them whenever the working directory
    /// and the executable directory differed. Under a service manager, a scheduled
    /// task, or a shell that has <c>cd</c>'d elsewhere, that is a silent relocation
    /// of the shard database. See SQLITE_PRODUCTION.md 3.10 and issues.md item 1.
    /// </para>
    /// <para>
    /// The tests are hermetic: the config is supplied directly through
    /// <see cref="ConfigManager.Initialize(MasterConfiguration)"/> rather than read from
    /// a Config.js, so the result does not depend on what a developer happens to have
    /// in their working copy, and they do not touch <c>.dat</c> files or a database.
    /// The assembly is <c>[DoNotParallelize]</c>, so swapping static state and putting
    /// it back is safe.
    /// </para>
    /// </summary>
    [TestClass]
    public class SqlitePathResolutionTests
    {
        private const string RelativeAuth = "db/ace_auth.db";
        private const string RelativeShard = "nested/deeper/ace_shard.db";
        private const string RelativeWorld = "../sibling/ace_world.db";

        private static MasterConfiguration savedConfig;
        private static string savedBaseDirectory;
        private static string sentinel;

        [TestInitialize]
        public void SaveState()
        {
            savedConfig = ConfigManager.Config;
            savedBaseDirectory = DbProvider.SqliteBaseDirectory;

            // A directory that exists and is not the working directory. If the
            // production path ever goes back to consulting the working directory, the
            // assertions below stop matching and say so, which is the entire reason
            // this is a real directory rather than a made-up string.
            sentinel = Path.Combine(Path.GetTempPath(), "ace-path-base-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sentinel);
        }

        [TestCleanup]
        public void RestoreState()
        {
            // ConfigManager.Config has a private setter, so Initialize is the only way
            // back. Passing the previous value through it restores null just as
            // faithfully as a non-null one.
            ConfigManager.Initialize(savedConfig);
            DbProvider.SqliteBaseDirectory = savedBaseDirectory;

            if (sentinel != null && Directory.Exists(sentinel))
                Directory.Delete(sentinel, true);
        }

        private static void UseRelativePaths()
        {
            ConfigManager.Initialize(new MasterConfiguration
            {
                Sqlite = new SqliteConfiguration
                {
                    Authentication = new SqliteDatabaseConfiguration { Database = RelativeAuth },
                    Shard         = new SqliteDatabaseConfiguration { Database = RelativeShard },
                    World         = new SqliteDatabaseConfiguration { Database = RelativeWorld }
                }
            });
        }

        private static string Normalize(string path)
            => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        // -------------------------------------------------------------------
        // The rule, on its own.
        // -------------------------------------------------------------------

        [TestMethod]
        public void ARelativePathIsTakenRelativeToTheBaseDirectory()
        {
            // The base is a parameter, so this asserts the rule rather than inferring
            // it. A regression to Path.GetFullPath(configured) would resolve against
            // the working directory and fail here on any host.
            var baseDirectory = Normalize(sentinel);

            Assert.AreEqual(
                Normalize(Path.Combine(baseDirectory, "db", "ace_auth.db")),
                Normalize(DbProvider.ResolveSqlitePath(RelativeAuth, baseDirectory)));
        }

        [TestMethod]
        public void ARelativePathMayDescendBelowTheBaseDirectory()
        {
            var baseDirectory = Normalize(sentinel);

            Assert.AreEqual(
                Normalize(Path.Combine(baseDirectory, "nested", "deeper", "ace_shard.db")),
                Normalize(DbProvider.ResolveSqlitePath(RelativeShard, baseDirectory)));
        }

        [TestMethod]
        public void ParentSegmentsAreNormalisedRatherThanAppended()
        {
            // "../sibling/..." must come out as a sibling of the base, not as a path
            // containing a literal "..". GetFullPath is what collapses it, and the
            // result has to stay under the base's parent for the assertion to be
            // meaningful.
            var baseDirectory = Normalize(sentinel);

            var resolved = Normalize(DbProvider.ResolveSqlitePath(RelativeWorld, baseDirectory));

            Assert.IsFalse(resolved.Contains(".."),
                $"Resolved path still contains a parent segment: {resolved}");
            Assert.AreEqual(
                Normalize(Path.Combine(Path.GetDirectoryName(baseDirectory), "sibling", "ace_world.db")),
                resolved);
        }

        [TestMethod]
        public void ARootedPathIsUsedExactlyAsGiven()
        {
            // A deployment that deliberately points the files somewhere else must be
            // left alone, so a rooted value short-circuits before the base is applied.
            // Built with Path.Combine so it is rooted on every platform -- a literal
            // "C:\..." would not be.
            var rooted = Path.Combine(Path.GetPathRoot(Path.GetFullPath(sentinel))!, "somewhere-else.db");

            Assert.IsTrue(Path.IsPathRooted(rooted), $"test bug: {rooted} is not rooted");
            Assert.AreEqual(rooted, DbProvider.ResolveSqlitePath(rooted, Normalize(sentinel)));
        }

        [TestMethod]
        public void AnAbsentConfiguredValueFallsBackToThePerDatabaseDefault()
        {
            // SqliteDatabaseConfiguration.Database ships as "", so the default is the
            // path most deployments actually get. It has to resolve the same way as a
            // configured value, or an empty config would reintroduce the bug for
            // exactly the people least likely to have set a path.
            DbProvider.SqliteBaseDirectory = Normalize(sentinel);

            foreach (var kind in Enum.GetValues<DatabaseKind>())
            {
                foreach (var blank in new[] { null, "", "   " })
                {
                    var cfg = new MasterConfiguration { Sqlite = new SqliteConfiguration() };
                    SetDatabase(cfg, kind, blank);

                    ConfigManager.Initialize(cfg);

                    var expected = Normalize(Path.Combine(Normalize(sentinel), "db",
                        $"ace_{kind.ToString().ToLowerInvariant()}.db"));

                    Assert.AreEqual(expected,
                        Normalize(DbProvider.ResolveSqlitePath(kind)),
                        $"kind={kind} configured={(blank == null ? "null" : $"'{blank}'")}");
                }
            }
        }

        [TestMethod]
        public void TheBaseDirectoryIsRootedSoGetFullPathCannotThrow()
        {
            // Path.GetFullPath(path, basePath) throws ArgumentException when basePath is
            // not itself rooted, which would turn a misconfigured default into a
            // failure on every connection rather than one bad path. AppContext
            // .BaseDirectory always ends in a separator and is always absolute, so
            // this only fails if someone reintroduces a computed default.
            var baseDirectory = DbProvider.SqliteBaseDirectory;

            Assert.IsFalse(string.IsNullOrWhiteSpace(baseDirectory),
                "the base directory is empty, so every relative path would throw");
            Assert.IsTrue(Path.IsPathRooted(baseDirectory),
                $"the base directory '{baseDirectory}' is not rooted");
        }

        // -------------------------------------------------------------------
        // The wiring: that the public entry point uses the base directory at all.
        //
        // What these two tests can and cannot prove, since a coverage claim here is
        // only worth as much as its limits:
        //
        //   caught -- reverting the rule to Path.GetFullPath(configured), and
        //             pointing the public overload at Directory.GetCurrentDirectory().
        //             Both were confirmed to fail these tests rather than assumed to.
        //
        //   NOT caught -- changing the *default* of SqliteBaseDirectory from
        //             AppContext.BaseDirectory to Directory.GetCurrentDirectory().
        //             Under the test host those are the same path, so nothing
        //             observable distinguishes them. No in-process test can close
        //             that link; it needs a subprocess started from a different
        //             directory, which is more machinery than the rest of this file
        //             put together. The default is one line and is documented on the
        //             property; it rests on review, not on a test.
        // -------------------------------------------------------------------

        [TestMethod]
        public void ResolvesAgainstTheBaseDirectoryRatherThanTheWorkingDirectory()
        {
            UseRelativePaths();
            DbProvider.SqliteBaseDirectory = Normalize(sentinel);

            var baseDirectory = Normalize(DbProvider.SqliteBaseDirectory);
            var expected = new Dictionary<DatabaseKind, string>
            {
                [DatabaseKind.Authentication] = Path.Combine(baseDirectory, "db", "ace_auth.db"),
                [DatabaseKind.Shard]         = Path.Combine(baseDirectory, "nested", "deeper", "ace_shard.db"),
                [DatabaseKind.World]         = Path.Combine(Path.GetDirectoryName(baseDirectory), "sibling", "ace_world.db")
            };

            // Every kind is checked, and the count is asserted, so this cannot pass by
            // quietly skipping. A test that asserted nothing would still be green
            // against the old implementation, which is the failure mode worth guarding
            // against here specifically.
            var checkedCount = 0;

            foreach (var kind in Enum.GetValues<DatabaseKind>())
            {
                var resolved = Normalize(DbProvider.ResolveSqlitePath(kind));

                Assert.AreEqual(Normalize(expected[kind]), resolved,
                    $"kind={kind} did not resolve against the base directory");
                checkedCount++;
            }

            Assert.AreEqual(3, checkedCount, "not every database kind was exercised");
        }

        [TestMethod]
        public void ARootedConfiguredPathIgnoresTheBaseDirectoryEntirely()
        {
            var rooted = Path.Combine(Path.GetPathRoot(Path.GetFullPath(sentinel))!, "explicitly-placed.db");

            var cfg = new MasterConfiguration { Sqlite = new SqliteConfiguration() };
            SetDatabase(cfg, DatabaseKind.World, rooted);
            ConfigManager.Initialize(cfg);

            DbProvider.SqliteBaseDirectory = Normalize(sentinel);

            Assert.AreEqual(rooted, DbProvider.ResolveSqlitePath(DatabaseKind.World),
                "a configured absolute path should be used unchanged");
        }

        private static void SetDatabase(MasterConfiguration config, DatabaseKind kind, string database)
        {
            switch (kind)
            {
                case DatabaseKind.Authentication:
                    config.Sqlite.Authentication.Database = database;
                    break;
                case DatabaseKind.Shard:
                    config.Sqlite.Shard.Database = database;
                    break;
                case DatabaseKind.World:
                    config.Sqlite.World.Database = database;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
