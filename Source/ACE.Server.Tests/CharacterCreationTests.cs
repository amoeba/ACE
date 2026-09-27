using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// End-to-end character creation against whichever provider Config.js selects.
    /// <para>
    /// This is the broadest single exercise of the shard write path available
    /// without a game client: it allocates a guid through
    /// <see cref="GuidManager"/> (which is backed by the provider-specific
    /// <c>GetSequenceGaps</c> query), runs the full <see cref="PlayerFactory"/>
    /// conversion from weenie to live player across the ~2,500 lines of
    /// ACE.Adapter mapping, then persists the character and all of its starter
    /// gear through <c>AddCharacterInParallel</c>, which writes the parent biota,
    /// every possession and the character row inside one save.
    /// </para>
    /// <para>
    /// Read-back deliberately goes through a fresh <see cref="ShardDbContext"/>
    /// rather than <c>DatabaseManager.Shard</c>, so the assertions hit the database
    /// instead of the in-memory biota cache that sits in front of it.
    /// </para>
    /// </summary>
    [TestClass]
    public class CharacterCreationTests
    {
        private const string CharacterName = "Sqltestchar";
        private const uint TestAccountId = 1;
        private const ushort ExpectedLevel = 275;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            TestEnvironment.EnsureInitialized();
        }

        [TestMethod]
        public void CreateCharacter_PersistsToShard_AndReadsBackFromDatabase()
        {
            var weenie = DatabaseManager.World.GetCachedWeenie("human");
            Assert.IsNotNull(weenie, "world database does not contain the 'human' weenie");

            // DynamicGuidAllocator is seeded from the shard via GetSequenceGaps, so
            // this also covers the provider-specific gap query.
            var guid = GuidManager.NewPlayerGuid();
            Assert.AreNotEqual(ObjectGuid.Invalid.Full, guid.Full, "GuidManager returned an invalid guid");

            var player = PlayerFactoryEx.Create275HeavyWeapons(weenie, guid, TestAccountId, CharacterName);
            Assert.IsNotNull(player, "PlayerFactory returned no player");

            var possessions = player.GetAllPossessions();
            var possessedBiotas = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>();

            foreach (var possession in possessions)
                possessedBiotas.Add((possession.Biota, possession.BiotaDatabaseLock));

            Assert.IsTrue(possessedBiotas.Count > 0, "starter gear produced no possessions to save");

            // SerializedShardDatabase dispatches onto a background queue, so the
            // result arrives via callback.
            var completed = new ManualResetEventSlim(false);
            var saved = false;

            DatabaseManager.Shard.AddCharacterInParallel(
                player.Biota,
                player.BiotaDatabaseLock,
                possessedBiotas,
                player.Character,
                player.CharacterDatabaseLock,
                ok => { saved = ok; completed.Set(); });

            Assert.IsTrue(completed.Wait(TimeSpan.FromMinutes(2)), "AddCharacterInParallel did not complete within two minutes");
            Assert.IsTrue(saved, "AddCharacterInParallel reported a save failure");

            var possessionIds = possessedBiotas.Select(b => b.biota.Id).ToList();

            try
            {
                VerifyPersisted(weenie.WeenieClassId, guid.Full, possessionIds);
            }
            finally
            {
                // Leave the shard the way it was found, so the test is repeatable
                // and does not litter the dev database.
                Cleanup(guid.Full, possessionIds);
            }
        }

        private static void VerifyPersisted(uint weenieClassId, uint characterId, List<uint> possessionIds)
        {
            using var context = new ShardDbContext();

            var biota = context.Biota.AsNoTracking().FirstOrDefault(b => b.Id == characterId);
            Assert.IsNotNull(biota, "no biota row was written for the new character");
            Assert.AreEqual(weenieClassId, biota.WeenieClassId, "biota row has the wrong weenie class id");
            Assert.AreEqual((int)WeenieType.Creature, biota.WeenieType, "biota row has the wrong weenie type");

            var character = context.Character.AsNoTracking().FirstOrDefault(c => c.Id == characterId);
            Assert.IsNotNull(character, "no character row was written for the new character");
            Assert.AreEqual(CharacterName, character.Name, "character row has the wrong name");
            Assert.AreEqual(TestAccountId, character.AccountId, "character row has the wrong account id");
            Assert.IsFalse(character.IsDeleted, "new character was written as deleted");

            // The display name also lives as a PropertyString on the biota.
            var name = context.BiotaPropertiesString.AsNoTracking()
                .FirstOrDefault(p => p.ObjectId == characterId && p.Type == (ushort)PropertyString.Name);

            Assert.IsNotNull(name, "character name was not written to biota_properties_string");
            Assert.AreEqual(CharacterName, name.Value, "biota_properties_string has the wrong name");

            // Level is a PropertyInt, not a column on the character row.
            var level = context.BiotaPropertiesInt.AsNoTracking()
                .FirstOrDefault(p => p.ObjectId == characterId && p.Type == (ushort)PropertyInt.Level);

            Assert.IsNotNull(level, "character level was not written to biota_properties_int");
            Assert.AreEqual(ExpectedLevel, level.Value, "character level did not round-trip");

            var skills = context.BiotaPropertiesSkill.AsNoTracking()
                .Where(p => p.ObjectId == characterId)
                .ToList();

            Assert.IsTrue(skills.Count > 0, "no trained or specialized skills were persisted");

            // Every starter item should be independently addressable, since they are
            // saved as separate biotas in the same pass as the character.
            var foundPossessions = context.Biota.AsNoTracking()
                .Count(b => possessionIds.Contains(b.Id));

            Assert.AreEqual(possessionIds.Count, foundPossessions, "not every possession was written to the biota table");

            // uint-valued properties are the classic place a SQLite port goes wrong,
            // so check one that Create275HeavyWeapons certainly sets.
            var experience = context.BiotaPropertiesInt64.AsNoTracking()
                .FirstOrDefault(p => p.ObjectId == characterId && p.Type == (ushort)PropertyInt64.TotalExperience);

            Assert.IsNotNull(experience, "total experience was not written to biota_properties_int64");
            Assert.IsTrue(experience.Value > 0, "total experience round-tripped as zero");
        }

        /// <summary>
        /// Removes everything the test wrote.
        /// <para>
        /// In the shard schema, every row that belongs to a biota carries the
        /// owning biota's id in one of a small set of columns: <c>object_Id</c>
        /// for the biota property tables (including book pages, which hang off
        /// the book but carry the biota id), <c>character_Id</c> for the
        /// character tables, and <c>player_Guid</c> for house permissions.
        /// Those are discovered from the model rather than hard-coded, and
        /// <c>biota</c>/<c>character</c> are removed last because they are the
        /// roots.
        /// </para>
        /// </summary>
        private static void Cleanup(uint characterId, List<uint> possessionIds)
        {
            var ids = possessionIds.Concat(new[] { characterId }).ToList();
            var idList = string.Join(",", ids);

            using var context = new ShardDbContext();
            var connection = context.Database.GetDbConnection();

            if (connection.State != ConnectionState.Open)
                connection.Open();

            using var transaction = connection.BeginTransaction();

            void Exec(string sql)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            var biotaIdColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "object_Id", "character_Id", "player_Guid"
            };

            var childTables = new List<(string table, string column)>();

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                var table = entityType.GetTableName();

                if (table == null || table == "biota" || table == "character")
                    continue;

                var column = entityType.GetProperties()
                    .Select(p => p.GetColumnName())
                    .FirstOrDefault(c => c != null && biotaIdColumns.Contains(c));

                if (column != null)
                    childTables.Add((table, column));
            }

            foreach (var (table, column) in childTables)
                Exec($"DELETE FROM {Q(table)} WHERE {Q(column)} IN ({idList})");

            Exec($"DELETE FROM {Q("character")} WHERE id IN ({idList})");
            Exec($"DELETE FROM {Q("biota")} WHERE id IN ({idList})");

            transaction.Commit();
        }

        /// <summary>
        /// MySQL reads "x" as a string literal unless ANSI_QUOTES is set, so the
        /// identifier quote has to follow the active provider.
        /// </summary>
        private static string Q(string identifier)
        {
            return DbProvider.IsSqlite ? $"\"{identifier}\"" : $"`{identifier}`";
        }
    }
}
