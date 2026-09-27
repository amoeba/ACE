# SQLite backend

ACEmulator now supports two relational backends behind one config flag. MySQL
remains the default and its behaviour is unchanged; SQLite is a single-file
alternative for local development that needs no database server.

```js
// Config.js
"Database": { "Provider": "sqlite" }   // or "mysql" (default)
```

| | MySQL | SQLite |
|---|---|---|
| Config section | `MySql` | `Sqlite` |
| Package | `Pomelo.EntityFrameworkCore.MySql` | `Microsoft.EntityFrameworkCore.Sqlite` |
| Provisioning | `DatabaseSetupScripts/*.sql` | created from the EF model at first boot |
| World database | imported from a `.sql` dump | pre-converted `.db` file |
| Concurrent writers | yes | serialized (`busy_timeout=30s`) |
| Target | production | local development |

---

## 1. Design

### 1.1 One switch, one resolution point

`DbProvider` (`Source/ACE.Database/DbProvider.cs`) is the only place that knows
which backend is active. It reads `ConfigManager.Config.Database.Resolve()` and
exposes `Active`, `IsSqlite`, `IsMySql`, and `ResolveSqlitePath(kind)`.

All three `DbContext` subclasses used to hard-code their provider inside
`OnConfiguring` with a Pomelo-specific fluent call:

```csharp
// before
protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    var config = ConfigManager.Config.MySql.Shard;
    optionsBuilder.UseMySqlServer(...).HasCharSet("utf8mb4");
}
```

`OnConfiguring` now delegates to `DbProvider.Configure(...)`, which picks
`UseMySqlServer` or `UseSqlite` from the same flag. Switching backends is a
config change, not a code change.

Stripping the fluent calls also removed **363 lines** of `HasCharSet`,
`EnableDetailedErrors`, `HasColumnType`, index and collation configuration
across the three contexts — 547 deletions against 111 insertions, a net loss of
436 lines. None of it was reachable on SQLite, and the Pomelo-only pieces are
now expressed once, in `DbProvider.Configure`.

### 1.2 Provisioning

MySQL is provisioned by replaying `DatabaseSetupScripts/*.sql` against a live
server. That has no SQLite equivalent, so `SqliteBootstrapper`
(`Source/ACE.Database/SqliteBootstrapper.cs`) does it at boot instead:

- **auth** and **shard** — `EnsureCreated()` against the existing EF model. The
  schema is derived from the same classes MySQL's SQL scripts were written
  against, so drift is impossible by construction. The 6 `accesslevel` rows are
  seeded on first creation.
- **world** — 54 tables / 2.8M rows / 43,913 weenies. Downloading and
  converting that at boot is not reasonable, so SQLite uses the pre-converted
  `ace_world.db` from [`amoeba/ace-to-sqlite`](https://github.com/amoeba/ace-to-sqlite),
  the same artifact that backs <https://acedb.treestats.net>. It is downloaded
  once (~137 MB) from `Database.Sqlite.World.WorldDatabaseUrl` and then reused.
  Point that setting at `""` to supply your own file.

`EnsureDatabases()` is called from two places on purpose: `Program.Main` (after
`ConfigManager.Initialize()`, because the offline-maintenance block below it
queries the shard schema before `DatabaseManager` exists) and
`DatabaseManager.Initialize` (so the test suites and any embedding host get the
same behaviour). It is idempotent and a no-op under MySQL.

`PRAGMA integrity_check` returns `ok` on all three files after provisioning.

### 1.3 Connection tuning

`SqlitePragmaInterceptor` (`Source/ACE.Database/SqlitePragmaInterceptor.cs`)
applies four pragmas to every connection:

| pragma | why |
|---|---|
| `journal_mode=WAL` | ACE opens a `DbContext` per operation and runs several background workers. WAL lets readers use a snapshot instead of blocking on the writer. |
| `busy_timeout=30000` | Default is 0, which surfaces `SQLITE_BUSY` under normal concurrent use. |
| `synchronous=NORMAL` | WAL companion. Crash-safe; only risks the last few transactions on power loss. Acceptable for a dev database. |
| `foreign_keys=ON` | SQLite ignores FKs unless asked. Affects auth/shard only — the pre-converted world DB carries no FKs. |

The connection string also sets `Cache=Shared` and `Default Timeout=30`.

> `Cache=Shared` means all contexts in a process share **one** physical
> connection. A failed transaction therefore has to be rolled back explicitly or
> the connection stays in a failed state for the next user. This caused a real
> bug during development; see §3.2.

### 1.4 Provider-specific SQL

ACE has some raw SQL that is not portable. Each case is handled explicitly
rather than by string-matching the provider inline:

| Site | MySQL | SQLite |
|---|---|---|
| `ShardDatabase.GetSequenceGaps` | original, session user-variables | `LAG()` window function |
| `ShardDatabase.GetEstimatedBiotaCount` | InnoDB row estimate | `COUNT(*)` |
| world-DB updater | runs | unsupported, see below |
| `AutoApplyDatabaseUpdates` | runs | unsupported, see below |
| `DatabaseManager` patch pipeline | runs | unsupported, see below |

The three update pipelines are not a config check at the call site. They are
`IDatabaseUpdateProvider` in `ACE.Server/DatabaseUpdate`, with
`MySqlDatabaseUpdateProvider` holding the original MySQL implementation
unchanged and `SqliteDatabaseUpdateProvider` reporting `IsSupported == false`
and the reason. `Program.Main` asks the resolved provider once and logs the
reason instead of calling it, so the omission is visible in the boot log
rather than silent, and only when the operator had actually enabled one of
the `Offline` flags.

The SQLite implementation's three methods throw rather than returning
quietly. The caller is expected to check `IsSupported`, so reaching one is a
wiring bug, and a no-op that reported success is the failure mode the class
exists to prevent.

---

## 2. What had to change, and why

### 2.1 `GetSequenceGaps`

The single most important query in the port. `DynamicGuidAllocator` seeds itself
from it, so every player guid flows through it.

The MySQL version walks ids in order while accumulating state in `@rownum`,
seeded to `MIN(id) - 1`. SQLite has no session variables whose value is
observable across rows in a `SELECT` list, so the port uses `LAG()` to supply the
previous id and a window `SUM()` for the running total:

```sql
WITH ordered AS (
  SELECT id,
         LAG(id, 1, (SELECT MIN(id) - 1 FROM biota WHERE id > {min})) OVER (ORDER BY id) AS prev
  FROM biota WHERE id > {min}
),
gaps AS (
  SELECT prev + 1 AS gap_starts_at,
         id       AS gap_ends_at_not_inclusive,
         SUM(id - (prev + 1)) OVER (ORDER BY id) AS running_total_available_ids
  FROM ordered
  WHERE prev + 1 <> id
)
SELECT gap_starts_at, gap_ends_at_not_inclusive FROM gaps
-- WHERE running_total_available_ids < {limit}   (when a cap is set)
```

Both versions report gaps *strictly between* existing ids: the leading gap
before the first id and the trailing space are not reclaimable by this query and
are excluded by both. In the MySQL version this falls out of `@rownum` being
seeded to `MIN(id)-1` (so the first row always yields `gap_ends_at = 0`, filtered
by `!= 0`); in the SQLite version `LAG`'s default argument does the same job.

### 2.2 Case-insensitive name comparison

MySQL's default collations (`utf8mb4_general_ci` and friends) are
case-insensitive. SQLite's `=` on TEXT is not. That is not a cosmetic
difference:

```sql
-- weenie.class_Name holds 'orb'
SELECT class_Id FROM weenie WHERE class_Name = 'Orb';   -- MySQL: 2366
                                                           -- SQLite: no rows
```

`WorldObjectFactory.CreateNewWorldObject("Orb")` therefore returned null on
SQLite and the caller dereferenced it. The same class of bug would have hit
`GetAccountByName` (logging in as `Bob` against a stored `bob`) and
`IsCharacterNameAvailable` (two characters differing only by case).

The four affected predicates now compare `column.ToLower() == value.ToLower()`,
which restores MySQL's semantics on SQLite and is a no-op on MySQL. It has to
be written out at each call site rather than hidden in a helper: EF Core
translates a fixed set of methods inside a `Where` predicate and rejects
anything else.

Cost: the comparison is no longer sargable, so these lookups scan. The
alternative — declaring the columns `COLLATE NOCASE` — is not available for the
pre-converted world database, so one mechanism everywhere beats a mix of two.
SQLite's built-in `lower()` folds ASCII only, which covers every account,
character and weenie name ACE accepts.

### 2.3 The world database's numeric columns

The upstream MySQL→SQLite conversion gave some numeric columns **TEXT**
affinity. Under SQLite that is worse than it sounds: `UPDATE ... SET c =
CAST(c AS INTEGER)` does not change a column's affinity, so the rebuild writes
back the same TEXT the column already has and the repair silently does nothing.
`typeof()` kept reporting `text` and comparisons against numbers misbehaved.

Fix: `NormalizeWorldDatabaseTypes` in `SqliteBootstrapper` rebuilds the
affected tables. It derives the expected types from the EF model, so it does not
depend on a hand-maintained list, and it refuses to convert a column if any
non-NULL text value is not numeric — `CAST('forty' AS INTEGER)` is `0`, which
would corrupt the data rather than reveal the problem.

Proof it works, from the development run:

```
before:  SELECT COUNT(*) FROM spell WHERE variance > 9  ->        6
after:   SELECT COUNT(*) FROM spell WHERE variance > 9  ->      647
```

`typeof()` now reports `integer`/`real`, `integrity_check` is `ok`, row counts
are preserved (`spell` 6,266, `weenie_properties_emote` 71,756,
`weenie_properties_emote_action` 154,306) and both indexes survived.

Two implementation traps, both found by running it:

- index DDL emitted by SQLite needs an explicit trailing `;`, otherwise it gets
  glued onto `COMMIT` and the transaction is never committed;
- with `Cache=Shared` a failed transaction must be rolled back, or the shared
  connection is left broken for the next caller.

---

## 3. Validation

### 3.1 Schema parity against the live MySQL baseline

Compared table-for-table and column-for-column against the MySQL container:

| database | tables | columns | mismatches |
|---|---|---|---|
| shard | 40 / 40 | 271 / 271 | 0 type-family mismatches |
| world | 54 / 54 | 508 / 508 | 0 |
| auth | 2 / 2 | 18 vs 20 | see below |

The two extra auth columns are `create_I_P_ntoa` and `last_Login_I_P_ntoa`.
They are expected: they are not in the EF model, nothing in C# references them,
and they were retired by
`Database/Optional/Authentication/2019-06-10-00-Drop_ntoa_Fields_From_Account_Table.sql`.
`EnsureCreated()` cannot emit columns the model does not declare.

### 3.2 World database differential: every cell

All 54 tables, **2,827,409 rows × 508 columns, every cell identical to MySQL.**

Two representation differences had to be normalised for the comparison to be
meaningful:

- `DATETIME` — MySQL renders `2005-02-09T10:00:00`, SQLite `2005-02-09 10:00:00`;
- `BIT(1)` — the MySQL CLI emits a raw control byte, and a NUL does not survive
  batch mode, so both sides were read through the same quoting.

### 3.3 `GetSequenceGaps` differential

`verify-sequence-gaps.sh` runs both implementations over the same table and
compares results. Final run:

```
identical: 10   expected divergence (cap semantics): 3   failed: 0
```

The 3 divergences are deliberate — see §4.

### 3.4 Test suites

Both providers run the same suites and pass:

| suite | MySQL | SQLite |
|---|---|---|
| `ACE.Database.Tests` | 7 / 7 | 7 / 7 |
| `ACE.Server.Tests` | 13 / 13 | 13 / 13 |

`CharacterCreationTests.CreateCharacter_PersistsToShard_AndReadsBackFromDatabase`
is the new end-to-end test and is the broadest single exercise of the shard
write path available without a game client. It allocates a guid through
`GuidManager` (so it covers the gap query), runs the full weenie→player
conversion across the ~2,500 lines of ACE.Adapter mapping, persists the
character and all starter gear through `AddCharacterInParallel`, and then reads
the rows back through a **fresh `ShardDbContext`** so the assertions hit the
database rather than the in-memory biota cache in front of it. It checks the
`biota` and `character` rows, the name in `biota_properties_string`, the level
in `biota_properties_int`, trained skills, total experience as a `uint`, and
that every starter item is independently addressable. It then removes
everything it wrote, verified by the shard returning to 0 rows in all 40 tables,
so the suite is repeatable.

`TestEnvironment` runs the process-wide initialisation exactly once, in
`Program.cs` order, since `DatabaseManager`, `WorldManager` and the managers
they tick from a background thread are not re-entrant.

### 3.5 Server boot

```
[SQLITE] auth  -> .../db/ace_auth.db
[SQLITE] shard -> .../db/ace_shard.db
[SQLITE] world -> .../db/ace_world.db
[SQLITE] World database contains 43,913 weenies and 6,266 spells.
Database provider is SQLite; skipping MySQL world/database patch pipeline.
Binding ConnectionListener to 0.0.0.0:9000 / 9001
[CHAT][AUDIT] [SYSTEM] ... "World is now open"
error/fatal/exception count: 0
```

43,913 weenies and 6,266 spells match MySQL exactly. A MySQL boot of the same
build was also verified clean.

---

## 4. Known gaps and deliberate divergences

1. **`GetSequenceGaps` cap.** The MySQL `limitAvailableIDsReturned` cap is dead
   code under MySQL 8.0: the outer `WHERE` reads `@available_ids` as `0` on
   every row, so `@available_ids < limit` is always true. Verified empirically.
   The SQLite rewrite *does* honour the cap. The two agree on all uncapped
   queries; they differ only when reclaimable ids exceed the limit, and the
   default is `uint.MaxValue` so this is not reachable in practice. The
   divergence is intentional — the SQLite version implements what the code was
   clearly trying to express.

2. **Name comparisons are no longer indexed.** See §2.2.

3. **No world-DB migration path.** `AutoUpdateWorldDatabase` and
   `AutoApplyDatabaseUpdates` are MySQL-only, and the SQLite implementation
   refuses rather than no-op'ing, so a mistake in the dispatch is loud. Applying
   `DatabaseSetupScripts/*.sql` to SQLite would need a translation layer; the
   SQLite path instead takes the world database as a pre-converted artifact, so
   it starts at whatever version that artifact is. Custom
   `Database/Optional/World/*.sql` will not be applied.

4. **No foreign keys in the pre-converted world DB.** `foreign_keys=ON` applies
   to auth/shard, which ACE creates. The world artifact has none to enforce.

5. **WAL files.** `-wal` and `-shm` siblings appear next to each `.db`. They are
   normal. Stop ACE before moving a database, and move the `.db` and both
   siblings together — do **not** `cp` a live database, and see
   [`SQLITE_SETUP.md`](SQLITE_SETUP.md) §8 for the safe backup command.

6. **One writer at a time.** SQLite serialises writers. `busy_timeout` absorbs
   brief contention, but sustained write concurrency will queue. Fine for a dev
   server; MySQL for anything else.

---

## 5. Running it

### SQLite

```bash
cd Source
dotnet build ACE.Server/ACE.Server.csproj -c Release
cp ACE.Server/Config.js.example ACE.Server/Config.js
# in Config.js: "Database": { "Provider": "sqlite" }
cd ACE.Server/bin/<config>/<tfm>
ACE_NONINTERACTIVE_SETUP=true dotnet ACE.Server.dll
```

First boot creates `db/ace_auth.db` and `db/ace_shard.db` and downloads
`db/ace_world.db`. Point `Sqlite.World.Database` at absolute paths if you run
from somewhere other than the output directory.

To take the bootstrap out of the picture entirely — supply all three `.db` files
yourself — set:

```js
"Database": { "Provider": "sqlite", "AutoCreate": false }
```

which makes `EnsureDatabases` return immediately and log
`[SQLITE] AutoCreate disabled; expecting database files to already exist.`

### MySQL

Unchanged. Set `"Provider": "mysql"` (or delete the `Database` block — MySQL is
the fallback for any unrecognised value) and provision via
`DatabaseSetupScripts/`.

### Building on Apple Silicon

The csproj pins `<Platforms>x64</Platforms>`, so an x64 artifact will not run on
arm64 macOS. Build with `-p:Platform=arm64` rather than editing the csproj:

```bash
dotnet build ACE.Server/ACE.Server.csproj -c Release -p:Platform=arm64
```

### Tests

```bash
# provider comes from Source/ACE.Server/Config.js, which the suites copy
# into their own output directory
dotnet test Source/ACE.Database.Tests/ACE.Database.Tests.csproj -c Release
dotnet test Source/ACE.Server.Tests/ACE.Server.Tests.csproj  -c Release
```

`ACE.DatLoader.Tests` needs the client `.dat` files and is unrelated to the
database work:

```bash
ACE_DAT_PATH=~/Downloads/ac-updates dotnet test Source/ACE.DatLoader.Tests/...
```

`DatTests.LoadCellDat_NoExceptions` fails against some `.dat` versions: it
asserts an exact 805,003 records while the current file holds 805,348. The
loader is fine — `UnpackCellDatFiles_NoExceptions` passes on the same file, and
the server reads it without complaint. The constant is simply stale.

---

## 6. Files

### New

| file | role |
|---|---|
| `Source/ACE.Database/DbProvider.cs` | provider resolution, connection strings, SQLite path resolution |
| `Source/ACE.Database/SqliteBootstrapper.cs` | schema creation, world-DB download, numeric-type repair, validation |
| `Source/ACE.Database/SqlitePragmaInterceptor.cs` | WAL / busy_timeout / synchronous / foreign_keys |
| `Source/ACE.Common/SqliteConfiguration.cs` | `DatabaseProvider` enum + `Sqlite` / `Database` config POCOs |
| `Source/ACE.Server.Tests/TestEnvironment.cs` | one-time process-wide test initialisation |
| `Source/ACE.Server.Tests/CharacterCreationTests.cs` | end-to-end character creation + read-back |

### Modified

| file | change |
|---|---|
| `Models/Auth/AuthDbContext.cs`, `Models/Shard/ShardDbContext.cs`, `Models/World/WorldDbContext.cs` | `OnConfiguring` delegates to `DbProvider.Configure`; 363 Pomelo-only fluent calls removed |
| `Source/ACE.Server/Program.cs` | bootstrap call; provider guards on the MySQL patch pipeline |
| `Source/ACE.Database/DatabaseManager.cs` | bootstrap call; world-DB validation; `DescribeTarget` |
| `Source/ACE.Database/ShardDatabase.cs` | `GetSequenceGaps` split into MySQL/SQLite SQL builders; `GetEstimatedBiotaCount`; provider-neutral log messages; case-insensitive name lookups |
| `Source/ACE.Database/WorldDatabase.cs` | case-insensitive weenie-name lookup |
| `Source/ACE.Database/AuthenticationDatabase.cs` | case-insensitive account lookups |
| `Source/ACE.Database/ShardDatabaseOfflineTools.cs` | palette-column check no longer assumes `MySqlException` |
| `Source/ACE.Common/MasterConfiguration.cs` | `Sqlite` + `Database` properties |
| `Source/ACE.Database/ACE.Database.csproj` | `Microsoft.EntityFrameworkCore.Sqlite` |
| `Source/ACE.Server/Config.js.example` | documented `Database` / `Sqlite` blocks |

### Test hygiene (pre-existing problems, not port regressions)

- `StartupTests` was missing `using ACE.DatLoader`, the `CodePagesEncodingProvider`
  registration, and `DatManager.Initialize`, so `WorldManager_Initialize`
  dereferenced a null `CellDat`. Routed through `TestEnvironment`.
- `AccountTests` hard-coded `accountId = 1` and never deleted the account it
  created, so it only passed against a virgin database. Now resolves the id and
  cleans up in `ClassInitialize`/`ClassCleanup`.
- `AccountTests` and `WeenieSearchTests` used a Windows-only `..\..\` config
  path. Now `Path.Combine`.
- `DatTests` hard-coded `C:\Turbine\Asheron's Call\`. Now `ACE_DAT_PATH`, with
  the Windows path as the default.

---

## 7. Branch

`prototype/sqlite-backend`, from `master` @ `47edade3`. The work is uncommitted
so it can be reviewed as a single diff:

```bash
git diff master
git status --porcelain      # the 6 new files
```
