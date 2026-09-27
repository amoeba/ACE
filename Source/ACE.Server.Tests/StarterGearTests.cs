using System;
using System.IO;

using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Parsing the shipped starter gear configuration.
    /// <para>
    /// The file is located by walking up to the nearest ancestor directory that
    /// contains <c>ACE.Server/Config.js.example</c>, not by counting parent
    /// directories. Counting five levels was correct only for
    /// <c>bin/&lt;Platform&gt;/Release/net*</c>; a flat <c>bin/Release/net*</c> layout
    /// overshoots to the repository root and the read fails with a
    /// <see cref="FileNotFoundException"/> for a path that looks almost right. See
    /// issues.md item 2a.
    /// </para>
    /// </summary>
    [TestClass]
    public class StarterGearTests
    {
        [TestMethod]
        public void CanParseStarterGearJson()
        {
            string contents = File.ReadAllText(StarterGearPath());

            StarterGearConfiguration config = JsonSerializer.Deserialize<StarterGearConfiguration>(contents, ConfigManager.SerializerOptions);
        }

        /// <summary>
        /// The shipped starter gear file. <see cref="TestEnvironment.FindServerDirectory"/>
        /// returns the <c>ACE.Server</c> project directory itself, so the file sits
        /// directly inside it.
        /// </summary>
        private static string StarterGearPath()
            => Path.Combine(TestEnvironment.FindServerDirectory(), "starterGear.json");

        [TestMethod]
        public void StarterGearJson_ResolvesBySearchingRatherThanByCountingDepth()
        {
            // Asserts the resolved path and the reason it is right, rather than relying
            // on the read above having happened to succeed. If the walk goes back to
            // counting levels, this is the test that says so -- a FileNotFoundException
            // from CanParseStarterGearJson would not identify the cause.
            var serverDir = TestEnvironment.FindServerDirectory();

            Assert.IsTrue(
                File.Exists(Path.Combine(serverDir, "Config.js.example")),
                $"'{serverDir}' was returned as the server directory but does not contain " +
                "Config.js.example, so the directory is not being found by searching.");

            var path = StarterGearPath();

            Assert.IsTrue(File.Exists(path),
                $"starterGear.json was not found at '{path}'. The server directory is resolved by " +
                "counting parent directories, which is only correct for one output layout.");
        }

        [TestMethod]
        public void TheServerDirectoryIsFoundFromAnyOutputDepth()
        {
            // The case the five-level walk got wrong, and the reason the search takes a
            // starting directory at all.
            //
            // At the depth the test host actually uses -- bin/arm64/Release/net10.0, four
            // levels -- counting five parents lands on Source, which is the correct
            // answer, so no test calling only the parameterless overload can distinguish
            // counting from searching. Three levels deeper, the same arithmetic lands
            // above the source tree and the file is not found. Driving the depth from
            // here is what makes the difference observable.
            //
            // The path does not have to exist: the search only inspects ancestors.
            var deeper = Path.Combine(AppContext.BaseDirectory, "a", "b", "c");

            var fromHere = TestEnvironment.FindServerDirectory();
            var fromDeeper = TestEnvironment.FindServerDirectory(deeper);

            Assert.AreEqual(fromHere, fromDeeper,
                "the server directory moved when the output depth changed, so the directory " +
                "is being found by counting parent directories rather than by searching.");

            Assert.IsTrue(File.Exists(Path.Combine(fromDeeper, "Config.js.example")),
                $"'{fromDeeper}' was returned for a deeper output directory but does not contain " +
                "Config.js.example.");
        }
    }
}
