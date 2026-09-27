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

        /// <summary>
        /// The <c>ACE.Server</c> project directory, found by walking up from the test
        /// output until a directory contains <c>ACE.Server/Config.js.example</c>.
        /// <para>
        /// The previous version counted five parent directories, which is only right
        /// for a <c>bin/x64/Debug/net*</c> output layout. A default <c>dotnet test</c>
        /// emits <c>bin/Debug/net*</c> -- four levels -- so the walk overshot to the
        /// repository root and initialization failed on a path ending in
        /// <c>.../ACE.Server/Config.js.example</c> (note: repo root, not
        /// <c>Source/ACE.Server</c>). That is why CI had to pass
        /// <c>-p:Platform=x64</c> just to keep the arithmetic correct. Looking for
        /// the file instead of counting directories does not care about platform,
        /// build configuration, or how deep the output path is.
        /// </para>
        /// <para>
        /// Internal rather than private because <see cref="StarterGearTests"/> needs the
        /// same answer and a fourth copy of this loop is worse than sharing one.
        /// </para>
        /// <para>
        /// The starting directory is a parameter so the search can be tested at a depth
        /// the test chooses. That matters because counting and searching agree at the
        /// depth the test host happens to use -- <c>bin/arm64/Release/net10.0</c> is four
        /// levels down, so five parents lands on <c>Source</c>, which is the right
        /// answer. A test that only ever calls the parameterless overload therefore
        /// cannot tell the two apart, and the defect this replaces is precisely a
        /// disagreement at some other depth.
        /// </para>
        /// </summary>
        internal static string FindServerDirectory()
            => FindServerDirectory(AppContext.BaseDirectory);

        /// <summary>
        /// The nearest ancestor of <paramref name="baseDirectory"/> that contains
        /// <c>ACE.Server/Config.js.example</c>, or the directory itself.
        /// </summary>
        internal static string FindServerDirectory(string baseDirectory)
        {
            var dir = new DirectoryInfo(baseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "ACE.Server", "Config.js.example");

                if (File.Exists(candidate))
                    return Path.GetDirectoryName(candidate)!;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Could not find ACE.Server/Config.js.example in any directory above {AppContext.BaseDirectory}. " +
                "The test output directory does not appear to sit inside the source tree.");
        }

        private static void Initialize()
        {
            var testDir = AppContext.BaseDirectory;
            var serverDir = FindServerDirectory();
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
