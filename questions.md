# Open questions

Decisions I need from you, and facts I could not establish myself. Nothing here
is blocking work already in flight; each item blocks something specific, noted
per item.

---

## 1. Windows x64 is unvalidated

**Blocks:** confidence in the whole branch, and the §3.10 fix.

Every validation so far was macOS arm64. CI is `platform: x64` on
`image: Visual Studio 2026`, and the working build command here is
`-p:Platform=arm64` because `ACE.Server.csproj` declares
`<Platforms>x64</Platforms>`.

The specific things that need a real Windows run:

- The `AppContext.BaseDirectory` paths in `ACE.Server.Tests` and
  `ACE.Database.Tests` were written to sidestep the §3.10 CWD bug. Whether
  they behave the same on Windows is unverified.
- `DbProvider.ResolveSqlitePath` and the whole `db/` directory layout.
- `SqliteBootstrapper.EnsureCreated` and the world database download, on
  first run, cold.
- Path separators in the `DatabaseSetupScripts/Updates` resolution that
  `MySqlDatabaseUpdateProvider.PatchDatabase` now owns.

**Question:** do you want me to run a Windows x64 validation pass before we go
further, or is CI on the eventual PR the intended place for that? If the latter,
every claim in the three docs is currently scoped to macOS arm64 and should say
so explicitly.

---

## 2. Who owns publishing the SQLite world database artifact

**Blocks:** merge. This is the most likely reason a merge stalls.

The SQLite backend takes its world database as a pre-converted `.db` file from
`https://acedb.treestats.net` (referenced at
`Source/ACE.Common/SqliteConfiguration.cs:36`), not as a MySQL dump. For the
upstream project, that artifact would need to be published as a release asset
alongside the `.sql.zip` in
[`ACEmulator/ACE-World-16PY-Patches`](https://github.com/ACEmulator/ACE-World-16PY-Patches).

This is not a code change and I cannot resolve it. It needs a maintainer
decision, and possibly a volunteer to own the conversion on each world release.
Until someone does, a merged PR points at a third-party repository that the
project does not control and that could disappear.

**Question:** is that a conversation you want to have with the maintainers
before offering the PR at all, or is it acceptable to offer it with the
dependency stated plainly in the description?

---

## 3. The discarded `ace_shard` characters

Asked previously, still unanswered, and I want to flag it once more because it
is the only open item where I may have destroyed something.

The original Homebrew `ace_shard` had characters in it. That MySQL instance is
torn down and the OrbStack `ace-mysql` shard is a fresh import that does **not**
have them. `ace_shard.character` currently has 0 rows.

**If that data was real play progress, it is gone** unless it was backed up
somewhere I do not know about. I have no record of a dump being taken before the
teardown, which is the part I should have been more careful about.

**Question:** did it matter? If yes, is there a backup, and should I add
"take a dump before tearing down a database" to the setup doc?

---

## 4. Leftover local state — your call

Both are harmless, neither is blocking.

- `/opt/homebrew/var/mysql` — 688 MB, server stopped, no longer referenced.
  Deleting frees the space.
- The `ace-mysql` OrbStack container — needed to run the MySQL half of the test
  matrix. Keep it running for now; it costs nothing but memory.

**Question:** delete the Homebrew datadir, and do you want the container left up
or stopped at the end of the session?

---

## 5. `docker` CLI is currently hanging

Noted for accuracy, not a question so much as a caveat on the last verification
pass.

`docker ps` and `docker exec` both hung past 90 s against the OrbStack socket,
after working earlier in the session. I could not confirm `ace_shard` row count
after the final MySQL test run. The suites passed, and they clean up after
themselves, so I expect 0 rows — but I did not verify it on that pass, and I
would rather say so than imply otherwise.

**Question:** none. Worth knowing that the MySQL half of the next verification
pass may need OrbStack restarted first.
