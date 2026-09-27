using System;
using System.IO;
using System.Threading;

using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;

namespace ACE.Database.Tests
{
    /// <summary>
    /// The schema version stamp that makes a <c>db/</c> directory self-describing.
    /// </summary>
    [TestClass]
    public class SqliteBootstrapperTests
    {
        private static string tempDir;
        private static MasterConfiguration savedConfig;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            tempDir = Path.Combine(Path.GetTempPath(), "ace-bootstrapper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            savedConfig = ConfigManager.Config;
        }

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

        private static SqliteConfiguration ConfigWith(string database, int schemaVersion)
        {
            return new SqliteConfiguration
            {
                Authentication = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, "ace_auth.db") },
                Shard         = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, "ace_shard.db") },
                World         = new SqliteDatabaseConfiguration { Database = Path.Combine(tempDir, database) },
                SchemaVersion = schemaVersion
            };
        }

        private static int ReadUserVersion(string path)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <summary>
        /// A non-default version is used on purpose: stamping a constant would pass even
        /// if the configured value were being ignored.
        /// </summary>
        [TestMethod]
        public void EnsureDatabases_StampsTheConfiguredSchemaVersion()
        {
            const int version = 7;

            ConfigManager.Initialize(new MasterConfiguration
            {
                Database = new DatabaseProviderConfiguration { Provider = "sqlite", AutoCreate = true, WorldDatabaseUrl = "" },
                Sqlite   = ConfigWith("ace_world.db", version)
            });

            SqliteBootstrapper.EnsureDatabases();

            Assert.AreEqual(version, ReadUserVersion(Path.Combine(tempDir, "ace_auth.db")),
                "ace_auth should be stamped with the configured schema version");
            Assert.AreEqual(version, ReadUserVersion(Path.Combine(tempDir, "ace_shard.db")),
                "ace_shard should be stamped with the configured schema version");
            Assert.AreEqual(version, ReadUserVersion(Path.Combine(tempDir, "ace_world.db")),
                "ace_world should be stamped with the configured schema version");
        }

        /// <summary>
        /// The default, so a deployment that never touches the setting gets a real
        /// version rather than SQLite's 0.
        /// </summary>
        [TestMethod]
        public void EnsureDatabases_StampsTheDefaultSchemaVersionWhenUnset()
        {
            ConfigManager.Initialize(new MasterConfiguration
            {
                Database = new DatabaseProviderConfiguration { Provider = "sqlite", AutoCreate = true, WorldDatabaseUrl = "" },
                Sqlite   = ConfigWith("ace_world.db", 1)
            });

            SqliteBootstrapper.EnsureDatabases();

            Assert.AreEqual(1, ReadUserVersion(Path.Combine(tempDir, "ace_auth.db")),
                "the default schema version should be 1, not SQLite's 0");
        }
    }
}
