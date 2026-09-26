using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;

using log4net;

using ACE.Common;
using ACE.Database.Models.Auth;
using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Creates and populates the three SQLite database files on first run.
    /// <para>
    /// This exists because the MySQL path provisions itself by replaying
    /// <c>DatabaseSetupScripts/Base/*.sql</c>, which are literal <c>mysqldump</c>
    /// output and therefore unparseable by any SQLite driver. Here the auth and
    /// shard schemas are instead generated straight from the EF model with
    /// <c>EnsureCreated()</c>, and the world database is fetched as a
    /// ready-converted SQLite file.
    /// </para>
    /// <para>
    /// The world artifact is the one published by
    /// <a href="https://github.com/amoeba/ace-to-sqlite">amoeba/ace-to-sqlite</a>,
    /// versioned to match ACEmulator/ACE-World-16PY-Patches tags so the world data
    /// always corresponds to the client .dat files in use. It was validated for this
    /// work against a live MySQL import of the matching release: 54/54 tables,
    /// 508/508 columns, identical row counts (43,913 weenies, 6,266 spells).
    /// </para>
    /// <para>
    /// Because the artifact is fetched rather than built from the .dat files, the
    /// update pipelines that keep a MySQL world current do not apply here. See
    /// <c>Program.Main</c>, which logs this at startup. Callers that want an
    /// up-to-date world on SQLite should point <c>WorldDatabaseUrl</c> at a
    /// release matching their .dat files, or rebuild the three files from scratch.
    /// </para>
    /// </summary>
    public static class SqliteBootstrapper
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Access levels required for login. The first account created is promoted to
        /// Admin, and access levels are referenced by the account table's foreign key.
        /// </summary>
        private static readonly (uint level, string name, string prefix)[] AccessLevels =
        {
            (0, "Player",     ""),
            (1, "Advocate",   ""),
            (2, "Sentinel",   "Sentinel"),
            (3, "Envoy",      "Envoy"),
            (4, "Developer",  ""),
            (5, "Admin",      "Admin")
        };

        public static void EnsureDatabases()
        {
            if (!DbProvider.IsSqlite)
                return;

            if (ConfigManager.Config?.Database?.AutoCreate == false)
            {
                log.Info("[SQLITE] AutoCreate disabled; expecting database files to already exist.");
                return;
            }

            var authPath = DbProvider.ResolveSqlitePath(DatabaseKind.Authentication);
            var shardPath = DbProvider.ResolveSqlitePath(DatabaseKind.Shard);
            var worldPath = DbProvider.ResolveSqlitePath(DatabaseKind.World);

            var dir = Path.GetDirectoryName(authPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            log.Info($"[SQLITE] auth  -> {authPath}");
            log.Info($"[SQLITE] shard -> {shardPath}");
            log.Info($"[SQLITE] world -> {worldPath}");

            EnsureAuthDatabase(authPath);
            EnsureShardDatabase(shardPath);
            EnsureWorldDatabase(worldPath);
        }

        private static void EnsureAuthDatabase(string path)
        {
            var created = !File.Exists(path);

            using var context = new AuthDbContext();
            context.Database.EnsureCreated();

            if (created)
                log.Info("[SQLITE] Created ace_auth schema from the EF model.");

            if (!context.Accesslevel.Any())
            {
                foreach (var (level, name, prefix) in AccessLevels)
                    context.Accesslevel.Add(new Accesslevel { Level = level, Name = name, Prefix = prefix });

                context.SaveChanges();
                log.Info($"[SQLITE] Seeded {AccessLevels.Length} access levels.");
            }
        }

        private static void EnsureShardDatabase(string path)
        {
            var created = !File.Exists(path);

            using var context = new ShardDbContext();
            context.Database.EnsureCreated();

            if (created)
                log.Info("[SQLITE] Created ace_shard schema from the EF model.");
        }

        private static void EnsureWorldDatabase(string path)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                log.Info("[SQLITE] World database already present, skipping download.");
                NormalizeWorldDatabaseTypes(path);
                return;
            }

            var url = ConfigManager.Config.Database.WorldDatabaseUrl;

            if (string.IsNullOrWhiteSpace(url))
            {
                log.Warn("[SQLITE] No world database present and no WorldDatabaseUrl configured. " +
                         "ACE will start but cannot load weenies, spells or recipes.");
                return;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = path + ".download";

            log.Info($"[SQLITE] Downloading world database from {url}");
            log.Info("[SQLITE] This is ~130MB and may take a few minutes on first run.");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (!response.IsSuccessStatusCode)
                {
                    log.Error($"[SQLITE] World database download failed: {(int)response.StatusCode} {response.ReasonPhrase}");
                    return;
                }

                var total = response.Content.Headers.ContentLength ?? -1L;
                using var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var output = File.Create(temp);
                input.CopyTo(output);

                if (total > 0)
                    log.Info($"[SQLITE] Downloaded {total / 1048576.0:F1} MB.");
            }

            // A partially-written SQLite file is worse than none: move it into place
            // only once the transfer completed.
            File.Move(temp, path, true);

            log.Info("[SQLITE] World database ready.");

            NormalizeWorldDatabaseTypes(path);
        }

        /// <summary>
        /// Sanity-checks that the world database actually contains data. A world
        /// database that exists but is empty produces a server that boots and then
        /// fails every weenie lookup, which is a confusing failure mode.
        /// </summary>
        public static bool ValidateWorldDatabase(string path)
        {
            if (!File.Exists(path))
            {
                log.Error($"[SQLITE] World database missing at {path}");
                return false;
            }

            try
            {
                using var context = new ACE.Database.Models.World.WorldDbContext();

                if (!context.Database.CanConnect())
                {
                    log.Error("[SQLITE] World database could not be opened.");
                    return false;
                }

                var weenies = context.Weenie.Count();
                var spells = context.Spell.Count();

                log.Info($"[SQLITE] World database contains {weenies:N0} weenies and {spells:N0} spells.");

                if (weenies == 0)
                {
                    log.Error("[SQLITE] World database contains no weenies. Characters cannot be created.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"[SQLITE] World database validation failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Repairs numeric columns that the upstream SQLite conversion declared as TEXT.
        /// <para>
        /// The published world database is produced by round-tripping the MySQL dump
        /// through a TSV intermediate, which loses column types for some columns.
        /// 85 numeric columns across <c>spell</c>, <c>weenie_properties_emote</c> and
        /// <c>weenie_properties_emote_action</c> are declared TEXT while holding
        /// numeric values. EF still reads them, but SQLite orders and compares by
        /// storage class with INTEGER always sorting before TEXT, so a predicate like
        /// <c>WHERE variance &gt; 9</c> matches nothing and <c>ORDER BY min</c> sorts
        /// 10 before 2.
        /// </para>
        /// <para>
        /// An <c>UPDATE ... CAST(...)</c> cannot fix this: TEXT <i>affinity</i> converts
        /// the numeric straight back to text on write, so the cast is silently undone.
        /// The declared column type has to change, and SQLite has no
        /// <c>ALTER COLUMN TYPE</c> -- so each affected table is rebuilt: create a
        /// corrected copy, copy the rows through with a per-column CAST, swap the
        /// tables, and recreate the indexes.
        /// </para>
        /// <para>
        /// The columns to repair are derived from the EF model rather than hard-coded,
        /// so this stays correct as the schema evolves, and it is a no-op once the
        /// types are right.
        /// </para>
        /// </summary>
        public static int NormalizeWorldDatabaseTypes(string path)
        {
            var repaired = 0;

            try
            {
                using var context = new ACE.Database.Models.World.WorldDbContext();
                using var connection = context.Database.GetDbConnection();

                if (connection.State != System.Data.ConnectionState.Open)
                    connection.Open();

                // table -> columns that need rebuilding, with their intended type.
                var affected = new Dictionary<string, Dictionary<string, string>>();

                foreach (var entityType in context.Model.GetEntityTypes())
                {
                    var table = entityType.GetTableName();
                    if (string.IsNullOrEmpty(table))
                        continue;

                    foreach (var property in entityType.GetProperties())
                    {
                        var underlying = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

                        var integral = underlying == typeof(int)    || underlying == typeof(uint)
                                    || underlying == typeof(long)   || underlying == typeof(ulong)
                                    || underlying == typeof(short)  || underlying == typeof(byte)
                                    || underlying == typeof(bool);

                        var floating = underlying == typeof(float) || underlying == typeof(double)
                                    || underlying == typeof(decimal);

                        if (!integral && !floating)
                            continue;

                        var column = property.GetColumnName();

                        var declared = Scalar<string>(connection,
                            $"SELECT type FROM pragma_table_info('{table}') WHERE name = '{column}'");

                        if (!string.Equals(declared, "TEXT", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Refuse to touch a column holding text that is not a number:
                        // CAST('forty' AS INTEGER) is 0, which would corrupt real data.
                        var nonNumeric = Scalar<long>(connection,
                            $"SELECT COUNT(*) FROM \"{table}\" WHERE typeof(\"{column}\") = 'text' " +
                            $"AND (\"{column}\" * 1.0) = 0 " +
                            $"AND TRIM(\"{column}\") NOT IN ('0','0.0','-0','+0','0e0','0.0e0')");

                        if (nonNumeric > 0)
                        {
                            log.WarnFormat("[SQLITE] Skipping {0}.{1}: {2} value(s) are not numeric.", table, column, nonNumeric);
                            continue;
                        }

                        if (!affected.TryGetValue(table, out var columns))
                            affected[table] = columns = new Dictionary<string, string>();

                        columns[column] = integral ? "INTEGER" : "REAL";
                    }
                }

                if (affected.Count == 0)
                {
                    // Say so explicitly. Silently returning would make a boot log
                    // that never mentions the repair indistinguishable from one
                    // where the check never ran.
                    log.Debug("[SQLITE] World database numeric column types are already correct; nothing to rebuild.");
                    return 0;
                }

                foreach (var (table, columns) in affected)
                    repaired += RebuildTable(connection, table, columns);

                if (repaired > 0)
                    log.Info($"[SQLITE] Rebuilt {affected.Count} world table(s) to restore numeric " +
                             $"column types ({repaired:N0} columns affected); the upstream conversion " +
                             $"had declared them TEXT.");
                else
                    log.Warn($"[SQLITE] Identified {affected.Count} world table(s) with TEXT-affinity " +
                              $"numeric columns but could not repair any of them.");
            }
            catch (Exception ex)
            {
                // A failed repair leaves TEXT-affinity numerics in place, which is a
                // correctness risk for numeric predicates but not fatal for a dev
                // server, so warn rather than abort startup.
                log.Warn($"[SQLITE] World database type normalization skipped: {ex.Message}");
            }

            return repaired;
        }

        /// <summary>
        /// Rebuilds <paramref name="table"/> with corrected declared column types.
        /// </summary>
        private static int RebuildTable(System.Data.Common.DbConnection connection,
                                        string table,
                                        Dictionary<string, string> columnTypes)
        {
            var createSql = Scalar<string>(connection,
                $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}'");

            if (string.IsNullOrEmpty(createSql))
                return 0;

            // Capture the index definitions before the table (and its indexes) is dropped.
            var indexSql = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}' AND sql IS NOT NULL";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    indexSql.Add(reader.GetString(0));
            }

            // Rewrite the declared type of each affected column in the original DDL.
            var corrected = createSql;
            foreach (var (column, type) in columnTypes)
            {
                var from = $"\"{column}\" TEXT";
                var to = $"\"{column}\" {type}";

                if (!corrected.Contains(from))
                {
                    log.WarnFormat("[SQLITE] Could not find declared TEXT type for {0}.{1}; skipping.", table, column);
                    continue;
                }

                corrected = corrected.Replace(from, to);
            }

            var temp = table + "__retyped";

            // Rewrite the table name in the DDL for the temporary copy.
            var tempCreate = corrected.Replace($"CREATE TABLE \"{table}\"", $"CREATE TABLE \"{temp}\"",
                                               StringComparison.Ordinal);

            if (tempCreate == corrected)
            {
                log.WarnFormat("[SQLITE] Could not rewrite DDL for {0}; skipping.", table);
                return 0;
            }

            // Column list, in declaration order, casting only the repaired columns.
            var selects = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0);
                    var expression = columnTypes.TryGetValue(name, out var type)
                        ? $"CAST(\"{name}\" AS {type})"
                        : $"\"{name}\"";

                    selects.Add($"{expression} AS \"{name}\"");
                }
            }

            if (selects.Count == 0)
                return 0;

            var columnList = string.Join(", ", selects.Select(s => s[(s.LastIndexOf("AS ", StringComparison.Ordinal) + 3)..]));

            // sqlite_master stores DDL without a trailing semicolon, so terminate each
            // statement explicitly -- otherwise the last one runs into the next keyword.
            var statements = new List<string>
            {
                "PRAGMA foreign_keys=OFF",
                "BEGIN",
                tempCreate,
                $"INSERT INTO \"{temp}\" ({columnList}) SELECT {string.Join(", ", selects)} FROM \"{table}\"",
                $"DROP TABLE \"{table}\"",
                $"ALTER TABLE \"{temp}\" RENAME TO \"{table}\""
            };

            statements.AddRange(indexSql);
            statements.Add("COMMIT");
            statements.Add("PRAGMA foreign_keys=ON");

            try
            {
                Execute(connection, string.Join(";\n", statements) + ";\n");
            }
            catch
            {
                // The connection is shared (Cache=Shared), so a transaction left open
                // here would poison every later caller with
                // "cannot start a transaction within a transaction". Always unwind.
                TryRollback(connection);
                TryExecute(connection, "PRAGMA foreign_keys=ON");
                throw;
            }

            log.DebugFormat("[SQLITE] Rebuilt {0} with {1} corrected column type(s).", table, columnTypes.Count);

            return columnTypes.Count;
        }

        private static void TryRollback(System.Data.Common.DbConnection connection)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            }
            catch
            {
                // Nothing useful to do; the connection is about to be reset or reused.
            }
        }

        private static void TryExecute(System.Data.Common.DbConnection connection, string sql)
        {
            try
            {
                Execute(connection, sql);
            }
            catch
            {
            }
        }

        private static void Execute(System.Data.Common.DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static T Scalar<T>(System.Data.Common.DbConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
                return default;
            return (T)Convert.ChangeType(result, typeof(T));
        }
    }
}
