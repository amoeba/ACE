using System.Collections.Generic;

namespace ACE.Server.DatabaseUpdate
{
    /// <summary>
    /// The automatic database update pipelines, as a single provider-shaped seam.
    /// </summary>
    /// <remarks>
    /// <para>Three things happen on startup that are collectively "keeping the
    /// databases current": the world database is checked against the latest
    /// ACE-World-16PY-Patches release and re-imported if it is behind, the
    /// operator's world customization scripts are replayed, and the scripts under
    /// <c>Database/Updates</c> are applied to the auth, shard and world databases.
    /// They are controlled by three keys under <c>Offline</c> in Config.js.</para>
    /// <para>All three are implemented in terms of MySQL: they fetch a
    /// <c>mysqldump</c>, replay SQL through <c>MySqlConnector</c>, and record what
    /// they have applied in an <c>applied_updates.txt</c> ledger. That is a
    /// property of the scripts rather than of the caller, and it is why this is an
    /// interface with one real implementation rather than a config check.</para>
    /// <para>The SQLite backend has no equivalent. Its schema is generated from the
    /// EF model rather than migrated, and its world database arrives as a finished
    /// artifact, so there is nothing for these pipelines to bring up to date. That
    /// is a genuine limitation rather than a missing feature, and it is the main
    /// reason SQLite is scoped to development and private test worlds.</para>
    /// </remarks>
    public interface IDatabaseUpdateProvider
    {
        /// <summary>
        /// Whether the pipelines can run against this provider. When false, the
        /// caller is expected to report <see cref="UnsupportedReason"/> rather than
        /// calling the methods below.
        /// </summary>
        bool IsSupported { get; }

        /// <summary>
        /// Why the pipelines cannot run, one log line per entry. Empty when
        /// <see cref="IsSupported"/> is true.
        /// </summary>
        IReadOnlyList<string> UnsupportedReason { get; }

        /// <summary>
        /// Compares the world database against the latest published patch release
        /// and re-imports it if it is behind. Backs
        /// <c>Offline.AutoUpdateWorldDatabase</c>.
        /// </summary>
        void CheckForWorldDatabaseUpdate();

        /// <summary>
        /// Replays the operator's world customization scripts. Backs
        /// <c>Offline.AutoApplyWorldCustomizations</c>.
        /// </summary>
        void AutoApplyWorldCustomizations();

        /// <summary>
        /// Applies the scripts under <c>Database/Updates</c> that are not yet in the
        /// applied ledger. Backs <c>Offline.AutoApplyDatabaseUpdates</c>.
        /// </summary>
        void AutoApplyDatabaseUpdates();
    }
}
