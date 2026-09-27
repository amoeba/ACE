using System;
using System.Collections.Generic;

namespace ACE.Server.DatabaseUpdate
{
    /// <summary>
    /// The automatic database update pipelines have no SQLite implementation, and
    /// this is where that fact lives.
    /// </summary>
    /// <remarks>
    /// The reason is structural rather than an oversight. The pipelines exist to
    /// replay SQL from <c>Database/Updates</c> and to re-import a <c>mysqldump</c>
    /// when the world release moves. Neither is possible here:
    /// <list type="bullet">
    /// <item>The auth and shard schemas are generated from the EF model by
    /// <c>SqliteBootstrapper</c>, so there is no migration history to replay
    /// against.</item>
    /// <item>The world database arrives as a finished <c>.db</c> file rather than a
    /// dump, and there is no SQLite dialect of a <c>.sql.zip</c> release to
    /// substitute for it.</item>
    /// <item>The scripts are MySQL. A census of all 35 of them finds
    /// <c>ALTER TABLE ... CHANGE COLUMN ... UNSIGNED</c>,
    /// <c>DROP COLUMN</c> across two statements, and identifier quoting that has no
    /// SQLite equivalent. Porting them is a real project, not a dialect swap, and
    /// the ledger would have to move into the database rather than sit beside it in
    /// a text file.</item>
    /// </list>
    /// The methods therefore throw rather than quietly doing nothing. The caller is
    /// expected to check <see cref="IDatabaseUpdateProvider.IsSupported"/> and log
    /// <see cref="UnsupportedReason"/> instead, so reaching one of these is a
    /// programming error and says so.
    /// </remarks>
    public sealed class SqliteDatabaseUpdateProvider : IDatabaseUpdateProvider
    {
        public static readonly SqliteDatabaseUpdateProvider Instance = new SqliteDatabaseUpdateProvider();

        private SqliteDatabaseUpdateProvider() { }

        public bool IsSupported => false;

        public IReadOnlyList<string> UnsupportedReason { get; } = new[]
        {
            "The automatic database update and world database update pipelines are MySQL-only and cannot run against SQLite.",
            "They replay MySQL SQL from Database/Updates through MySqlConnector, and re-import a mysqldump when the world release moves.",
            "The SQLite backend instead generates its auth and shard schema from the EF model and takes the world database as a pre-converted file, so there is nothing for these pipelines to bring up to date.",
            "This world will not receive schema or data patches as ACE is upgraded, and there is no replication or point-in-time recovery.",
            "To pick up a newer world release, point Database.Sqlite.World.WorldDatabaseUrl at a release matching your .dat files, or delete the files in the SQLite database directory and let them be rebuilt on the next start."
        };

        public void CheckForWorldDatabaseUpdate() => throw NotSupported();

        public void AutoApplyWorldCustomizations() => throw NotSupported();

        public void AutoApplyDatabaseUpdates() => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException(
                "The automatic database update pipelines are MySQL-only. Check IDatabaseUpdateProvider.IsSupported and log UnsupportedReason instead of calling this.");
        }
    }
}
