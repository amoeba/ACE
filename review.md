# Code review — `sqlite` branch vs `master`

Reviewed `sqlite` against `master`. **The branch moved during the review** — five
commits landed while I was reading it (`362b6f09`, `a49b7b76`, `dd0dba17`,
`3b3f2330`, `e879912a`), and the last of those fixed High finding #2 below. This
document is re-baselined against `e879912a` and records which findings are now
resolved.

Verification method: the solution builds clean (0 warnings, 0 errors) and
`Source/ACE.Database.Tests` runs **18/18 green** against real SQLite databases via
the real `Config.js`. Load-bearing claims were checked with a probe built against
Microsoft.Data.Sqlite 9.0.20 (the version this branch references) and by
deserializing `Config.js.example` through the actual `ConfigManager.SerializerOptions`
— not by reading alone.

---

## Resolved

### 1. ~~`Cache=Shared` defeats the WAL concurrency the branch turns on for~~ — **fixed**

`Source/ACE.Common/SqliteConfiguration.cs` shipped
`ConnectionOptions = "Cache=Shared;Foreign Keys=True;Default Timeout=30"`, which
`SQLITE_PRODUCTION.md` §3.2 and `issues.md` item 3 both documented as a P1 defect
prescribing `Cache=Shared` → `Pooling=True`. It was still the default.

Measured, `journal_mode=WAL` in both cases:

| operation | `Cache=Shared` | `Pooling=True` |
|---|---|---|
| write on conn B while conn A holds an open reader | `SQLITE_LOCKED` "database table is locked" after **30,078 ms** | **succeeds in 0 ms** |
| second writer while the first holds `BEGIN IMMEDIATE` | `SQLITE_LOCKED` after 30,090 ms | `SQLITE_BUSY` "database is locked" after 30,107 ms |

Shared cache gives connections one shared *page cache* with shared table locks, so
a reader on one connection stalls a write on another for the entire busy timeout
and then fails with an error `busy_timeout` will not retry — the reader/writer
concurrency WAL exists to provide, and exactly what ACE generates (a `DbContext`
per operation plus several background workers).

**Now:** `Pooling=True`, the property is settable, and the `[JsonIgnore]` is gone so
it is overridable from `Config.js` rather than only by rebuilding. Confirmed that
`ConnectionOpened` still fires once per open on a pooled reopen, so
`SqlitePragmaInterceptor` keeps working. Pinned by
`Source/ACE.Database.Tests/SqliteConnectionStringTests.cs` (7 tests) — verified to
**fail** when `Cache=Shared` is restored and pass when fixed, in both directions.
That suite needs no database and no `Config.js`, so CI runs it.

Two corrections carried into the docs: the 30 s `Default Timeout` *is* consumed
before the failure surfaces (`SQLITE_LOCKED` is not retried, but the timeout elapses
first), and "one physical connection per file" was wrong — connections get distinct
native handles and share only the page cache. `SQLITE_BACKEND.md` and
`SqliteBootstrapper.RebuildTable` both carried that wrong premise; both fixed.

### 2. ~~`WorldDatabaseUrl` documented where nothing reads it~~ — **fixed upstream**

`e879912a` moved it out of the `Sqlite.World` block into `Database`. It used to be
silently discarded (`SqliteDatabaseConfiguration` has no such property and
`UnmappedMemberHandling` defaults to `Skip`), so the documented "set to `""` to
supply your own `ace_world.db`" was a no-op. Verified against the old example by
deserializing it: `Config.Database.WorldDatabaseUrl` came back as the C# default,
not the file's value. No action needed.

### 3. Collapsed brace in a shipped file — **fixed**

`Source/ACE.Server/Program_Setup.cs:457` had the closing `}` on the same line as
the last `PatchDatabase(...)` call.

### 4. Two tests that could not fail — **fixed**

- `DatabaseUpdateProviderTests.Active_MatchesTheConfiguredProvider` asserted
  `DbProvider.Active` equals `DatabaseUpdates.Active is Sqlite ? Sqlite : MySql` —
  and `DatabaseUpdates.Active` is *defined* as `For(DbProvider.Active)`, so the
  right-hand side was `DbProvider.Active` by construction. Now it asserts against
  `ConfigManager.Config.Database.Resolve()` and pins the precondition: with
  `DbProvider.ConfigUnavailable` (new, see #7) it reports inconclusive rather than
  passing for the wrong reason.
- `StartupTests.WorldManager_Initialize` called `StopWorld()` and asserted nothing.
  `Initialize()` returns as soon as it starts its thread, so the test now waits for
  `WorldManager.WorldActive` — set on the first line of `UpdateWorld`, i.e. after
  `PreloadConfigLandblocks` survives — then asserts it goes back down. `WorldActive`
  already existed; no production API was added for the test.

### 5. Provider-selection coverage not running in CI — **partly resolved**

The workflow runs only `ACE.Database.Tests` (`sqlite-backend-ci.yml`), by design
and with reasons documented. The new connection-string tests (finding #1) live
there, so the defect that actually shipped is now covered in CI without touching
the workflow.

`DatabaseUpdateProviderTests` still does not run. The branch's own new comment
(`sqlite-backend-ci.yml`, gap #4) correctly rejects the obvious fix — a filtered
`ACE.Server.Tests` run resolves MySQL rather than SQLite, because
`DbProvider.Active` swallows an uninitialised `ConfigManager` and nothing in a
filtered run calls `ConfigManager.Initialize`. That is finding #7, and it is the
real blocker; see Outstanding below.

### 6. Duplicated `ExecuteScript` and a false "shared" comment — **fixed**

`Program.ExecuteScript` was widened to `internal` documented as "shared with
`MySqlDatabaseUpdateProvider`", which carried its own byte-identical `private
static` copy and called that instead. Deleted the copy; the three call sites now
call `Program.ExecuteScript`, and the comment says it is the single copy.

### 7. `DbProvider` null-safety asymmetry — **fixed**

`Active` wrapped config access in `try/catch` ("ConfigManager not initialised yet
— design-time, tooling, tests") while `ResolveSqlitePath`, `SqliteConnectionString`,
`MySqlConnectionString` and both `kind switch` blocks inside `Configure` dereferenced
`ConfigManager.Config` unguarded.

The concrete consequence was visible in the test run: `AccountTests.ClassCleanup`
executes even when `ClassInitialize` threw, so a setup failure resurfaced as

```
System.NullReferenceException
   at ACE.Database.DbProvider.Configure(DbContextOptionsBuilder, DatabaseKind)
```

instead of the real error. Confirmed against the build before and after — the same
scenario now reports:

```
System.InvalidOperationException: ConfigManager has not been initialized, so no
database connection string can be built. Call ConfigManager.Initialize() before
touching any DbContext.
```

Also added `DbProvider.ConfigUnavailable`, so "the operator asked for MySQL" and
"no config was ever read" are distinguishable. That is what lets a test assert
something real about provider resolution (see #4), and it is the precondition for
fixing #5.

### 8. `EnsureDatabases` only created the auth directory — **fixed**

It derived the directory from `authPath` alone, so if the three paths were
configured into different directories, `EnsureShardDatabase`'s `EnsureCreated()`
would fail with a bare "unable to open database file" because SQLite will not
create a missing parent. Now a small `EnsureDirectoryFor` helper is applied to all
three paths, and the duplicate logic in `EnsureWorldDatabase` was folded into it.

---

## Also fixed (not in the original review)

### The test suite could not run at all locally

`AccountTests`, `WeenieSearchTests` and `ACE.Server.Tests.TestEnvironment` each
located `ACE.Server/Config.js.example` by counting **five** parent directories from
`AppContext.BaseDirectory`. That is only correct for a `bin/x64/Debug/net*` output
layout; a default `dotnet test` emits `bin/Debug/net*` — four levels — so the walk
overshot to the repository root and every class failed with
`DirectoryNotFoundException` on `.../ACE.Server/Config.js.example` (repo root, not
`Source/ACE.Server`). That is the only reason CI had to pass `-p:Platform=x64` just
to keep the arithmetic honest.

All three now walk up looking for the file. Result: `dotnet test
Source/ACE.Database.Tests` goes from **8 failed / 11 passed** to **18/18 green**,
with no platform flag, no MySQL server, no `.dat` files and no 130 MB download.

### Smaller items

- `SqliteBootstrapper`: a failed world-database download left the partial
  `path + ".download"` behind — ~130 MB next to a world database that does not
  exist. Now cleaned up on the failure path.
- `AccountTests.DeleteTestAccount` compared `AccountName` case-sensitively while
  production lookups now fold case, so a mixed-case leftover would be found by
  `GetAccountByName` and missed by cleanup. Now folded, for the same reason
  `DbProvider.CaseInsensitiveComparisonRationale` exists.
- `ShardDatabase.GetSequenceGaps` changed `GetFieldValue<decimal>` → `<long>` on the
  MySQL path without comment. MySqlConnector converts so the path is unaffected;
  the reason is now written down.

---

## Outstanding

### The concurrency soak is still owed

`issues.md` item 3 asked for one alongside the fix, and it is the only thing that
would honestly close the finding: a test that creates real contention between
connections and asserts zero `SQLITE_BUSY`/`SQLITE_LOCKED` under ACE's actual
background-worker load. `SqliteConnectionStringTests` pins the *setting*; it cannot
prove the behaviour under load. `SQLITE_PRODUCTION.md` §3.2 now records this as
still outstanding rather than done.

### `ACE.Server.Tests` remains uncovered in CI

Fixed properly, this needs an explicit config-initialisation step so a filtered run
resolves the provider the checkout actually names, rather than falling back to
MySQL. `DbProvider.ConfigUnavailable` (finding #7) is the hook that makes such a
test able to fail; the test itself still needs writing. This is a design decision
about what CI should guarantee, so it is flagged rather than unilaterally changed.

### Column-count inconsistency — unresolved, needs the artifact

`SqliteBootstrapper.cs:282` says "85 numeric columns"; the workflow comment
(`sqlite-backend-ci.yml:229`) says the published artifact has 84. These may
legitimately differ (the code repairs EF-model columns, not only TEXT-declared
ones) but the pair reads as a contradiction. I could not settle it from the local
world database, because it has already been repaired — only 6 columns remain
TEXT-declared. It needs the unrepaired artifact, which the workflow pins by
SHA256. Left unchanged rather than guessed at.

### World-database download has no integrity check

~130 MB streamed from a GitHub `releases/latest` URL with no size or hash check.
The CI workflow *does* pin a SHA256 (`:38`); production has no equivalent, and
`latest` can move underneath an operator.

### Housekeeping, for a maintainer to decide

- Two empty `Force PR base recompute` commits (`621fea7f`, `1b0f2aac`) — squash.
- `issues.md` and `questions.md` at the repo root are scratch notes, not docs —
  "Outstanding work… None of this is pushed", "Decisions I need from you". They
  should not ride along in the PR. The three `SQLITE_*.md` files (1700 lines) are a
  different matter and read as genuinely finished.
- `ShardDatabaseOfflineTools.cs:993` widens `catch (MySqlException)` to
  `catch (Exception)`, routing non-MySQL failures on the MySQL path into the
  `ALTER TABLE` repair attempt instead of propagating. Probably an improvement, but
  it is an unremarked behaviour change to the MySQL path.
- Pre-existing `command`/`reader` leak in `GetSequenceGaps`
  (`ShardDatabase.cs:89-91`) carried into the refactor.
- `MYSQL_ROOT_PASSWORD: "Password12!"` in the workflow is a throwaway CI
  credential, so informational only.

---

## What's solid

- **The MySQL provider move is faithful.** All seven methods extracted from
  `Program_DbUpdates.cs` at the base commit, diffed against the new file: bodies
  identical, only `IsRunningInContainer`/`CleanupConnection` requalification,
  reordering, and doc comments. The `IDatabaseUpdateProvider` seam is a real
  improvement — an unsupported provider that throws and logs why, rather than
  silently no-op'ing, is the right call.
- **The case-folding fix is complete.** A sweep of `ACE.Database` for remaining
  case-sensitive name comparisons in SQL found none; the 8 `ToLower() ==` sites are
  consistent, and the rationale comment explains the sargability trade-off honestly.
- **The pragma interceptor is well-built** — set-then-read-back individually rather
  than batched, the WAL-vs-readonly distinction is correct, and the tests fail
  loudly if the interceptor isn't wired up.
- **No SQL-injection exposure.** The interpolated table/column names in
  `NormalizeWorldDatabaseTypes` come from the EF model, not input; the `uint`
  interpolations in the gap queries are safe.
- The `RebuildTable` TEXT-affinity repair is a genuinely clever solution to a real
  problem, and its reasoning about why `UPDATE ... CAST` cannot work is correct.
- The workflow's self-critical comments are a real asset. Its gap-4 note is a
  sharper version of my finding #5, and it is right for the reason I gave above.
