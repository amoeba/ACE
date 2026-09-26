using System;
using System.Collections.Concurrent;

using Microsoft.EntityFrameworkCore;

using log4net;

namespace ACE.Database
{
    public static class DatabaseManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static AuthenticationDatabase Authentication { get; } = new AuthenticationDatabase();

        public static WorldDatabaseWithEntityCache World { get; } = new WorldDatabaseWithEntityCache();

        private static SerializedShardDatabase serializedShardDb;

        public static SerializedShardDatabase Shard { get; private set; }

        public static ShardConfigDatabase ShardConfig { get; } = new ShardConfigDatabase();

        public static bool InitializationFailure = false;

        public static void Initialize(bool autoRetry = true)
        {
            // For SQLite there is no Program_Setup provisioning step, so create and
            // populate the database files here if they are missing. No-op for MySQL,
            // which Program_Setup provisions separately, and a no-op once the files
            // exist, so calling it from both places is harmless.
            SqliteBootstrapper.EnsureDatabases();

            Authentication.Exists(true);

            if (Authentication.GetListofAccountsByAccessLevel(ACE.Entity.Enum.AccessLevel.Admin).Count == 0)
            {
                log.Warn("Authentication Database does not contain any admin accounts. The next account to be created will automatically be promoted to an Admin account.");
                AutoPromoteNextAccountToAdmin = true;
            }
            else
                AutoPromoteNextAccountToAdmin = false;

            World.Exists(true);

            if (!World.IsWorldDatabaseGuidRangeValid())
            {
                log.Fatal("World Database contains instance GUIDs outside of static range which will prevent GuidManager from properly assigning GUIDs and can result in GUID exhaustion prematurely.");
                InitializationFailure = true;
                return;
            }

            var playerWeenieLoadTest = World.GetCachedWeenie("human");
            if (playerWeenieLoadTest == null)
            {
                log.Fatal("World Database does not contain the weenie for human (1). Characters cannot be created or logged into until the missing weenie is restored.");
                InitializationFailure = true;
                return;
            }

            // By default, we hold on to player biotas a little bit longer to help with offline updates like pass-up xp, allegiance updates, etc...
            var shardDb = new ShardDatabaseWithCaching(TimeSpan.FromMinutes(Common.ConfigManager.Config.Server.ShardPlayerBiotaCacheTime), TimeSpan.FromMinutes(Common.ConfigManager.Config.Server.ShardNonPlayerBiotaCacheTime));
            serializedShardDb = new SerializedShardDatabase(shardDb);
            Shard = serializedShardDb;

            shardDb.Exists(true);

            if (DbProvider.IsSqlite && !SqliteBootstrapper.ValidateWorldDatabase(DbProvider.ResolveSqlitePath(DatabaseKind.World)))
                InitializationFailure = true;
        }

        /// <summary>
        /// Provider-neutral description of a database target, for log messages that
        /// previously assumed a MySQL host/port pair.
        /// </summary>
        public static string DescribeTarget(DatabaseKind kind)
        {
            if (DbProvider.IsSqlite)
                return DbProvider.ResolveSqlitePath(kind);

            var cfg = kind switch
            {
                DatabaseKind.Authentication => Common.ConfigManager.Config.MySql.Authentication,
                DatabaseKind.Shard         => Common.ConfigManager.Config.MySql.Shard,
                _                          => Common.ConfigManager.Config.MySql.World
            };

            return $"{cfg.Database} on {cfg.Host}:{cfg.Port}";
        }

        public static bool AutoPromoteNextAccountToAdmin { get; set; }

        public static void Start()
        {
            serializedShardDb.Start();
        }

        public static void Stop()
        {
            if (serializedShardDb != null)
                serializedShardDb.Stop();
        }

        private static readonly ConcurrentDictionary<string, ServerVersion> cachedServerVersions = new();

        public static ServerVersion CachedServerVersionAutoDetect(string database, string connectionString)
        {
            if (!cachedServerVersions.TryGetValue(database, out ServerVersion serverVersion))
            {
                serverVersion = ServerVersion.AutoDetect(connectionString);
                cachedServerVersions[database] = serverVersion;
            }
            return serverVersion;
        }
    }
}
