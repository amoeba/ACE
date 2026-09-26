using System;
using System.IO;

using ACE.Common;
using ACE.Database;
using ACE.DatLoader;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// One-time, process-wide initialisation for the integration tests.
    /// <para>
    /// Most of what these tests exercise is static (<see cref="DatabaseManager"/>,
    /// <see cref="WorldManager"/>, <see cref="GuidManager"/>, ...), and several of
    /// those are not re-entrant, so initialisation has to happen exactly once no
    /// matter how many test classes ask for it.
    /// </para>
    /// <para>
    /// The order below mirrors <c>Program.cs</c>, with the console/network layers
    /// left out because there is nothing to serve.
    /// </para>
    /// </summary>
    internal static class TestEnvironment
    {
        private static readonly object Gate = new object();

        private static bool initialized;
        private static Exception failure;

        public static void EnsureInitialized()
        {
            lock (Gate)
            {
                if (initialized)
                    return;

                if (failure != null)
                    throw new InvalidOperationException("Test environment initialisation previously failed.", failure);

                try
                {
                    Initialize();
                    initialized = true;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }
            }
        }

        private static void Initialize()
        {
            var testDir = AppContext.BaseDirectory;
            var serverDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "ACE.Server"));
            var configSource = Path.Combine(serverDir, "Config.js");

            if (!File.Exists(configSource))
                configSource = Path.Combine(serverDir, "Config.js.example");

            File.Copy(configSource, Path.Combine(testDir, "Config.js"), true);

            ConfigManager.Initialize();

            // The .dat files use Windows-1252 strings, and .NET Core does not ship
            // that code page by default. Program.cs and DatTests.cs both register it.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            DatManager.Initialize(ConfigManager.Config.Server.DatFilesDirectory, true);

            DatabaseManager.Initialize();

            if (DatabaseManager.InitializationFailure)
                throw new InvalidOperationException(
                    "DatabaseManager reported an initialisation failure. Check that Config.js points at a reachable database provider " +
                    "and that the world database is populated.");

            DatabaseManager.Start();

            // GuidManager reads "unlimited_sequence_gaps" out of PropertyManager,
            // so PropertyManager has to come first.
            PropertyManager.Initialize();
            GuidManager.Initialize();

            // WorldManager's update loop ticks these on a background thread and
            // dereferences their state without null checks, so they all have to be
            // up before the world starts.
            PlayerManager.Initialize();
            HouseManager.Initialize();

            WorldManager.Initialize();
        }
    }
}
