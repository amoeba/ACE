using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Threading;

using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;

namespace ACE.Database.Tests
{
    /// <summary>
    /// Contention between connections: the access pattern ACE generates, and the one
    /// the §3.2 defect lived in.
    /// <para>
    /// ACE opens a <c>DbContext</c> per operation and runs several background workers,
    /// so at any moment some connections are reading and others are writing to the same
    /// three files. Under <c>Cache=Shared</c> that meant a write on one connection
    /// stalled for the whole busy timeout and then failed with
    /// <c>SQLITE_LOCKED</c> ("database table is locked") while another connection
    /// merely held a reader open -- measured at 30,060ms and a failure, against 0ms
    /// under <c>Pooling=True</c>.
    /// </para>
    /// <para>
    /// <see cref="SqliteConnectionStringTests"/> pins the setting but cannot exercise
    /// it: with no contention there is nothing to be wrong <em>with</em>. This class
    /// creates the contention and asserts the outcome, which is the honest way to
    /// close the item.
    /// </para>
    /// <para>
    /// The connection string is the one <see cref="ACE.Database.DbProvider"/>
    /// generates, not a hand-built one. The setting that was wrong lives in the
    /// default, so a hand-built string would prove nothing about what ACE actually
    /// opens -- the same reason the pragma tests, which deliberately hand-build
    /// strings to assert on individual pragma outcomes, are not evidence here.
    /// </para>
    /// <para>
    /// What "holds a reader" means here is worth stating precisely, because the
    /// obvious alternative is a test that cannot fail. A reader holding an
    /// explicit read transaction (<c>BEGIN</c> then <c>SELECT</c>, transaction left
    /// open) blocks a writer for the whole busy timeout <em>even in a healthy WAL
    /// database</em> -- measured at 30,075ms and SQLITE_BUSY. That is normal SQLite
    /// behaviour, not the defect, and asserting otherwise would be asserting something
    /// false. The defect is narrower and worse: a connection that has simply
    /// executed a <c>SELECT</c> and left the reader open, with no transaction at all,
    /// blocks a writer under shared cache and does not under pooling. That is the
    /// scenario below.
    /// </para>
    /// </summary>
    [TestClass]
    public class SqliteConcurrencySoakTests
    {
        /// <summary>
        /// Enough iterations that a per-collision stall would show up as a real
        /// elapsed time rather than noise, few enough that the suite stays fast.
        /// </summary>
        private const int Iterations = 25;

        /// <summary>
        /// The §3.2 defect took 30,060ms before failing. A healthy write while another
        /// connection holds a reader is immediate, so anything past a few seconds is
        /// the defect and not the machine. Ten seconds is three times under the
        /// measured failure and orders of magnitude above the healthy case.
        /// </summary>
        private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(10);

        private static string tempDir;
        private static MasterConfiguration savedConfig;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            tempDir = Path.Combine(Path.GetTempPath(), "ace-concurrency-soak-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            savedConfig = ConfigManager.Config;
        }

        /// <summary>
        /// Removes the temp directory. Retried regardless of outcome, because Windows
        /// can report a file handle as busy for a short while after the owning process
        /// closes it, and a throw here is reported as a failed test even when every
        /// assertion in the class passed.
        /// </summary>
        [ClassCleanup]
        public static void TestCleanup()
        {
            ConfigManager.Initialize(savedConfig);

            if (tempDir == null || !Directory.Exists(tempDir))
                return;

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    return;
                }
                catch (Exception e) when (attempt < 9 && (e is IOException || e is UnauthorizedAccessException))
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// The production connection string for a database in the temp directory.
        /// </summary>
        /// <remarks>
        /// The paths are absolute so they do not depend on the working directory, and
        /// the config is restored in <see cref="TestCleanup"/> because the assembly is
        /// <c>[DoNotParallelize]</c> and the state is process-wide. Each test passes
        /// its own database name so they do not share a file.
        /// </para>
        /// <para>
        /// The busy timeout is shortened from the production 30 seconds to 5. The lock
        /// <em>type</em> is what this class is about, not how long SQLite waits before
        /// reporting it, and the production value is pinned separately by
        /// <see cref="SqliteConnectionStringTests"/>. At 30 seconds a regression would
        /// cost half a minute per iteration -- 25 iterations of the multi-reader test is
        /// twelve minutes -- so the soak would be a test people avoid running. Five
        /// seconds is still fifty times the healthy case, which is immediate.
        /// </para>
        /// <para>
        /// <c>ConnectionOptions</c> is deliberately left at the production default rather
        /// than set here. Setting it would make this class test a string of its own and
        /// a regression to <c>Cache=Shared</c> would pass unnoticed -- the same trap the
        /// pragma tests fall into by hand-building strings. Only the busy timeout is
        /// adjusted, by replacing it in the string that production generated.
        /// </para>
        /// </remarks>
        private static string ProductionConnectionString(string database)
        {
            ConfigManager.Initialize(new MasterConfiguration
            {
                Sqlite = new SqliteConfiguration
                {
                    Authentication = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, "ace_auth.db") },
                    Shard         = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, database) },
                    World         = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, "ace_world.db") }
                }
            });

            return ACE.Database.DbProvider.SqliteConnectionString(DatabaseKind.Shard)
                .Replace("Default Timeout=30", "Default Timeout=5");
        }

        /// <summary>
        /// Asserts the premise of both tests: pooling is on and shared cache is off.
        /// </summary>
        /// <remarks>
        /// Without this the soak could be exercising a hand-built string and proving
        /// nothing about the default the §3.2 defect was in. Checking for the absence of
        /// <c>Cache=Shared</c> as well as the presence of <c>Pooling=True</c> is what makes
        /// the premise complete: either one alone could hold while the other was wrong.
        /// </remarks>
        private static void AssertProductionConnection(string connectionString)
        {
            Assert.IsTrue(
                connectionString.IndexOf("Pooling=True", StringComparison.OrdinalIgnoreCase) >= 0,
                $"The connection string does not enable pooling: '{connectionString}'. " +
                "This soak would no longer be testing the setting the §3.2 defect was in.");

            Assert.IsFalse(
                connectionString.IndexOf("Cache=Shared", StringComparison.OrdinalIgnoreCase) >= 0,
                $"The connection string enables shared cache: '{connectionString}'. " +
                "Shared cache is the §3.2 defect.");
        }

        /// <summary>
        /// A database in WAL mode with one table and one row, which is the minimum
        /// needed for a read to be a read.
        /// </summary>
        private static void CreateDatabase(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // WAL is what makes a reader and a writer able to proceed at the same time.
            // Production gets this from SqlitePragmaInterceptor on every connection; it
            // is set here because these connections are opened directly rather than
            // through EF, so the interceptor is not in play.
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE probe (id INTEGER PRIMARY KEY, name TEXT);";
                create.ExecuteNonQuery();
            }

            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = "INSERT INTO probe (name) VALUES ('seed');";
                seed.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Opens a reader that has executed its query and left the result open, with no
        /// transaction -- the shape of an ACE read that is between operations.
        /// </summary>
        private static SqliteConnection OpenReader(string connectionString, out SqliteCommand command, out SqliteDataReader dataReader)
        {
            var reader = new SqliteConnection(connectionString);
            reader.Open();

            command = reader.CreateCommand();
            command.CommandText = "SELECT * FROM probe;";
            dataReader = command.ExecuteReader();

            // Read one row so the reader is genuinely positioned inside the result set
            // rather than merely opened.
            dataReader.Read();

            return reader;
        }

        [TestMethod]
        public void AWriterSucceedsWhileAnotherConnectionHoldsAReader()
        {
            var connectionString = ProductionConnectionString("ace_shard_soak.db");
            AssertProductionConnection(connectionString);

            CreateDatabase(connectionString);

            var failures = new List<string>();

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                using var reader = OpenReader(connectionString, out var readCommand, out var dataReader);

                using var writer = new SqliteConnection(connectionString);
                writer.Open();

                using var writeCommand = writer.CreateCommand();
                writeCommand.CommandText = "INSERT INTO probe (name) VALUES ('soak');";

                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var rows = writeCommand.ExecuteNonQuery();
                    stopwatch.Stop();

                    if (rows != 1)
                        failures.Add($"iteration {iteration}: expected 1 row written, got {rows}.");

                    if (stopwatch.Elapsed >= StallThreshold)
                        failures.Add(
                            $"iteration {iteration}: a single insert while another connection held a reader " +
                            $"took {stopwatch.ElapsedMilliseconds}ms. The §3.2 defect took 30,060ms before " +
                            $"failing; a healthy write is immediate.");
                }
                catch (SqliteException e)
                {
                    stopwatch.Stop();

                    // SQLITE_LOCKED (6) is the defect: shared cache imposing table-level
                    // locks between connections. SQLITE_BUSY (5) is the retryable one
                    // that busy_timeout exists for. Neither is expected here -- a reader
                    // that is not in a transaction does not block a writer in WAL.
                    failures.Add(
                        $"iteration {iteration}: contention failed with SQLite error {e.SqliteErrorCode} " +
                        $"after {stopwatch.ElapsedMilliseconds}ms: {e.Message}");

                    // Every iteration fails identically once the defect is present, so
                    // there is nothing to learn from continuing and a real regression
                    // would otherwise cost the full busy timeout per iteration.
                    break;
                }

                // The reader must still be usable: it was not blocked, and it must be
                // able to see the row the writer committed.
                try
                {
                    dataReader.Close();

                    using var recheck = reader.CreateCommand();
                    recheck.CommandText = "SELECT COUNT(*) FROM probe;";
                    var count = Convert.ToInt64(recheck.ExecuteScalar());

                    if (count < 2)
                        failures.Add(
                            $"iteration {iteration}: the reader saw {count} rows after the write, so it was " +
                            "not positioned inside the result set and this iteration tested nothing.");
                }
                catch (SqliteException e)
                {
                    failures.Add($"iteration {iteration}: the reader failed after the write: {e.Message}");
                }
            }

            Assert.AreEqual(0, failures.Count,
                $"{failures.Count} of {Iterations} iterations failed:\n  " + string.Join("\n  ", failures));
        }

        [TestMethod]
        public void ManyReadersDoNotBlockAWriter()
        {
            // ACE's actual shape: several connections reading while one writes.
            var connectionString = ProductionConnectionString("ace_shard_multi.db");
            AssertProductionConnection(connectionString);

            CreateDatabase(connectionString);

            var failures = new List<string>();

            for (var iteration = 0; iteration < Iterations; iteration++)
            {
                var readers = new List<SqliteConnection>();
                var dataReaders = new List<SqliteDataReader>();

                try
                {
                    for (var r = 0; r < 4; r++)
                    {
                        var reader = OpenReader(connectionString, out _, out var dataReader);
                        readers.Add(reader);
                        dataReaders.Add(dataReader);
                    }

                    using var writer = new SqliteConnection(connectionString);
                    writer.Open();

                    using var writeCommand = writer.CreateCommand();
                    writeCommand.CommandText = "INSERT INTO probe (name) VALUES ('multi');";

                    var stopwatch = Stopwatch.StartNew();

                    try
                    {
                        writeCommand.ExecuteNonQuery();
                        stopwatch.Stop();

                        if (stopwatch.Elapsed >= StallThreshold)
                            failures.Add(
                                $"iteration {iteration}: the write took {stopwatch.ElapsedMilliseconds}ms " +
                                $"with 4 readers holding result sets.");
                    }
                    catch (SqliteException e)
                    {
                        stopwatch.Stop();
                        failures.Add(
                            $"iteration {iteration}: contention failed with SQLite error {e.SqliteErrorCode} " +
                            $"after {stopwatch.ElapsedMilliseconds}ms: {e.Message}");

                        // Same reasoning as the single-reader test: once the defect is
                        // present every iteration fails the same way.
                        break;
                    }

                    // Every reader must still be able to read.
                    for (var r = 0; r < readers.Count; r++)
                    {
                        try
                        {
                            dataReaders[r].Close();

                            using var recheck = readers[r].CreateCommand();
                            recheck.CommandText = "SELECT COUNT(*) FROM probe;";
                            recheck.ExecuteScalar();
                        }
                        catch (SqliteException e)
                        {
                            failures.Add($"iteration {iteration} reader {r}: failed after the write: {e.Message}");
                        }
                    }
                }
                finally
                {
                    foreach (var dataReader in dataReaders) dataReader.Dispose();
                    foreach (var reader in readers) reader.Dispose();
                }
            }

            Assert.AreEqual(0, failures.Count,
                $"{failures.Count} failures across {Iterations} iterations:\n  " + string.Join("\n  ", failures));
        }
    }
}
