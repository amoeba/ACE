using System;
using System.IO;
using System.Linq;
using System.Net;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Database.Models.Auth;
using ACE.Entity.Enum;

[assembly: DoNotParallelize]

namespace ACE.Database.Tests
{
    [TestClass]
    public class AccountTests
    {
        private const string TestAccountName = "testaccount1";
        private const string TestPassword = "testpassword1";

        private static AuthenticationDatabase authDb;

        /// <summary>
        /// The id handed out by the most recent run. Kept in a field rather than
        /// hard-coded so the suite does not depend on a freshly created database,
        /// where <c>account</c> is AUTOINCREMENT and the first id is not 1.
        /// </summary>
        private static uint testAccountId;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // copy config.js
            var testDir = AppContext.BaseDirectory;
            var serverDir = FindServerDirectory();
            var configSource = Path.Combine(serverDir, "Config.js");

            if (!File.Exists(configSource))
                configSource = Path.Combine(serverDir, "Config.js.example");

            File.Copy(configSource, Path.Combine(testDir, "Config.js"), true);

            ConfigManager.Initialize();

            // Create the SQLite files from the EF model if they are not there yet,
            // the same way Program.cs and DatabaseManager.Initialize do. A no-op
            // for MySQL, where the schema comes from the SQL setup scripts.
            //
            // Without this the suite only passes against a database something
            // else already provisioned -- on a clean checkout the account tests
            // fail with "no such table: account", which reads like a provider bug
            // rather than a missing fixture.
            SqliteBootstrapper.EnsureDatabases();

            authDb = new AuthenticationDatabase();

            // These tests share one account and depend on running in order, so a
            // leftover account from a previous run would make the first one fail on
            // the unique name. Start from a known state instead.
            DeleteTestAccount();
        }

        [ClassCleanup]
        public static void TestTeardown()
        {
            DeleteTestAccount();
        }

        /// <summary>
        /// The <c>ACE.Server</c> project directory, found by walking up from the test
        /// output until a directory contains <c>ACE.Server/Config.js.example</c>.
        /// <para>
        /// The previous version counted five parent directories, which is only right
        /// for a <c>bin/x64/Debug/net*</c> output layout. A default <c>dotnet test</c>
        /// emits <c>bin/Debug/net*</c> -- four levels -- so the walk overshot to the
        /// repository root and every class here failed with
        /// <c>DirectoryNotFoundException</c> on a path ending in
        /// <c>.../ACE.Server/Config.js.example</c> (note: repo root, not
        /// <c>Source/ACE.Server</c>). That is why CI had to pass
        /// <c>-p:Platform=x64</c> just to keep the arithmetic correct. Looking for
        /// the file instead of counting directories does not care about platform,
        /// build configuration, or how deep the output path is.
        /// </para>
        /// </summary>
        private static string FindServerDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

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

        private static void DeleteTestAccount()
        {
            using var context = new AuthDbContext();

            // Case-folded for the same reason the production lookups are: MySQL's
            // collations are case-insensitive and SQLite's = is not, so a plain ==
            // here would miss a mixed-case leftover that GetAccountByName happily
            // finds. See DbProvider.CaseInsensitiveComparisonRationale.
            var existing = context.Account
                .FirstOrDefault(a => a.AccountName.ToLower() == TestAccountName.ToLower());

            if (existing == null)
                return;

            context.Account.Remove(existing);
            context.SaveChanges();
        }

        [TestMethod]
        public void CreateAccount_GetAccountByName_ReturnsAccount()
        {
            var newAccount = authDb.CreateAccount(TestAccountName, TestPassword, AccessLevel.Player, IPAddress.Parse("127.0.0.1"));
            testAccountId = newAccount.AccountId;

            var results = authDb.GetAccountByName(newAccount.AccountName);
            Assert.IsNotNull(results);
            Assert.AreEqual((uint)AccessLevel.Player, results.AccessLevel);
        }

        [TestMethod]
        public void UpdateAccountAccessLevelToSentinelAndBackToPlayer_ReturnsAccount()
        {
            var accountId = ResolveTestAccountId();
            Account newAccount = new Account();
            newAccount.AccountName = TestAccountName;

            authDb.UpdateAccountAccessLevel(accountId, AccessLevel.Sentinel);
            var results = authDb.GetAccountByName(newAccount.AccountName);
            Assert.IsNotNull(results);
            Assert.AreEqual((uint)AccessLevel.Sentinel, results.AccessLevel);

            authDb.UpdateAccountAccessLevel(accountId, AccessLevel.Player);
            var results2 = authDb.GetAccountByName(newAccount.AccountName);
            Assert.IsNotNull(results2);
            Assert.AreEqual((uint)AccessLevel.Player, results2.AccessLevel);
        }

        [TestMethod]
        public void GetAccountIdByName_ReturnsAccount()
        {
            ResolveTestAccountId();

            Account newAccount = new Account();
            newAccount.AccountName = TestAccountName;

            var id = authDb.GetAccountIdByName(newAccount.AccountName);
            var results = authDb.GetAccountById(id);
            Assert.IsNotNull(results);
            Assert.AreEqual(id, results.AccountId);
            Assert.AreEqual(newAccount.AccountName, results.AccountName);
        }

        [TestMethod]
        public void GetAccountByName_TestPassword_ReturnsMatch()
        {
            var results = authDb.GetAccountByName(TestAccountName);
            Assert.IsNotNull(results);
            Assert.IsTrue(results.PasswordMatches(TestPassword));
        }

        [TestMethod]
        public void GetAccountByName_TestPassword_ReturnsNoMatch()
        {
            var results = authDb.GetAccountByName(TestAccountName);
            Assert.IsNotNull(results);
            Assert.IsFalse(results.PasswordMatches("testpassword2"));
        }

        /// <summary>
        /// These tests are order-dependent by design -- they all exercise the single
        /// account the first test creates. Resolving the id lazily instead of
        /// assuming 1 keeps that dependency explicit without also depending on the
        /// database starting out empty.
        /// </summary>
        private static uint ResolveTestAccountId()
        {
            if (testAccountId != 0)
                return testAccountId;

            testAccountId = authDb.GetAccountIdByName(TestAccountName);
            Assert.AreNotEqual(0u, testAccountId, $"account '{TestAccountName}' does not exist; CreateAccount must run first");

            return testAccountId;
        }
    }
}
