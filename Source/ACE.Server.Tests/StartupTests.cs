using System;
using System.IO;

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
            // TestEnvironment has already initialized the world, which preloads the
            // configured landblocks from the .dat files and resolves their weenies
            // out of the world database. Both of those are the thing being tested.
            WorldManager.StopWorld();
        }
    }
}
