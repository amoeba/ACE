using System;
using System.IO;
using System.Linq;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;

namespace ACE.Database.Tests
{
    [TestClass]
    public class WeenieSearchTests
    {
        private const uint PyrealClassId = 273;

        // Fixed rather than DateTime.Now so a seeded row is byte-identical
        // between runs, which matters when a failed run's database is uploaded
        // as an artifact and diffed against the next one.
        private static readonly DateTime SeededAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static WorldDatabase worldDb;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // copy config.js
            var testDir = AppContext.BaseDirectory;
            var serverDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "ACE.Server"));
            var configSource = Path.Combine(serverDir, "Config.js");

            if (!File.Exists(configSource))
                configSource = Path.Combine(serverDir, "Config.js.example");

            File.Copy(configSource, Path.Combine(testDir, "Config.js"), true);

            ConfigManager.Initialize();
            SqliteBootstrapper.EnsureDatabases();
            SeedPyreal();
            worldDb = new WorldDatabase();
        }

        /// <summary>
        /// The two rows both lookups below resolve through.
        /// <para>
        /// Seeded rather than shipped, so the suite needs no downloaded world
        /// database: <c>EnsureDatabases</c> creates the schema, and these tests
        /// only ever look at one weenie. Against the real 145MB database this is
        /// a no-op, which is what lets the same suite serve both a fixture-backed
        /// run and a fidelity run against the published artifact.
        /// </para>
        /// <para>
        /// <c>class_Name</c> is 'coinstack' and the name property is 'Pyreal'
        /// because that is how upstream stores it -- the two tests deliberately
        /// look the same weenie up by different keys.
        /// </para>
        /// </summary>
        private static void SeedPyreal()
        {
            using var context = new WorldDbContext();
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            if (!context.Weenie.Any(r => r.ClassId == PyrealClassId))
            {
                context.Weenie.Add(new Weenie
                {
                    ClassId = PyrealClassId,
                    ClassName = "coinstack",
                    Type = (int)WeenieType.Coin,
                    LastModified = SeededAt,
                });

                context.SaveChanges();
            }

            if (!context.WeeniePropertiesString.Any(r => r.ObjectId == PyrealClassId && r.Type == (ushort)PropertyString.Name))
            {
                context.WeeniePropertiesString.Add(new WeeniePropertiesString
                {
                    ObjectId = PyrealClassId,
                    Type = (ushort)PropertyString.Name,
                    Value = "Pyreal",
                });

                context.SaveChanges();
            }
        }

        [TestMethod]
        public void GetWeenie_Pyreal_ById_ReturnsObject()
        {
            var result = worldDb.GetWeenie(273);

            var stringName = result.WeeniePropertiesString.FirstOrDefault(x => x.Type == (ushort)PropertyString.Name)?.Value;

            Assert.IsNotNull(result);
            Assert.AreEqual("Pyreal", stringName);
            Assert.AreEqual("Pyreal", result.GetProperty(PropertyString.Name));
        }

        [TestMethod]
        public void GetWeenie_Pyreal_ByName_ReturnsObject()
        {
            var result = worldDb.GetWeenie("coinstack");

            var stringName = result.WeeniePropertiesString.FirstOrDefault(x => x.Type == (ushort)PropertyString.Name)?.Value;

            Assert.IsNotNull(result);
            Assert.AreEqual("Pyreal", stringName);
            Assert.AreEqual("Pyreal", result.GetProperty(PropertyString.Name));
        }
    }
}
