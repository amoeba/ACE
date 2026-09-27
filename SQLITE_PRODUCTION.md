# Why SQLite is the development path, not the production path

Supporting analysis for the decision that
[`SQLITE_BACKEND.md`](SQLITE_BACKEND.md)'s SQLite backend is scoped to local
development and private test worlds, and that MySQL remains the supported
target for public shards.

This is deliberately **not** a roadmap to running a public shard on SQLite. It
does two things instead: it establishes that the reasons for that scoping are
*not* performance reasons, which is the part that is easy to get wrong, and it
records the defects and limitations that still need to be honest about.

Every claim below is either measured on this machine (Apple Silicon, APFS/NVMe)
or quoted from the SQLite/EF Core documentation. Where I did not measure
something, I say so.

**Summary of the position.** SQLite performs comfortably within ACE's access
pattern — §2 measures it and the single-writer constraint turns out not to
bind. MySQL stays the primary target for three reasons that have nothing to do
with speed:

1. **No patch pipeline.** `Database/Updates/` is applied automatically on MySQL
   and has no SQLite equivalent, so a long-running world silently drifts behind
   every schema and data change ACE ships. A development world is rebuilt often
   enough that this does not matter; a shard is not.
2. **No replication or point-in-time recovery.** A second copy is the one thing
   SQLite cannot give you on a single box, and it is the thing a shard most
   needs.
3. **The world database is a fetched artifact, not a built one.** The MySQL path
   imports a first-party dump and keeps it current; the SQLite path takes a
   pre-converted file and cannot update it in place.

Independently of that scoping, three defects in the current implementation
should be fixed before this is offered upstream, because they make a *development*
world quietly misbehave: a connection mode that defeats WAL (§3.2), durability
left at a development setting (§3.5), and relative paths resolved against the
working directory rather than the executable (§3.10). A fourth — a swallowed
pragma failure (§3.1) — is fixed, and §3.1 records what it took.

---

## 1. Sources

- [Gotchas with SQLite in Production](https://blog.pecar.me/sqlite-prod/) —
  Anže Pečar, Jul 2024. The best short treatment of the failure modes. The
  numbered subsections in this document that cite it refer to the *article's*
  section numbers, which are shorter than ours.
- [SQLite in Production](https://queryplane.com/blog/sqlite-in-production/) —
  QueryPlane, Feb 2026. Broader survey covering tooling (Litestream, LiteFS),
  benchmarks, and where SQLite genuinely breaks down.
- [SQLite documentation](https://www.sqlite.org/pragma.html) and the
  `Microsoft.Data.Sqlite` XML docs, for behaviour claims.

---

## 2. Is SQLite even the right shape for ACE?

Before the defects: the workload. A database is a good fit for SQLite when the
access pattern is single-process, the data is modest, and reads dominate. Let
us check each against measurements.

**One process, one machine.** ACE is a game server. One `ACE.Server.dll`, one
box. This is the single most favourable fact and it is not a coincidence that
most of the SQLite-in-production case studies are single-process applications.

**Three independent files, so three independent writer locks.** `ace_auth`,
`ace_shard` and `ace_world` are separate files, so SQLite's "one writer at a
time" limit applies *per file*. Authentication writes are rare; the world
database is never written at all (§4.1). Essentially all writes are shard
writes.

This is a structural advantage, not a coincidence of naming. [pecar §4](https://blog.pecar.me/sqlite-prod/)
identifies table-splitting as the actual remedy for SQLite's write contention:
"you can split your tables across multiple databases. This way, each database
can write to its set of tables in parallel." ACE's auth/shard/world split is
that pattern, arrived at independently.

**The write rate is far below the limit.** Measured on the real 640 KB shard
database, 5,000-row transactions, median of 7 reps after warmup:

| `synchronous` | median | throughput |
|---|---|---|
| `OFF` | 7.5 ms | 662,910 rows/s |
| `NORMAL` | 7.5 ms | 670,440 rows/s |
| `FULL` | 7.8 ms | 644,565 rows/s |

And the ceiling if ACE committed *one row at a time* — which it does not, but
this is the pessimistic case:

| | per commit | ceiling |
|---|---|---|
| one row per transaction | 0.026 ms | ~37,866 commits/s |
| 5,000 rows per transaction | 0.001 ms/row | ~817,044 rows/s |

Batching is **22× cheaper per row**, which validates ACE's existing write shape
(`SaveBiotasInParallel`, `AddCharacterInParallel`). A shard that logged in 100
players simultaneously would consume a few percent of that ceiling for a
fraction of a second. **The single-writer constraint is not a real constraint
for ACE.**

**The world database is large but read-only, with a small hot set.** 137 MB,
44k weenies, and the per-table breakdown is the interesting part:

| table | rows | on disk |
|---|---|---|
| `landblock_instance` | 365,195 | 29.5 MB |
| `weenie_properties_int` | 505,423 | 7.5 MB |
| `weenie_properties_float` | 311,562 | 5.3 MB |
| `weenie_properties_string` | 82,791 | — |
| `weenie` | 43,913 | — |

So the read pattern is: one ~30 MB bulk read of landblock config at startup, then
small random lookups. The hot tables fit in RAM many times over. This has a
direct consequence for the "huge pages" question in §4.2.

**Conclusion on fit: good.** A shard server is a textbook SQLite workload. The
problems below are problems in *our* code, not with SQLite for this
application. Which is the encouraging finding — the difficult part (picking the
right database) is right.

**This is worth stating plainly, because it is the opposite of the usual
assumption.** SQLite is not being kept as the development option because it is
slow or unscalable here. On this workload it is comfortably fast enough, and
the single-writer limitation that usually sinks SQLite does not bind. It is the
development option because of patching, replication, and artifact trust — §3.3,
§3.8 and §3.9. Anyone revisiting that decision should start from those three,
not from performance.

---

## 3. Findings, by severity

### 3.1 fixed — pragma failures are no longer silently swallowed

**Status: fixed.** `SqlitePragmaInterceptor` now applies each pragma separately,
reads its value back, and treats a `journal_mode` that is not `wal` as fatal. See
[§3.1.1](#31-what-was-wrong-and-what-changed) below for the details, including a
second bug the fix itself introduced and how the tests catch both.

#### 3.1 What was wrong, and what changed

The original implementation ran the pragmas like this:

```csharp
try
{
    command.CommandText = Pragmas;   // journal_mode, busy_timeout, synchronous, foreign_keys
    command.ExecuteNonQuery();
}
catch { }                             // <-- empty
```

Two independent problems.

**The empty `catch` hides real errors.** Reproduced:

```
$ sqlite3 "file:t.db" "PRAGMA journal_mode=WAL;"     # t.db not writable
Error in 2nd command line argument: attempt to write a readonly database
```

So a database that ACE cannot write to produced an error that we discarded
without a log line, and the server started. The first pragma in the batch is the
one that fails, so the state afterwards is unknown: possibly no WAL, possibly
`busy_timeout=0`, possibly `synchronous=FULL`, and the boot log is
indistinguishable from a correct run.

`busy_timeout=0` is the dangerous part. It is SQLite's default, and it means the
first collision between two writers is an immediate `SQLITE_BUSY` rather than a
30-second wait. Under MySQL that error class is handled and retried by InnoDB;
under SQLite it surfaces as a player-visible failure.

**The result of each pragma was never inspected.** `PRAGMA journal_mode` is
queryable — it returns the mode actually in effect. We set it and assumed.

This mattered most exactly where SQLite is most dangerous.
[pecar §3](https://blog.pecar.me/sqlite-prod/) is blunt about it: network
filesystems "usually don't have the required lock-level guarantees, which means
you can end up with a corrupt database." The deployments that trigger a WAL
failure — NFS, SMB, some container overlay mounts, a read-only mount — are the
same deployments where running on rollback journalling with a zero busy timeout
will corrupt or wedge. Failing loudly is the entire mitigation.

**What it does now.** Each pragma is set and then queried with a separate
statement; `journal_mode` not reading back as `wal` throws with the database path
and the actual mode, and the other three report a warning.

#### 3.1.1 Two subtleties the fix had to get right

**A pragma's SET form does not reliably return the resulting value.** The first
version of the fix read back the value returned by the SET statement itself. That
verifies `journal_mode` and `busy_timeout`, which do return a row — and silently
verifies nothing for the other two, because `PRAGMA synchronous=NORMAL;` and
`PRAGMA foreign_keys=ON;` return **no rows at all**:

```
$ sqlite3 c.db "PRAGMA synchronous=NORMAL; SELECT 'synchronous='||(SELECT * FROM pragma_synchronous);"
1
```

The unit tests passed, because they read the pragmas from the connection
themselves and the pragmas genuinely were in effect. Only a real server boot
showed the damage: **3072 identical warnings** in a single boot, one per
connection open, because the read-back returned null and was reported as
possibly-not-in-effect. Fixed by always querying with a separate statement.

**Warnings are deduplicated per database and pragma.** ACE opens a fresh
connection per operation, so a genuine misconfiguration would otherwise repeat
the same line thousands of times and bury the one line that matters. Bounded at
four pragmas times three databases.

#### 3.1.2 The check is on the journal mode, not on writability

A subtlety worth recording, because it is easy to "fix" in the wrong direction.
A database **already in WAL mode** can be opened read-only and read from
perfectly well — reads touch neither the main file nor the `-wal`. Verified:

```
non-WAL database, read-only connection   -> journal_mode=WAL fails, SQLITE_READONLY (8)
already-WAL,     read-only connection   -> journal_mode=WAL succeeds, reads work
```

Rejecting the second case would refuse to start against a world database that is
working exactly as intended. So the interceptor checks the *mode*, not
writability, and `SqlitePragmaTests` pins both cases.

#### 3.1.3 Test coverage

Four tests in `ACE.Database.Tests/SqlitePragmaTests.cs`, each verified to fail
against the old behaviour:

| Test | What it pins |
|---|---|
| `AppliesAndVerifiesPragmas` | all four pragmas actually in effect after an open |
| `PragmaThatCannotBeApplied_Throws` | a read-only connection to a non-WAL database throws, and the message names both the pragma and the file |
| `AlreadyWalOnAReadOnlyConnection_IsAccepted` | a working read-only world database is not rejected |
| `HealthyDatabase_LogsNoWarnings` | a healthy open produces no warning at all |

The last one exists because the bug in §3.1.1 was invisible to the other three.
It attaches a log4net `MemoryAppender` to the interceptor's logger, logs a
sentinel first so that a mis-wired appender fails loudly rather than passing
vacuously, and asserts the sentinel is the *only* warning captured.

### 3.2 P1 — `Cache=Shared` disables the thing WAL is for

`Source/ACE.Common/SqliteConfiguration.cs` builds:

```csharp
public string ConnectionOptions { get; } = "Cache=Shared;Foreign Keys=True;Default Timeout=30";
```

`SqliteConfiguration.cs:89`, the `ConnectionOptions` property:

`Cache=Shared` puts every connection to a given file into one shared cache with
**one set of table-level locks**. The consequences:

- Readers block writers at *table* granularity, and they block them for the
  duration of the read transaction. That is the fallback behaviour WAL exists to
  replace with snapshot isolation.
- `SQLITE_LOCKED` in shared-cache mode is *not* retried by `busy_timeout`.
  Retrying it requires `sqlite3_unlock_notify()`, which `Microsoft.Data.Sqlite`
  does not expose. So this introduces a distinct, unretriable error class.
- SQLite's own documentation discourages shared cache and marks it for removal
  from the codebase.

Meanwhile `busy_timeout=30000` is largely inert: with one physical connection
per file there is no second connection to time out against.

Why is it there? The honest reason is that ACE constructs a fresh
`DbContext` per operation — `new ShardDbContext()` appears all over
`ACE.Database` — so without sharing, each operation would open its own
connection and its own page cache. `Cache=Shared` was the cheap way to stop
that. It solved a real problem, with the wrong tool, and we never wrote down
the trade-off. Both articles' "configure for multi-threaded access" advice is
about *not* doing this.

**Fix.** `Microsoft.Data.Sqlite` 9.0.20 supports a `Pooling` connection-string
keyword (confirmed present in `SqliteConnectionStringBuilder.Pooling` and
`PoolingKeyword` in the assembly). Replace `Cache=Shared` with `Pooling=True`
plus `Default Timeout=30`. Then optionally add EF Core context pooling
(`AddDbContextPool`) to stop re-allocating a `DbContext` per operation, which is
the change that actually removes the connection churn rather than hiding it.

This is a real behavioural change, not a drop-in, and it needs the full test
suite re-run plus a concurrency soak (§8). It is worth doing because the
current configuration is not a production configuration, and pretending
otherwise is how the failure arrives at 3am.

### 3.3 P1 — no backups, and our own docs gave bad advice

There is no backup mechanism. Worse, `SQLITE_BACKEND.md` §4, item 5 said:

> copy all three if you move a database while the server is running

That is [pecar §6, "Gotcha: Backups"](https://blog.pecar.me/sqlite-prod/) almost
verbatim — "you might be tempted to copy/paste the SQLite file to create a
backup, but this is a bad idea as it can corrupt the backup file. Instead, you
should always use the `VACUUM INTO` command." **Fixed** in both docs now, but it
is worth recording that the
advice was wrong and that this is the single most common way people lose data
with SQLite. The correct command is `VACUUM INTO`, which is safe against a live
database:

```bash
sqlite3 db/ace_shard.db "VACUUM INTO '/backups/ace_shard-2026-09-26.db';"
sqlite3 '/backups/ace_shard-2026-09-26.db' "PRAGMA integrity_check;"   # must print: ok
```

The larger gap is recovery rather than backup. A shard backup that is only
taken when someone remembers does not survive a disk failure. The answer both
articles converge on is
[Litestream](https://github.com/benbjohnson/litestream) — continuous
replication to S3, with point-in-time recovery:

```yaml
# /etc/litestream.yml
dbs:
  - path: /srv/ace/db/ace_shard.db
    replicas:
      - type: s3
        bucket: my-ace-backups
        path: ace/ace_shard
  - path: /srv/ace/db/ace_auth.db
    replicas:
      - type: s3
        bucket: my-ace-backups
        path: ace/ace_auth
```

The world database is a re-downloadable artifact, so it does not need backing
up at all — which is a genuine advantage over MySQL, where `ace_world` is a
real 137 MB table you have been backing up for no reason.

**Fix.** `VACUUM INTO` in the setup docs (done), Litestream configuration in the
runbook, and a restore drill before there is any player data to lose.

### 3.4 P2 — EF Core opens deferred transactions

Microsoft.Data.Sqlite documents the hazard on the very overload that EF Core
uses, `SqliteConnection.BeginTransaction(bool deferred)`:

> Warning, commands inside a deferred transaction can fail if they cause the
> transaction to be upgraded from a read transaction to a write transaction but
> the database is locked. **The application will need to retry the entire
> transaction when this happens.**

This is [pecar §5, "Gotcha: Transactions"](https://blog.pecar.me/sqlite-prod/):
a plain `BEGIN` takes no lock, so two transactions that both read and then both
try to write can deadlock against each other, and the resulting `SQLITE_BUSY` is
not resolvable by `busy_timeout` — the whole transaction must be replayed. His
prescription is `BEGIN IMMEDIATE`.

I checked how exposed ACE is. It is **less exposed than it first appears**,
and the doc should be precise rather than alarmist:

```
$ grep -rn 'BeginTransaction' --include='*.cs' ACE.Database/ ACE.Adapter/ ACE.Server/
(no matches)
```

ACE never opens an explicit transaction. Every write goes through an implicit
EF `SaveChanges()` transaction whose first statement is a write, which acquires
`RESERVED` immediately and makes `busy_timeout` sufficient. So today this is
**latent, not active**: probability low, impact high.

It becomes active the first time someone adds `BeginTransaction` to make, say,
"save character and save biota atomically" — a natural, correct change to make.
That contributor will inherit a deadlock that only reproduces under concurrency
and only on some machines.

**Fix.** An EF Core `DbTransactionInterceptor` that rewrites the `BEGIN` to
`BEGIN IMMEDIATE` for the shard and auth contexts, plus a
`DbException`-level retry wrapper on `SaveChanges`. Roughly half a day, and it
removes a whole class of "works on my machine" bug reports.

### 3.5 P2 — `synchronous=NORMAL` is the wrong default for a shard

`SqlitePragmaInterceptor.cs:20-22` sets `synchronous=NORMAL` and documents it
as "an acceptable trade for a local development database". That is a correct
description of the setting and an incorrect thing to ship — it is a development
default that has not been revisited, and the comment is what should have
triggered that.

In WAL mode, `NORMAL` means commits are not fsynced. The database will not
*corrupt* on power loss, but **recently committed transactions can be lost**.
For a shard, "recently committed" means "this player just received a Mandrake
and it is not in the database".

I assumed this would be expensive and that `FULL` would be a throughput
trade-off to be weighed. Measured (§2), it is not: **`FULL` costs +4%** over
`NORMAL` for batched writes on this hardware (7.8 ms vs 7.5 ms for 5,000 rows).
That is well inside noise, and it is the single most surprising number in this
assessment.

Caveat, stated plainly: this is APFS on NVMe, where fsync is cheap. On a
container with a slower volume, or a spinning disk, `FULL` will cost more. It
should be a config knob, not a hardcoded constant, so it can be measured on the
target host rather than guessed at.

**Fix.** Make `synchronous` configurable; default `NORMAL` for development,
`FULL` for anything a player would notice.

### 3.6 P2 — no schema version stamp

All three databases report `PRAGMA user_version = 0`:

```
=== ace_auth.db  (28K) ===   page_size=4096  journal=wal  user_version=0
=== ace_shard.db (640K) ===  page_size=4096  journal=wal  user_version=0
=== ace_world.db (148M) ===  page_size=4096  journal=wal  user_version=0
```

`SqliteBootstrapper` stamps nothing, so nothing can detect a database built by
a different version of ACE, or a world artifact from the wrong release. Today the
only check is `ValidateWorldDatabase`, which counts weenies and spells — that
catches a truncated or wrong file, and nothing else. A shard database migrated
by hand, or a world artifact three versions stale, looks fine.

`user_version` is a free 32-bit integer per database, designed for exactly this.
Writing ACE's own schema version into it is a few lines in the bootstrapper,
and it makes "which version is this box on" answerable without archaeology.

### 3.7 P3 — the read pragmas the articles recommend, and what they buy

[pecar §1](https://blog.pecar.me/sqlite-prod/) recommends this set:

```sql
PRAGMA foreign_keys = ON;          -- we have it
PRAGMA journal_mode = WAL;         -- we have it
PRAGMA synchronous = NORMAL;       -- we have it (see §3.5)
PRAGMA mmap_size = 134217728;      -- MISSING
PRAGMA journal_size_limit = 27103364;  -- MISSING
PRAGMA cache_size = 2000;          -- MISSING
```

Three are unset. Measured against the real 148 MB world database, 8 passes of
~1,000 spaced point lookups on `weenie_properties_string`:

| configuration | ms/pass |
|---|---|
| default (2 MB page cache, no mmap) | 11.6 |
| `cache_size=-65536` (64 MB) | 12.1 |
| `mmap_size=268435456` | 10.5 |
| both | 10.7 |

**These differences are noise.** The whole file sits in the OS page cache and
the working set is small, so SQLite's own page cache and mmap have nothing left
to improve. I am reporting this rather than a manufactured win: on this
hardware, with this workload, `mmap_size` and `cache_size` do not matter.

They stop mattering in two situations, and both are plausible in production:

- **Memory pressure.** The machine also has to hold ACE's weenie and biota
  caches. When the OS evicts the world database, a 2 MB SQLite page cache
  becomes the bottleneck, because every read is a `pread()` syscall plus a copy.
  A 64–128 MB `cache_size` covers the `landblock_instance` preload outright.
- **A cold cache after restart.** Same argument.

`journal_size_limit` is the one with no "probably fine" argument. It is unset,
so it defaults to `-1` (unlimited), and the `-wal` file can stay large
indefinitely after a large transaction rather than being truncated at
checkpoint. [pecar §4](https://blog.pecar.me/sqlite-prod/) flags exactly this:
"you might also encounter a WAL issue where the checkpoint doesn't flush the
`.wal` file data in the main database file. This can make your `.wal` file grow
very large." The shard's 40-table `GetSequenceGaps` and the world bootstrapper's
table rebuilds are precisely the kind of operation that spikes it. He notes the
real fix is [WAL2](https://www.sqlite.org/cgi/src/doc/wal2/doc/wal2.md), which
is experimental and requires compiling SQLite — not an option against
`Microsoft.Data.Sqlite` — so `journal_size_limit` is the practical mitigation.

**Fix.** Add `cache_size`, `journal_size_limit` and `temp_store`, all
configurable, all defaulting to the conservative current behaviour. Do not
expect a speedup; the point is bounding worst cases.

### 3.8 P3 — no replication, no point-in-time recovery

Covered in §3.3. Litestream is the answer. The one genuinely absent capability
is **high availability** — SQLite has no second node to fail over to without
LiteFS or rqlite, both of which add a distributed-systems layer to a system that
is currently one process on one box. For a game shard, planned downtime to
promote a backup is usually acceptable; it should be a deliberate decision
rather than an unexamined gap.

### 3.9 P3 — SQLite's `ALTER TABLE` limits will shape any migration story

[pecar §7](https://blog.pecar.me/sqlite-prod/): "SQLite has limited support for
the ALTER TABLE statement, which relational schema migration tools rely upon.
Only adding and dropping columns and renaming tables are supported." There is
no `ALTER COLUMN`, no `DROP CONSTRAINT`, and `RENAME COLUMN` requires SQLite
3.25+.

This is the concrete shape of the migration gap. Today we dodge it entirely:
`Offline.AutoUpdateWorldDatabase` and `Offline.AutoApplyDatabaseUpdates` are
MySQL-only, and `SqliteDatabaseUpdateProvider` reports them as unsupported
rather than the call site skipping them, so the reason is logged whenever an
operator has enabled either flag. The world database arrives as a finished
artifact.
That is fine while the artifact is current and becomes a problem the first time
you need to move a running shard onto a newer world release — at which point
the only supported operation is a straight file replacement, discarding any
local world edits.

The good news is that we already have the hard part. `RebuildTable` in
`SqliteBootstrapper` does exactly what a SQLite migration would need to do:
create a new table, copy every row, drop the old, rename. That is the standard
[batched-migration](https://alembic.sqlalchemy.org/en/latest/batch.html#running-batch-migrations-for-sqlite-and-other-databases)
workaround, and it is already written and tested against the real schema. A
migration story for ACE is therefore mostly a matter of driving existing
machinery from version N to N+1 rather than inventing anything.

**Status.** Out of scope for a development-only backend. Worth recording that it
is *cheap* if that scoping ever changes: drive `RebuildTable` and stamp
`user_version` (§3.6) as you go, since the bootstrapper already computes the
EF-model-driven target shape.

### 3.10 defect — relative paths resolve against the working directory, not the executable

`DbProvider.ResolveSqlitePath` finishes with:

```csharp
return Path.IsPathRooted(configured)
    ? configured
    : Path.GetFullPath(configured);
```

`Path.GetFullPath` resolves a relative path against `Environment.CurrentDirectory`.
ACE resolves `Config.js` against the *executable* location instead
(`Program.cs:132`, `Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)`).
So the two disagree whenever the process working directory is not the directory
the server was launched from, and the SQLite files are created in one place while
the configuration is read from another.

In Docker this happens to work — the `ENTRYPOINT` is `dotnet ACE.Server.dll`
with `WORKDIR /ace`, so the working directory and the executable directory
coincide. Launched from a service manager, a scheduled task, or a shell that
`cd`s elsewhere, it does not.

This is a bug rather than a stylistic disagreement because **the project already
fixed this exact problem once**, for the MySQL scripts:

```csharp
// MySqlDatabaseUpdateProvider.PatchDatabase()
if (!Directory.Exists(updatesPath))
{
    // File not found in Environment.CurrentDirectory
    // Lets try the ExecutingAssembly Location
    var executingAssemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
```

That fallback exists because of #3886, "Add fallback using path of executing
assembly to help determine the updates directory path ... when the current
directory is set to an unexpected directory path." The SQLite path should
resolve from the executable location from the start rather than rediscovering
the same bug on a second code path.

**Fix.** Resolve relative SQLite paths against the directory containing
`ACE.Server.dll`, matching `Program.cs:132`, or at minimum try that location as
a fallback the way `MySqlDatabaseUpdateProvider.PatchDatabase` does.

---

## 4. Structural opportunities

These are not defects. They are places where the current design leaves value on
the table, and they are cheap.

### 4.1 the world database is never written — make that enforced

I checked every write path against world tables:

```
SaveChanges on WorldDbContext:        0
ExecuteSqlRaw / raw command on world: 0
INSERT/UPDATE/DELETE on world tables: 0
```

Across all 41 `WorldDbContext` instantiations, zero writes. The only offline
tool that writes at all is `BiotaGuidConsolidator`, which is shard-only. The
world contexts even set `QueryTrackingBehavior.NoTracking` on all 15 read paths
(`WorldDatabase.cs:180` and 14 others).

So the largest, most expensive file — 148 MB, the one most likely to be
corrupted and least likely to be recoverable — is opened read-write when it
could be opened read-only. The runtime connection should be `Mode=ReadOnly`,
which removes all lock acquisition on reads, removes any possibility of
corrupting the artifact, and eliminates the need for a `-wal` there at all.

One real nuance, and it is the reason this is not a one-line change:

> A read-only connection to a WAL-mode database still needs a **writable `-shm`**
> file, because WAL uses shared memory for its read-mark array.

So if `db/` is writable (the normal case) `Mode=ReadOnly` works as-is. If you
want the world database to be genuinely immutable — mounted read-only, say —
then the database must *not* be in WAL mode, and the options are
`journal_mode=DELETE` or the `immutable=1` URI parameter, which tells SQLite the
file can never change and skips all locking and change detection. `immutable=1`
is the strongest guarantee and the fastest, at the cost of requiring a restart
whenever the file is replaced.

The bootstrapper needs write access on first run regardless (it rebuilds tables
to repair numeric affinities, §2.3 of the backend doc), and it already opens
its own raw connections, so the read-only split is clean: bootstrap read-write,
runtime read-only, and the repair is skipped once `user_version` (§3.6) says
the artifact is already correct.

### 4.2 `page_size` and huge pages: measured, and it is a non-issue

Since `page_size` is the SQLite knob that interacts with huge pages, I measured
it. `PRAGMA page_size` is fixed at creation and only changes via `VACUUM`, so
each arm is a full copy plus in-place `VACUUM` at the target size, 32 MB page
cache identical across arms, median of 5 reps after warmup:

| `page_size` | file size | pages | full scan | vs 4096 |
|---|---|---|---|---|
| 4,096 | 131.2 MiB | 33,582 | 6 ms | baseline |
| 8,192 | 130.8 MiB | 16,739 | 6 ms | −0% size, −1% scan |
| 16,384 | 131.2 MiB | 8,399 | 6 ms | +0% size, −1% scan |
| 65,536 | 135.6 MiB | 2,169 | 6 ms | +3% size, −3% scan |

**Nothing.** All four are within noise, exactly as the `cache_size`/`mmap_size`
result was, and for the same reason: the hot set is small and RAM is not the
binding constraint. Larger pages only pay off when the working set does not fit
in cache, which is not ACE's situation. 4,096 is the right choice and should
stay.

To be precise about the mechanism, since "huge pages" is worth stating
correctly: **SQLite has no huge-page pragma.** Transparent Huge Pages are a
Linux kernel facility, and the only two knobs that interact with them are
`page_size` (how large each b-tree node is, and therefore how many are
resident) and `mmap_size` (whether reads are memory-mapped and thus eligible
for THP backfill). Both were measured here and both are irrelevant at this
scale. If ACE is deployed on Linux and the world database grows well past
memory, THP at
`/sys/kernel/mm/transparent_hugepage/enabled = always` plus a large `mmap_size`
would reduce TLB misses on a full scan — that is the only condition under which
any of this is worth doing. **Recommendation: do nothing.**

### 4.3 no schema version, no foreign keys on the world artifact

Covered in §3.6. Separately: `foreign_keys=ON` applies only to the auth and
shard schemas, which ACE creates from its EF model. The pre-converted world
database carries no foreign keys to enforce, so a mismatched artifact is not
caught by the engine — only by the weenie/spell count check. An FK-bearing
`ace_world.db` would be a better artifact; worth raising with whoever maintains
`ace-to-sqlite`.

---

## 5. Summary table

Scoped as: what must be fixed before offering this upstream, versus what is a
documented limitation of a development-only backend.

| # | Finding | Kind | Action |
|---|---|---|---|
| 3.1 | Pragma failures swallowed; `journal_mode` never verified | **defect** | **fixed** — see §3.1 |
| 3.2 | `Cache=Shared` defeats WAL; `SQLITE_LOCKED` unretriable | **defect** | fix before PR — needs soak test |
| 3.5 | `synchronous=NORMAL` | **defect** | make configurable — `FULL` costs +4% |
| — | `ResolveSqlitePath` resolves against CWD, not `exeLocation` | **defect** | fix before PR — see §3.10 |
| 3.3 | No replication / PITR | limitation | out of scope; `VACUUM INTO` documented |
| 3.9 | No patch pipeline; `ALTER TABLE` limits | limitation | out of scope; provider refuses loudly and logs why |
| 3.4 | EF opens deferred transactions | limitation | latent; low impact at dev concurrency |
| 3.6 | `user_version=0`; no schema stamp | improvement | nice-to-have |
| 3.7 | `cache_size` / `mmap_size` / `journal_size_limit` unset | improvement | nice-to-have; measured as noise |
| 3.8 | High availability absent | out of scope | LiteFS/rqlite; not a dev-mode concern |
| 4.1 | World DB opened read-write though never written | improvement | nice-to-have |
| 4.2 | `page_size` / huge pages | not applicable | measured; do nothing |

---

## 6. What is already correct

Not everything needs changing, and it is worth being specific so the fixes above
do not disturb it:

- **WAL mode.** Correct, and demonstrably clean: after a normal shutdown all
  three `-wal` files are 0 bytes, meaning the checkpoint ran and there is no
  unmerged data.
- **`busy_timeout=30000`** (once §3.1 makes it reliable). 30 s is generous
  against ~38,000 commits/s of headroom.
- **Batched writes.** `SaveBiotasInParallel` and friends are 22× cheaper per
  row than per-row commits. Already the right shape.
- **Three separate files** → three independent writer locks.
- **A read-only-in-practice world database** — the basis of §4.1.
- **Schema parity.** 271/271 shard columns, 508/508 world columns, zero type
  family mismatches, and a full 2.8 M-row × 508-column cell-for-cell
  differential against MySQL. The data layer is not the risk.
- **Disabling the MySQL patch pipeline** under SQLite. Correct — that pipeline
  is MySQL-specific raw SQL. The skip is now a provider answering `IsSupported`
  rather than a conditional at the call site, and the reason is logged whenever
  an operator has enabled one of the `Offline` flags. A warning is the right
  severity; failing startup would be worse than not patching, and returning
  quietly would be how a world drifts behind with nothing in the log to say so.

---

## 7. What to fix before offering this upstream

Narrowed from a production-readiness plan to the set that has to be right for a
*development* backend to be worth merging. The bar is different: not "safe for
players," but "does not waste a contributor's afternoon or lose their work
quietly."

**Required — the four defects, one of them now done**

1. §3.10 Resolve relative SQLite paths against the executable location, matching
   `Program.cs:132`. Every contributor hits this, and the project has already
   been bitten by it once (#3886).
2. §3.1 ~~Replace the empty `catch` in the pragma interceptor.~~ **Done.** Each
   pragma is applied and read back individually, a `journal_mode` that is not
   `wal` is fatal, warnings are deduplicated, and four tests pin it — see §3.1.
3. §3.2 `Cache=Shared` → `Pooling=True`. Needed mostly so `busy_timeout` means
   something; re-run both suites and add a concurrency soak asserting zero
   `SQLITE_BUSY`/`SQLITE_LOCKED`.
4. §3.5 Make `synchronous` configurable. `FULL` costs +4% and prevents a
   developer's `-wal` from losing their last few writes to a closed laptop lid.

**Worth doing, same PR if it fits**

5. §3.6 Stamp `PRAGMA user_version`; verify on startup. Two lines, and it makes
   "which version is this `db/` directory" answerable when someone reports a
   problem.
6. §3.7 `cache_size` and `journal_size_limit`, configurable. Bounding worst
   cases; measured as no speedup, so do not expect one.
7. §4.1 Open the world database read-only at runtime. Prevents accidental
   corruption of the artifact, which is the one file a developer cannot easily
   regenerate.

**Explicitly not doing, and that is the decision**

- §3.3 replication, PITR, Litestream. Not a development-mode concern.
- §3.9 the patch pipeline. The gap is real and is the primary reason this stays
  development-scoped; it is logged at startup so nobody discovers it late.
- §3.4 `BEGIN IMMEDIATE`. Latent, and low impact at development concurrency.
  Worth a comment on `SaveChanges` so the first person to add
  `BeginTransaction` knows.
- §3.8 high availability, §4.2 huge pages and `page_size`. Measured, no effect,
  and the reason is structural rather than incidental.

---

## 8. Test requirements for the changes above

The existing validation is solid on the data layer and is the baseline to hold
this work to:

- `ACE.Database.Tests` 7/7 and `ACE.Server.Tests` 13/13, under **both**
  providers. The suites read `Config.js`, so the same assertions must pass on
  MySQL and on SQLite. A §3.2 change that only passes one provider is not done.
- The end-to-end character creation test
  (`ACE.Server.Tests/CharacterCreationTests.cs`) is the closest thing to a real
  write path; it must keep leaving all 40 shard tables at zero rows.
- New for §3.2: a concurrency soak, as described above. Neither existing suite
  creates contention, which is exactly why §3.2 has gone unnoticed.
- New for §3.4: a test that runs two transactions which both read and then
  write the same row, asserting the retry wrapper resolves it.
- New for §3.1: a test that opens a database which cannot be put into WAL mode
  and asserts the server **fails with a clear error** rather than starting
  degraded. **Done** — `SqlitePragmaTests.PragmaThatCannotBeApplied_Throws`.
- New for §3.10: a test that asserts a relative SQLite path resolves against
  the executable directory rather than the working directory.
- New for the PR as a whole: a CI job that runs both suites with
  `"Provider": "sqlite"`. Without it none of the above is exercised by the
  project's own pipeline, since `AppVeyor/Config.js` has no `Provider` key and
  resolves to MySQL.

---

## 9. Verdict

The scoping decision is sound, and it is worth being precise about why, because
the usual reason is wrong.

SQLite is not the development backend because it is too slow for a shard. §2
measures it against ACE's real access pattern and finds the opposite: batched
writes around 670,000 rows/s, a ceiling near 38,000 commits/s, and a read
working set small enough that `cache_size`, `mmap_size` and `page_size` all
measure as noise. The single-writer limitation that decides most SQLite
verdicts does not bind here, and the three-file layout means three independent
writer locks rather than one.

It is the development backend because of **patching, replication, and artifact
trust**. A MySQL world receives `Database/Updates/` automatically and keeps its
world dump current; a SQLite world receives neither, and drifts silently as ACE
ships changes. A MySQL world has a database server that can be backed up,
replicated, and recovered. A SQLite world is three files with `VACUUM INTO` and
hope. And the world database is a pre-converted artifact from a third-party
repository rather than an import the project performs itself.

None of those are fixable inside this PR, and two of them are process rather
than code. That is the honest reason for the boundary, and it is a better
argument than a vague performance concern — it also means the boundary can be
revisited on evidence rather than on impression.

What the PR *is*, then: a way to run and develop ACE with no database server,
whose data layer is verified equal to MySQL's by schema parity and a full
2.8 M-row differential, with MySQL unchanged and still the default. Three
defects in §7 remain, and they are small. Fixing §3.1 first was the right order:
it was the one that produced a server starting cleanly, logging nothing about a
problem, and then failing at random — a poor advertisement for the thing meant
to make development easier.
