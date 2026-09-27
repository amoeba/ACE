using System;
using System.IO;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Managers;

[assembly: DoNotParallelize]

namespace ACE.Server.Tests
{
    [TestClass]
    public class StartupTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            TestEnvironment.EnsureInitialized();
        }

        [TestMethod]
        public void DatabaseManager_Initialize()
        {
            // TestEnvironment has already run this. It is the step that forces every
            // prepared statement in the model to be validated against whichever
            // provider Config.js selected, so assert the outcome rather than calling
            // it a second time -- DatabaseManager.Initialize is not re-entrant.
            Assert.IsFalse(DatabaseManager.InitializationFailure, "DatabaseManager reported an initialization failure.");
            Assert.IsNotNull(DatabaseManager.Authentication, "authentication database was not opened");
            Assert.IsNotNull(DatabaseManager.Shard, "shard database was not opened");
            Assert.IsNotNull(DatabaseManager.World, "world database was not opened");
        }

        [TestMethod]
        public void WorldManager_Initialize()
        {
            // TestEnvironment has already initialized the world. Initialize() returns
            // as soon as it has started the thread, so the thing worth checking is
            // that the thread got all the way into the update loop -- which means it
            // survived PreloadConfigLandblocks, the step that dereferences
            // DatManager.CellDat and takes the test host down when no .dat files were
            // loaded. WorldActive is set on the first line of UpdateWorld, so waiting
            // for it with a timeout is a real signal rather than a race.
            //
            // The previous version of this test called StopWorld() and asserted
            // nothing, so it passed whether or not the world ever started.
            var deadline = DateTime.UtcNow.AddSeconds(30);

            while (!WorldManager.WorldActive && DateTime.UtcNow < deadline)
                Thread.Sleep(100);

            Assert.IsTrue(WorldManager.WorldActive,
                "the world manager thread did not reach the update loop within 30s of Initialize");

            WorldManager.StopWorld();

            // StopWorld only raises a flag the loop observes, so give it a moment to
            // actually go down rather than asserting on the flag immediately.
            deadline = DateTime.UtcNow.AddSeconds(30);

            while (WorldManager.WorldActive && DateTime.UtcNow < deadline)
                Thread.Sleep(100);

            Assert.IsFalse(WorldManager.WorldActive, "StopWorld left the world running after 30s");
        }
    }
}
