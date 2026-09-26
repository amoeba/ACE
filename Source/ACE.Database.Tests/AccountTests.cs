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
            var serverDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "ACE.Server"));
            var configSource = Path.Combine(serverDir, "Config.js");

            if (!File.Exists(configSource))
                configSource = Path.Combine(serverDir, "Config.js.example");

            File.Copy(configSource, Path.Combine(testDir, "Config.js"), true);

            ConfigManager.Initialize();
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

        private static void DeleteTestAccount()
        {
            using var context = new AuthDbContext();

            var existing = context.Account.FirstOrDefault(a => a.AccountName == TestAccountName);

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
