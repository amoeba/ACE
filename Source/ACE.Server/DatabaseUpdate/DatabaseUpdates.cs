using ACE.Common;
using ACE.Database;

namespace ACE.Server.DatabaseUpdate
{
    /// <summary>
    /// Resolves the update pipeline implementation for a database provider.
    /// </summary>
    public static class DatabaseUpdates
    {
        /// <summary>
        /// The implementation matching the provider selected in Config.js. MySQL is
        /// the default, so an absent or unrecognised value resolves to it.
        /// </summary>
        public static IDatabaseUpdateProvider Active => For(DbProvider.Active);

        /// <summary>
        /// The implementation for a given provider. Separated from
        /// <see cref="Active"/> so it can be exercised without a Config.js.
        /// </summary>
        public static IDatabaseUpdateProvider For(DatabaseProvider provider)
        {
            return provider switch
            {
                DatabaseProvider.Sqlite => SqliteDatabaseUpdateProvider.Instance,
                _                         => MySqlDatabaseUpdateProvider.Instance
            };
        }
    }
}
