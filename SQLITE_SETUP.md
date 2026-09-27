# Setting up ACE with SQLite

How to run ACEmulator on SQLite instead of MySQL. No database server, no
`DatabaseSetupScripts`, no `mysqldump` — three files in a directory.

**SQLite is the optional development and private-test backend. MySQL remains
the supported target for public shards**, and it is still the default. The
reason for that boundary is not performance — see §2 of
[`SQLITE_PRODUCTION.md`](SQLITE_PRODUCTION.md) — it is that SQLite gets no
automatic database or world database updates, and has no replication.

For the design rationale and validation results see
[`SQLITE_BACKEND.md`](SQLITE_BACKEND.md).

---

## 1. When to use this

Use SQLite when you are:

- developing ACE itself, or a server emulator, on one machine
- running a private test world for a handful of people
- evaluating ACE without wanting to install and administer MySQL
- testing on a machine where you cannot run a database daemon

Use MySQL when players would be upset to lose progress on. Specifically, on
SQLite:

- **`Database/Updates/` is never applied.** The automatic database update and
  world database update pipelines are MySQL-only. A long-running world will
  drift behind schema and data changes as ACE is upgraded, with nothing in the
  log to tell you. If you need a current world, rebuild the files or point
  `WorldDatabaseUrl` at a release matching your `.dat` files.
- **There is no replication or point-in-time recovery.** `VACUUM INTO` takes a
  consistent snapshot of a live database (§8), which is enough for a local
  copy, but a lost disk is a lost shard.
- **The world database is a fetched artifact,** not one built from the `.dat`
  files, so it can lag the first-party world release.

ACE logs these limitations at startup whenever SQLite is selected, so a running
server will always tell you which backend it is on and what it is missing.

---

## 2. Requirements

| | |
|---|---|
| .NET | 10.0 SDK |
| Client `.dat` files | `client_cell_1.dat`, `client_portal.dat`, `client_local_English.dat` |
| Disk | ~400 MB free (137 MB world database, plus build output and logs) |
| Disk type | **local disk only.** See the warning below. |

> **The database files must be on a local filesystem.** Not NFS, not SMB, not a
> Docker bind mount from a network share, not most object-storage FUSE mounts.
> SQLite's locking relies on byte-range locks that network filesystems
> implement loosely or not at all, and the result is a silently corrupted
> database. This is the single most important rule in this document.
>
> A local bind mount into a container is fine. A *network* mount is not.

Point `Server.DatFilesDirectory` in `Config.js` at the directory containing the
`.dat` files.

---

## 3. Quick start

```bash
git clone <your fork> ACE
cd ACE/Source

dotnet build ACE.Server/ACE.Server.csproj -c Release

cp ACE.Server/Config.js.example ACE.Server/Config.js
```

Edit `ACE.Server/Config.js`:

1. Set `Server.DatFilesDirectory` to your `.dat` directory.
2. Set the database provider:

```js
  "Database": {
    "Provider": "sqlite"
  },
```

Nothing else is required. The `Sqlite` section already has working defaults
(`db/ace_auth.db`, `db/ace_shard.db`, `db/ace_world.db`).

Run it:

```bash
cd ACE.Server/bin/Release/net10.0
ACE_NONINTERACTIVE_SETUP=true dotnet ACE.Server.dll
```

First run creates the auth and shard schemas and downloads the world database.
**Expect two to five minutes**, most of it the 137 MB download. Progress looks
like:

```
[SQLITE] auth  -> /path/to/db/ace_auth.db
[SQLITE] shard -> /path/to/db/ace_shard.db
[SQLITE] world -> /path/to/db/ace_world.db
[SQLITE] Created ace_auth schema from the EF model.
[SQLITE] Seeded 6 access levels.
[SQLITE] Created ace_shard schema from the EF model.
[SQLITE] Downloading world database from https://github.com/amoeba/ace-to-sqlite/...
[SQLITE] This is ~130MB and may take a few minutes on first run.
[SQLITE] Downloaded 137.0 MB.
[SQLITE] Identified 3 world table(s) with TEXT-affinity numeric columns.
[SQLITE] Rebuilt spell with N corrected column type(s).
[SQLITE] Rebuilt weenie_properties_emote with N corrected column type(s).
[SQLITE] Rebuilt weenie_properties_emote_action with N corrected column type(s).
[SQLITE] Rebuilt 3 world table(s) to restore numeric column types.
[SQLITE] World database contains 43,913 weenies and 6,266 spells.
[SQLITE] World database ready.
```

Every subsequent run skips all of that — you will see only the three path
lines and `[SQLITE] World database numeric column types are already correct;
nothing to rebuild.` The type-repair block appears once, on the run that first
lands the world database, and repairs 84 columns across those three tables. It
exists because the pre-converted artifact stores them with SQLite's TEXT
affinity, which silently breaks numeric comparisons and sorts: `spell` is
mostly numeric, and before the repair `SELECT COUNT(*) FROM spell WHERE
variance > 9` returned 6 rows instead of 647. The `N` values above are per
table and vary with the artifact revision.

### On Apple Silicon

The csproj pins `<Platforms>x64</Platforms>`, and an x64 build will not run on
arm64 macOS. Add `-p:Platform=arm64` rather than editing the csproj:

```bash
dotnet build ACE.Server/ACE.Server.csproj -c Release -p:Platform=arm64
cd ACE.Server/bin/arm64/Release/net10.0
```

---

## 4. The configuration, annotated

```js
  // Which backend to use: "mysql" (default) or "sqlite".
  // Any unrecognised value falls back to mysql.
  "Database": {
    "Provider": "sqlite",

    // Create the auth/shard schemas and fetch the world database on startup if
    // the files are missing. Set false to manage the files yourself.
    "AutoCreate": true
  },

  "Sqlite": {
    "Authentication": { "Database": "db/ace_auth.db" },
    "Shard":         { "Database": "db/ace_shard.db" },
    "World": {
      "Database": "db/ace_world.db",

      // Where to fetch the pre-converted world database from.
      // Set to "" to supply your own instead.
      "WorldDatabaseUrl": "https://github.com/amoeba/ace-to-sqlite/releases/download/latest/ace_world_patches.db"
    }
  },
```

Paths may be relative or absolute. A relative path is resolved against the
directory holding `Config.js` and the ACE executable — not the directory you
launch from — so the database files stay where the config put them when ACE is
started from elsewhere (a service manager, a scheduled task, a shell that has
`cd`'d). Use an absolute path to place them somewhere else deliberately.

Per-database diagnostic flags are available and default to off:

```js
    "Shard": {
      "Database": "db/ace_shard.db",
      "EnableDetailedErrors": false,
      "EnableSensitiveDataLogging": false
    },
```

Turn on `EnableDetailedErrors` when you are debugging a query problem — SQLite
errors are terser than MySQL's and this makes a much larger difference than it
does on the MySQL path.

### Using your own world database

The world database is 43,913 weenies and 6,266 spells and is what every
character, spell, item and landblock is created from. It is the one artifact
you cannot generate yourself from scratch — it comes from the game's data files,
converted ahead of time by
[`amoeba/ace-to-sqlite`](https://github.com/amoeba/ace-to-sqlite).

To use your own copy, drop it where you want it, set the path, and disable the
download:

```js
    "World": { "Database": "/srv/ace/ace_world.db", "WorldDatabaseUrl": "" }
```

You can also convert one yourself from an existing MySQL `ace_world` with
`amoeba/ace-to-sqlite`. ACE will verify the file on startup and log the weenie
and spell counts; if they come back as 0, the file is not a world database.

---

## 5. Verifying it worked

Look for all of these in the startup log:

```
[SQLITE] World database contains 43,913 weenies and 6,266 spells.
Database provider is SQLite; skipping MySQL world/database patch pipeline.
Binding ConnectionListener to 0.0.0.0:9000
Binding ConnectionListener to 0.0.0.0:9001
... says on the Audit channel, "World is now open"
```

If you see the world open and no `ERROR`/`FATAL` lines, you are running on
SQLite. `Database provider is SQLite; skipping MySQL world/database patch
pipeline.` is expected and not a warning — those steps replay
`DatabaseSetupScripts/*.sql` through MySQL-specific code and have no SQLite
equivalent.

Quick check from a shell:

```bash
cd ACE.Server/bin/Release/net10.0/db
sqlite3 ace_world.db "SELECT COUNT(*) FROM weenie;"     # 43913
sqlite3 ace_shard.db "SELECT COUNT(*) FROM biota;"     # 0 on a fresh server
sqlite3 ace_auth.db  "SELECT COUNT(*) FROM accesslevel;" # 6
```

---

## 6. Creating an account

There is no admin account in a fresh database. ACE promotes the first account
created to Admin automatically, so create one and it is immediately an admin.

In-game, as any account (this is the normal player registration path — the
client asks for a new account when the name is unknown):

```
accountcreate <username> <password> [accesslevel]
```

`accesslevel` is optional and defaults to `Server.Accounts.DefaultAccessLevel`:

| | |
|---|---|
| 0 | Player |
| 1 | Advocate |
| 2 | Sentinel |
| 3 | Envoy |
| 4 | Developer |
| 5 | Admin |

The command requires Admin rights, so the account-creation path that also works
is the server console — the prompt is enabled when ACE is launched with a
terminal attached (omit `ACE_NONINTERACTIVE_SETUP` and do not redirect stdin):

```
accountcreate myadmin mypassword 5
```

You should see:

```
Account successfully created for myadmin (1) with access rights as an Admin.
```

If instead you see this at startup, no admin exists yet and the next account
created will be promoted:

```
Authentication Database does not contain any admin accounts. The next account
to be created will automatically be promoted to an Admin account.
```

The world opens on its own at startup. To close it to players while you work on
it, `world close`; reopen with `world open`.

---

## 7. Where the files are, and moving them

By default all three databases land in `db/` next to the running binary, each
with two siblings:

```
db/
  ace_auth.db        the real data
  ace_auth.db-wal    write-ahead log
  ace_auth.db-shm    shared-memory index for the WAL
  ace_shard.db
  ace_shard.db-wal
  ace_shard.db-shm
  ace_world.db
  ace_world.db-wal
  ace_world.db-shm
```

The `-wal` and `-shm` files are normal. ACE checkpoints and removes them when
the connection closes cleanly; a non-zero `-wal` after a crash is replayed on
the next open.

**To move a database, stop ACE first**, then move the `.db` and both siblings
together. Never copy the files while the server is running — see the warning in
§8.

---

## 8. Backing up

> **Do not `cp` a SQLite database while ACE is running.** Copying a live
> database can produce a backup that is subtly corrupt, and you will only find
> out when you need it. This is the most common way people lose data with
> SQLite.

Stop ACE, or use SQLite's own backup command, which is safe to run against a
live database:

```bash
# safe against a running server, per database
sqlite3 db/ace_shard.db "VACUUM INTO '/backups/ace_shard-$(date +%F).db';"
sqlite3 db/ace_auth.db  "VACUUM INTO '/backups/ace_auth-$(date +%F).db';"
```

`VACUUM INTO` writes a consistent snapshot and compacts it as a side effect. The
world database is almost never modified, so a stopped-server file copy of just
`ace_world.db` is fine.

Verify a backup before trusting it:

```bash
sqlite3 /backups/ace_shard-2026-09-26.db "PRAGMA integrity_check;"   # must print: ok
```

To restore, stop ACE and put the file back.

For continuous replication and point-in-time recovery, use
[Litestream](https://github.com/benbjohnson/litestream) — see the production
doc for the configuration.

---

## 9. Running the tests

The suites read `Source/ACE.Server/Config.js`, so they test whichever provider
you have configured.

```bash
cd Source
dotnet test ACE.Database.Tests/ACE.Database.Tests.csproj -c Release
dotnet test ACE.Server.Tests/ACE.Server.Tests.csproj  -c Release
```

Expected: 7/7 and 13/13. `ACE.Server.Tests` includes an end-to-end character
creation test that writes a full level-275 character with starter gear and then
deletes it again, so the shard is unchanged afterwards.

On Apple Silicon add `-p:Platform=arm64`.

The dat-file tests are unrelated to the database and need the client files:

```bash
ACE_DAT_PATH=/path/to/dats dotnet test ACE.DatLoader.Tests/... -c Release
```

---

## 10. Going back to MySQL

Set the provider back:

```js
  "Database": { "Provider": "mysql" }
```

Nothing else changes; the MySQL path is untouched and the `MySql` section of
`Config.js` is still there. Migrating player data between the two is **not**
supported — there is no export/import path. If you want to keep what you have,
stay on the provider you started with.

The SQLite files can be deleted afterwards. Deleting `db/ace_shard.db` and
`db/ace_auth.db` discards all characters and accounts.

---

## 11. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `no such table: ...` on startup | shard/auth database file exists but is empty or truncated | delete the `.db`, `-wal`, `-shm` and restart so it is recreated |
| `World database contains 0 weenies` | `ace_world.db` is not a world database, or the download was interrupted | delete `ace_world.db` and restart, or supply your own |
| `SQLITE_BUSY` / `database is locked` under load | two writers collided and the wait was exhausted | see below |
| `attempt to write a readonly database` | file or directory permissions | ACE needs write access to the database *and* its directory (for `-shm`) |
| `disk I/O error`, `database disk image is malformed` | database is on a network filesystem, or was corrupted there | move it to local disk and re-download the world database |
| Account name lookups fail | names are stored and compared case-insensitively; make sure you did not hand-edit the schema | — |
| Port 9000/9001 already in use | an earlier ACE is still running | `pkill -f ACE.Server.dll` |
| Server exits immediately, log stops at the console prompt | stdin was closed | run it from a terminal, or ignore this if you launched with a pipe |

### `SQLITE_BUSY` under load

ACE waits up to 30 seconds for a competing writer before giving up, so
occasional contention is invisible. If you do see `database is locked`:

1. Confirm the pragmas actually applied. Run
   `sqlite3 db/ace_shard.db "PRAGMA journal_mode; PRAGMA busy_timeout;"` —
   `journal_mode` must be `wal` and `busy_timeout` must be `30000`. If either
   is wrong, see the silent-failure note in
   [`SQLITE_PRODUCTION.md`](SQLITE_PRODUCTION.md) §3.1; that is a bug worth
   reporting rather than working around.
2. Check the files are on local disk (§2).
3. Check the disk is not full. SQLite needs room for the WAL on top of the
   database.

---

## 12. What SQLite does not do here

So there are no surprises later:

- **No world database updates.** `Offline.AutoUpdateWorldDatabase` and
  `Offline.AutoApplyDatabaseUpdates` are MySQL-only. Leave them on and the boot
  log explains why they did not run; the world database itself is not touched.
  To move to a newer world database you replace the file. Custom
  `Database/Optional/World/*.sql` scripts do not run under SQLite.
- **No replication or point-in-time recovery** without an external tool
  (Litestream).
- **One writer at a time per database file.** The three files give ACE three
  independent writer locks, and ACE's write rate is nowhere near the limit, but
  there is no row-level or table-level concurrency.
- **No foreign keys in the pre-converted world database.** The auth and shard
  schemas ACE creates itself do have them.
- **The collation differs.** See `SQLITE_BACKEND.md` §2.2 if you ever hand-write
  SQL against these databases.
