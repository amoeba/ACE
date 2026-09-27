# Open defects

Outstanding work on `prototype/sqlite-backend`, as of `e405c182`. None of this
is pushed. Section references are to `SQLITE_PRODUCTION.md`.

Ordered by value, not by section number. The §3.10 bug is first because every
contributor hits it and the project has already been bitten by the same class
of bug once.

---

## 1. §3.10 — relative SQLite paths resolve against the working directory

**Severity:** high, for usability
**File:** `Source/ACE.Database/DbProvider.cs`, `ResolveSqlitePath`

The method ends in:

```csharp
return Path.IsPathRooted(configured)
    ? configured
    : Path.GetFullPath(configured);
```

`Path.GetFullPath` resolves a relative path against `Environment.CurrentDirectory`.
ACE reads `Config.js` against the *executable* location instead
(`Program.cs:132`), so the `.db` files get created in a different directory from
the one the config was read out of.

In Docker this happens to work: the `ENTRYPOINT` is `dotnet ACE.Server.dll` with
`WORKDIR /ace`, so the working directory and the executable directory coincide.
Launched from a service manager, a scheduled task, or a shell that `cd`s
elsewhere, it does not.

The project already fixed this exact problem once, for the MySQL scripts, in
[#3886](https://github.com/ACEmulator/ACE/issues/3886) — "Add fallback using
path of executing assembly to help determine the updates directory path ... when
the current directory is set to an unexpected directory path." The precedent now
lives in `MySqlDatabaseUpdateProvider.PatchDatabase`. The SQLite path should
resolve from the executable location from the start rather than rediscover the
same bug on a second code path.

**Fix:** resolve relative SQLite paths against the directory containing
`ACE.Server.dll`, or at minimum try that location as a fallback the way the MySQL
path does.

**Test:** a test asserting a relative SQLite path resolves against the
executable directory rather than the working directory. This one is awkward to
test honestly — the working directory is process-global, so it needs a
subprocess or a temporarily changed CWD, and it should assert the resolved
absolute path rather than merely that "it worked."

**Note:** a related but separate concern is that the tests use
`AppContext.BaseDirectory` to sidestep this. That works around the bug rather
than testing it, so the test above is still needed.

---

## 2. No CI job exercises the SQLite path

**Severity:** high, for mergeability
**Status:** addressed by `.github/workflows/sqlite-backend-ci.yml`, with the
scope limit recorded in questions.md item 6.

`AppVeyor/Config.js` has no `Database.Provider` key, so `DbProvider.Active`
resolves to MySQL and the entire SQLite backend was dead code in the project's
own pipeline.

The workflow adds a two-leg matrix — `sqlite` and `mysql` — on
`windows-latest`, which also closes the one platform the branch had never been
exercised on. It is additive; AppVeyor still owns releases and deploys. The
`mysql` leg ships no `Database` key at all, exactly as AppVeyor does, so it
validates the unchanged default path.

What it runs: the whole solution builds, `ACE.Database.Tests` executes. Both
were verified locally before the workflow was written — the `sqlite` leg
end to end from a fresh directory against the digest-pinned artifact, the
`mysql` leg against OrbStack MySQL. `ACE.Server.Tests` is built but not run;
see questions.md item 6 for why and what it costs.

Two things this job deliberately does **not** do, both worth knowing so its
green result is not over-read:

- It **cannot** catch item 1 below. It writes absolute SQLite paths precisely
  so that every process shares one database file, and that choice routes around
  the very bug CI should be reporting.
- It **cannot** catch item 2b below, because it never runs the suite that
  crashes.

---

## 2a. Test helpers hard-code an output-path depth

**Severity:** medium — will bite the next person who changes the build layout
**Status:** fixed
**Files:** `ACE.Server.Tests/TestEnvironment.cs`, `ACE.Server.Tests/StarterGearTests.cs`,
`ACE.Database.Tests/AccountTests.cs`, `ACE.Database.Tests/WeenieSearchTests.cs`

Both compute the server directory by walking five levels up from
`AppContext.BaseDirectory`:

```csharp
var serverDir = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", "..", "ACE.Server"));
```

That is correct only for `bin/<Platform>/Release/net<TFM>`. With a flat
`bin/Release/net<TFM>` the same walk overshoots to the repository root and every
test fails with a confusing `DirectoryNotFoundException` for a path that looks
almost right.

This is not hypothetical. It is why the workflow must pass
`-p:Platform=x64` to **both** `dotnet build` and `dotnet test`: the csproj
declares `<Platforms>x64</Platforms>`, so a bare build emits `bin/x64/Release`
while a bare `dotnet test` looks in `bin/Release`. AppVeyor hides this by
setting `platform: x64` globally; Actions has no equivalent.

**Fix:** walk up to the nearest ancestor directory containing
`ACE.Server/ACE.Server.csproj` instead of counting levels. That is correct for
any layout, any platform, and any future target framework.

**Resolution.** Three of the four sites were converted by the review batch; this
commit did the fourth, `StarterGearTests`, which still had the raw walk. All four
now search for `ACE.Server/Config.js.example` rather than counting. That marker
was chosen over the `.csproj` the fix above names because it is a shipped file
rather than a build output, so it is present in any checkout and in any layout.

`TestEnvironment.FindServerDirectory` takes the starting directory as a
parameter, and that parameter is the point. At the depth the test host actually
uses — `bin/arm64/Release/net10.0`, four levels — counting five parents lands on
`Source`, which is the *correct* answer, so a test that only ever calls the
parameterless overload cannot tell counting from searching. The defect this
replaces is precisely a disagreement at some other depth. Driving the depth from
the test is what makes it observable.

**Tests:** `StarterGearTests` has three. `TheServerDirectoryIsFoundFromAnyOutputDepth`
calls the search from a path three levels deeper than the real output directory
and asserts the answer does not move. Verified by mutation: replacing the search
with a five-parent count fails it. The other two assert the resolved path and the
reason it is right, so a regression says so by name rather than as a
`FileNotFoundException` from a read that happens to fail.

**Residual:** the same helper is duplicated in `AccountTests` and
`WeenieSearchTests` in the other test project, and those two copies are private
and unguarded. They are correct, but nothing would catch a regression there.
Consolidating all four into one shared helper would fix that; it needs a home
both test projects can see, which is why it was not done here.

---

## 2b. A background thread kills the test host when `.dat` files are absent

**Severity:** medium — presents as a flake, not a failure
**Files:** `ACE.Server/Managers/WorldManager.cs:60`,
`ACE.Server/Managers/LandblockManager.cs:127`,
`ACE.Server/Entity/Landblock.cs:174`

`WorldManager.Initialize` spawns landblock preloading on a background thread.
`PreloadConfigLandblocks` is gated on `Server.LandblockPreloading`, which
`Config.js.example` ships as `true`, and `Landblock..ctor` immediately reads
`DatManager.CellDat`. With no `client_cell_1.dat` that is a null dereference,
and because it is on a thread pool thread with no handler it takes the whole
process down.

The bad part is the timing: the test results are sometimes reported *before*
the thread dies, so the same configuration passes once and aborts the run the
next time. Found while writing the CI job — the first simulated run passed, the
second aborted with the identical config.

**Fix (CI):** set `LandblockPreloading: false` in the generated config. That is
correct only where the `.dat` files are absent, which is the case on a runner.

**Fix (product):** worth considering separately. A developer who follows
`SQLITE_SETUP.md`, has not yet pointed `DatFilesDirectory` at a real `.dat`
set, and starts the server gets an unhandled exception on a background thread
with no indication that the cause is a missing data file. `DatManager`
already logs a clear `FileNotFoundException` message for exactly this case and
then lets startup continue into the dereference. An explicit guard, or failing
fast with that message, would be kinder.

---

## 2c. Test suites silently re-copy `Config.js`, so edits to the copy are lost

**Severity:** low, but it wasted real time
**Files:** `ACE.Server.Tests/TestEnvironment.cs:63`,
`ACE.Database.Tests/AccountTests.cs:43`

Both do an unconditional `File.Copy(configSource, testDir/Config.js, true)`
before `ConfigManager.Initialize()`.

This is correct — it is what lets a developer edit one config and have both
suites see it — but it means editing the copy in the test output directory has
no effect whatsoever. Editing it to point at an empty `.dat` directory to test
dat-free behaviour appeared to work, and the suites passed, because the copy
had already been overwritten from the real config. The conclusion drawn from
that run was wrong.

**No code change needed.** Documented here because the same trap will catch
anyone else trying to vary the config per suite. Vary
`Source/ACE.Server/Config.js` instead, and be aware the suites share it.

---

## 2d. `ACE.Database.Tests` only passed against a database something else had provisioned

**Severity:** medium — this is what blocked a clean-checkout test run
**Status:** fixed alongside the CI workflow
**File:** `ACE.Database.Tests/AccountTests.cs`

`SqliteBootstrapper.EnsureDatabases()` was reachable only from
`DatabaseManager.Initialize()` and `Program.cs`. `ACE.Database.Tests` calls
neither — it does not reference `ACE.Server` at all — so nothing in that suite
ever created `ace_auth.db`.

Every local run passed anyway, because `ACE.Server.Tests` had already created
the file earlier in the session and the two suites share a database directory.
On a clean runner the suite fails 6 / 12 with:

```
SqliteException: SQLite Error 1: 'no such table: account'
```

which reads like a provider defect rather than a missing fixture.

**Fix:** `AccountTests.TestSetup` now calls `SqliteBootstrapper.EnsureDatabases()`
after `ConfigManager.Initialize()`, exactly as `Program.cs` does. No-op on
MySQL — verified 11 / 11 against the local OrbStack MySQL with the call in
place, `ace_auth.account` left at 0 rows.

Worth noting what this says about the branch more broadly: the suites passed for
most of this session partly because the environment was already warm. A green
run on a developer machine is weaker evidence than it looks when nothing in the
suite provisions its own fixtures.

---

## 3. §3.2 — `Cache=Shared` defeats WAL; `SQLITE_LOCKED` is unretriable — **FIXED**

**Severity:** medium (was high in `SQLITE_PRODUCTION.md` §3.2)
**File:** `Source/ACE.Common/SqliteConfiguration.cs`

```csharp
// was:
ConnectionOptions = "Cache=Shared;Foreign Keys=True;Default Timeout=30"

// now:
public string ConnectionOptions { get; set; } = "Pooling=True;Foreign Keys=True;Default Timeout=30";
```

`Cache=Shared` is a testing and single-process-coordination option. It defeated
some of the benefit of WAL, and it interacted badly with `busy_timeout` — which is
the thing that actually makes concurrent access safe. A `SQLITE_LOCKED` in
particular is not retriable the way `SQLITE_BUSY` is.

Measured (Microsoft.Data.Sqlite 9.0.20, WAL, two connections): a write on one
connection while the other merely holds a reader took **30,078 ms and then failed**
under `Cache=Shared`, and **0 ms** under `Pooling=True`. So it was worse than the
original note assumed — not an unretriable error, but a 30-second stall per
collision en route to one.

**Fix:** done. `Cache=Shared` → `Pooling=True`, and the property is settable and no
longer `[JsonIgnore]`d, so it is overridable from Config.js rather than only by
rebuilding. Pinned by `Source/ACE.Database.Tests/SqliteConnectionStringTests.cs`
(verified to fail when the old default is restored).

**Test:** both suites re-run. The concurrency soak below is now written.

**Concurrency soak:** `SqliteConcurrencySoakTests`, 2 tests of 25 iterations each.
It opens a reader that has executed its `SELECT` and left the result open -- the shape
of an ACE read between operations -- and asserts a write on another connection
succeeds immediately, with a second test doing the same against four readers.

The scenario had to be chosen carefully, and the wrong choice is a test that cannot
fail. A reader holding an explicit read transaction (`BEGIN` then `SELECT`,
transaction left open) blocks a writer for the whole busy timeout **even in a
healthy WAL database** -- measured at 30,075ms and SQLITE_BUSY. That is normal
SQLite behaviour, not the defect. The defect is narrower: a connection that has
merely executed a `SELECT` and left the reader open, with no transaction, blocks a
writer under shared cache (30,060ms, SQLITE_LOCKED) and does not under pooling (0ms).
That is the scenario the soak uses.

The connection string is the one `DbProvider` generates, with `ConnectionOptions`
left at the production default rather than set by the test -- setting it would make
the soak test a string of its own and a regression would pass unnoticed. Only the
busy timeout is shortened, to 5s, so a real regression costs seconds instead of
twenty-five minutes; the production value is pinned separately by
`SqliteConnectionStringTests`.

Verified by mutation. Restoring the `Cache=Shared` default fails both tests, and
fails fast: the soak asserts its own premise (pooling on, shared cache off) before
contending, so a regressed default is caught in milliseconds rather than after a
busy timeout per iteration.

---

## 4. §3.5 — `synchronous=NORMAL` is hardcoded

**Severity:** medium
**Status:** fixed
**File:** `Source/ACE.Database/SqlitePragmaInterceptor.cs`, `Source/ACE.Common/SqliteConfiguration.cs`

`SqlitePragmaInterceptor` sets `synchronous=NORMAL` and documents the choice,
but there is no way to change it without editing code.

`NORMAL` is a reasonable default for a server. It is a worse default for a
developer on a laptop, where the last few writes can be lost to a closed lid.
`FULL` measures at +4% (see §2), which is not a real cost at development
volumes.

**Fix:** make it configurable, defaulting to `NORMAL` so behaviour is unchanged
for anyone not asking for it. The interceptor already applies and verifies each
pragma individually, so the new one needs a test in `SqlitePragmaTests` like the
others.

**Resolution.** `Sqlite.Synchronous` (int, default 1=NORMAL; 0=OFF, 2=FULL,
3=EXTRA). The interceptor reads it per connection rather than caching it, so a
config change takes effect on the next connection and a test that changes it
between connections sees the new value. When `ConfigManager` has not been
initialised the interceptor falls back to a default config, which is the previous
hardcoded value, so behaviour is unchanged there.

**Tests:** `SqlitePragmaTests.ConnectionOpened_PragmaValuesFollowTheConfiguration`
sets `Synchronous=2`, `CacheSize=-8000` and `JournalSizeLimit=-1`, and asserts the
interceptor applied all three. Verified by mutation: hardcoding
`synchronous=NORMAL` in the interceptor fails it.

---

## 5. §3.6 — no `user_version` stamp

**Severity:** low
**Status:** fixed
**File:** `Source/ACE.Database/SqliteBootstrapper.cs`, `Source/ACE.Common/SqliteConfiguration.cs`

`user_version` is 0 in all three files, so nothing records which version a given
`db/` directory is at. The consequence is entirely about supportability: someone
reports a problem, and there is no way to tell them which state their files are
in.

**Fix:** stamp `PRAGMA user_version` on creation and verify it on startup. Two
lines. Worth doing because the SQLite migration story, if the scoping ever
changes, needs it — a migration would drive `RebuildTable` and stamp
`user_version` as it goes.

**Resolution.** `Sqlite.SchemaVersion` (int, default 1). `StampUserVersion` runs
after each `EnsureCreated()` so the schema and the stamp are written together, and
`LogUserVersions` reads all three back on startup and logs them, warning when a
database predates the configured version. That warning is the hook a future
migration would use, and it is the difference between "these files are at version
1" and "these files are at version 0 and nobody knows why".

**Tests:** `SqliteBootstrapperTests`, two cases. The first stamps a non-default
version (7) on purpose — stamping a constant would pass even if the configured
value were ignored — and asserts all three databases carry it. The second asserts
the default is 1 rather than SQLite's 0.

---

## 6. §3.7 — `cache_size` and `journal_size_limit` unset

**Severity:** low
**Status:** fixed
**File:** `Source/ACE.Database/SqlitePragmaInterceptor.cs`, `Source/ACE.Common/SqliteConfiguration.cs`

Both are unset. `journal_size_limit` bounds WAL growth, which matters for a
long-running process; `cache_size` bounds the read working set.

Measured as no speedup (§2), so the case is about bounding worst cases rather
than performance. **Do not expect this one to show a benchmark difference.**

**Fix:** set both, configurable.

**Resolution.** `Sqlite.CacheSize` (default -2000, i.e. 2 MiB, which is SQLite's
own default) and `Sqlite.JournalSizeLimit` (default 100 MiB; -1 for no limit).
Both are applied and read back by the interceptor alongside the other pragmas, so
a failure to apply is reported rather than ignored.

**Tests:** `SqlitePragmaTests.ConnectionOpened_AppliesTheDefaultCacheAndJournalLimits`
asserts both defaults are in effect, and
`ConnectionOpened_PragmaValuesFollowTheConfiguration` asserts they follow the
configuration.

---

## 7. §4.1 — world database is opened read-write at runtime

**Severity:** low
**File:** `Source/ACE.Database/WorldDatabase.cs`

ACE never writes to the world database. It is opened read-write anyway, which
means a bug anywhere in the read path could corrupt the one file a developer
cannot easily regenerate — the pre-converted artifact has to be re-fetched.

**Fix:** open it `Mode=ReadOnly` at runtime.

**Note:** read-only connections interact with the pragma interceptor.
`journal_mode=WAL` on a read-only connection fails with `SQLITE_READONLY` if the
database is not already WAL, and succeeds if it is — which is what
`SqlitePragmaTests.AlreadyWalOnAReadOnlyConnection_IsAccepted` pins. Whoever does
this should re-run those four tests rather than assume they still pass.

---

## 8. `Config.js.docker` has no `Database`/`Sqlite` block

**Severity:** low
**File:** `Source/ACE.Server/Config.js.docker`

The Docker config has no SQLite section, so the Docker path cannot select the
backend. `docker-compose.yml` also has no volume for the `.db` files, which
means a container restart loses the world database and re-downloads it.

Groups naturally with the Docker work rather than with the code fixes.

---

## Not doing, and that is the decision

Not a backlog. Listed so they are not mistaken for oversights.

- **§3.3** replication, PITR, Litestream. Not a development-mode concern.
- **§3.9** the patch pipeline. The gap is real and is the primary reason this
  stays development-scoped. `SqliteDatabaseUpdateProvider` now refuses loudly
  and logs why, which is the right amount of effort.
- **§3.4** `BEGIN IMMEDIATE`. Latent, low impact at development concurrency.
  The one thing worth doing is a comment on `SaveChanges` so the first person to
  add `BeginTransaction` knows.
- **§3.8** high availability. LiteFS or rqlite; not a dev-mode concern.
- **§4.2** huge pages and `page_size`. Measured, no effect, and the reason is
  structural rather than incidental.
