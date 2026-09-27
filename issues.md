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
**File:** `AppVeyor/` — nothing to fix, something to add

`AppVeyor/Config.js` has no `Database.Provider` key, so `DbProvider.Active`
resolves to MySQL and the entire SQLite backend is dead code in the project's
own pipeline.

This is the item that gates confidence in everything else on this list. Every
fix below is unverified by CI until this job exists, and a reviewer has no
mechanism for checking that a later change did not break the second backend.

**Fix:** a CI job that runs `ACE.Database.Tests` and `ACE.Server.Tests` with
`"Provider": "sqlite"`. Both suites read `Config.js`, so the same assertions
must pass on both providers — a change that only passes one is not done.

Also worth doing at the same time: a second job, or a matrix, that runs the
existing suites on MySQL so the baseline stays covered. The current job already
does that implicitly; making it explicit in a matrix keeps it from being
accidentally dropped.

---

## 3. §3.2 — `Cache=Shared` defeats WAL; `SQLITE_LOCKED` is unretriable

**Severity:** medium
**File:** `Source/ACE.Common/SqliteConfiguration.cs:89`

```csharp
ConnectionOptions = "Cache=Shared;Foreign Keys=True;Default Timeout=30"
```

`Cache=Shared` is a testing and single-process-coordination option. It defeats
some of the benefit of WAL, and it interacts badly with `busy_timeout` — which is
the thing that actually makes concurrent access safe. A `SQLITE_LOCKED` in
particular is not retriable the way `SQLITE_BUSY` is.

**Fix:** `Cache=Shared` → `Pooling=True`.

**Test:** re-run both suites, plus **a new concurrency soak** asserting zero
`SQLITE_BUSY` / `SQLITE_LOCKED` under contention. Neither existing suite creates
any contention between connections, which is precisely why this defect has gone
unnoticed. This is the most substantial new test on the list and it is the one
that would honestly close the item.

---

## 4. §3.5 — `synchronous=NORMAL` is hardcoded

**Severity:** medium
**File:** `Source/ACE.Database/SqlitePragmaInterceptor.cs:20-22`

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

---

## 5. §3.6 — no `user_version` stamp

**Severity:** low
**File:** `Source/ACE.Database/SqliteBootstrapper.cs`

`user_version` is 0 in all three files, so nothing records which version a given
`db/` directory is at. The consequence is entirely about supportability: someone
reports a problem, and there is no way to tell them which state their files are
in.

**Fix:** stamp `PRAGMA user_version` on creation and verify it on startup. Two
lines. Worth doing because the SQLite migration story, if the scoping ever
changes, needs it — a migration would drive `RebuildTable` and stamp
`user_version` as it goes.

---

## 6. §3.7 — `cache_size` and `journal_size_limit` unset

**Severity:** low
**File:** `Source/ACE.Database/SqlitePragmaInterceptor.cs`

Both are unset. `journal_size_limit` bounds WAL growth, which matters for a
long-running process; `cache_size` bounds the read working set.

Measured as no speedup (§2), so the case is about bounding worst cases rather
than performance. **Do not expect this one to show a benchmark difference.**

**Fix:** set both, configurable.

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
