namespace ACE.Common
{
    public class MasterConfiguration
    {
        public GameConfiguration Server { get; set; } = new GameConfiguration();

        public DatabaseConfiguration MySql { get; set; } = new DatabaseConfiguration();

        public SqliteConfiguration Sqlite { get; set; } = new SqliteConfiguration();

        public DatabaseProviderConfiguration Database { get; set; } = new DatabaseProviderConfiguration();

        public OfflineConfiguration Offline { get; set; } = new OfflineConfiguration();

        public DDDConfiguration DDD { get; set; } = new DDDConfiguration();
    }
}
