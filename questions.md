# Open questions

Decisions I need from you, and facts I could not establish myself. Nothing here
is blocking work already in flight; each item blocks something specific, noted
per item.

---

## 1. Windows x64 — deferred to CI, and the docs say so

**Status:** answered by building the CI workflow, not by a local pass.

Every validation in this branch was macOS arm64. CI is `platform: x64` on
`image: Visual Studio 2026`, and the working build command here is
`-p:Platform=arm64` because `ACE.sln` declares only `Debug|x64` and
`Release|x64`. A Windows run is not possible on this machine — `ACE.sln` cannot
even be *built* for arm64 (MSB4126), and the x64 output cannot execute here
(`Could not find 'dotnet' host for the 'X64' architecture`).

So the Windows x64 pass happens on the PR's first CI run, and the three docs
scope every validation claim to macOS arm64 explicitly.

What writing the workflow settled without needing Windows:

- The `AppContext.BaseDirectory` five-level walk works, but only because
  `bin/x64/Release/net10.0` is the same depth as `bin/arm64/Release/net10.0`.
  Build and test must therefore agree on `-p:Platform`, or the walk overshoots
  to the repository root. See issues.md item 2a — this would have failed on the
  first CI run.
- The world database download, digest check and `NormalizeWorldDatabaseTypes`
  repair were exercised end to end on a fresh directory against the pinned
  artifact. That part is now verified; only the OS underneath is not.
- `ACE.Database.Tests` and its `AppContext.BaseDirectory` path behave
  identically, since it is the same walk at the same depth.

Still genuinely unverified until CI runs: `ResolveSqlitePath` and the `db/`
directory layout on Windows, and path separators in the
`DatabaseSetupScripts/Updates` resolution that `PatchDatabase` now owns. Those
need the real runner.

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

## 5. `docker` CLI hang — resolved

Was hanging past 90 s against the OrbStack socket; it recovered on its own
without restarting OrbStack. `ace-mysql` reports `Up 7 hours`, so the socket
was the problem rather than the container.

The check this blocked is now done: `ace_shard.character` is 0 rows after the
final MySQL run, which is the expected clean state, and `ACE.Database.Tests`
passes 11 / 11 on MySQL with the `EnsureDatabases` call from issues.md item 2d
in place.

**Question:** none.

---

## 6. CI and the `.dat` files — decided

**Decision:** run only `ACE.Database.Tests` in CI. `ACE.Server.Tests` is built
but not executed.

Kept as the reasoning, because the reasoning is what makes the coverage gap in
the workflow interpretable rather than just smaller.

`ACE.Server.Tests.CharacterCreationTests` needs `client_portal.dat`.
`PlayerFactory.Create` reads heritage groups from `DatManager.PortalDat`
unconditionally, so this is core ACE behaviour and **no code change removes the
dependency** — threading `randomizeHeritageAndApperance: false` through to skip
`RandomizeHeritage` does not help, because the very next line in
`PlayerFactory.Create` dereferences `PortalDat` again. Reverted; not worth
revisiting.

The `.dat` set is ~1.2 GB and is Turbine's to distribute.

Worth knowing the shape of the problem: the project's own 13 tests already run
dat-free on AppVeyor. `CharacterCreationTests` is new in this branch and is the
only thing that broke that. Measured with a genuinely empty
`DatFilesDirectory`:

| suite | without `.dat` files |
|---|---|
| `ACE.Database.Tests` | 11 / 11 pass |
| `ACE.Server.Tests` | 20 / 21 pass — only `CharacterCreationTests` fails |

So the dependency is one test — but the suite around it cannot run on a runner
anyway, because of the background-thread crash in issues.md item 2b.

**What this costs.** CI no longer runs `StartupTests` or the end-to-end
character-creation write path, which is the single most valuable test in the
branch. That test still runs, and still passes, on a machine with the `.dat`
files — verified at 21 / 21. The workflow builds the whole solution rather than
just the suite it runs, so a compile break in the untested projects is still
caught.

**What would reverse it.** Hosting the `.dat` files privately and fetching them
with a repo secret is the only route to full coverage on every push. It also
means the job goes red on any fork that lacks the secret, which is a poor
property for a workflow whose purpose is to be reviewable.

**Still open:** if you want the write path covered on every push, the honest
version is a self-hosted runner with the `.dat` files already on disk. No
secret, no redistribution, full coverage.

---

## 7. Should the SQLite world database artifact be re-published?

Related to item 2 above, and found while verifying the CI workflow.

The published `amoeba/ace-to-sqlite` v0.9.295 artifact has damaged numeric
types. `spell.variance` tops out at **99** where a correctly-typed database
reaches **800**, and only 6 rows exceed 9 where 647 should — the upstream
conversion declared 84 numeric columns as `TEXT` across `spell`,
`weenie_properties_emote` and `weenie_properties_emote_action`.

`SqliteBootstrapper.NormalizeWorldDatabaseTypes` repairs all of it on first
open and logs which tables it rebuilt, so this is self-healing and I verified
it end to end: fresh directory, unrepaired artifact, 99 → 800, 43,913 weenies
intact, both suites green.

But it does mean my earlier validation was run against a file I had already
repaired by hand, and the 2.8 M-row differential describes the repaired state
rather than what a first-time download produces. Those agree once the
bootstrapper has run, which is the state any real user reaches — but the docs
should say so rather than leaving the impression that the published artifact
was verified as-is.

**Question:** re-publish the artifact with the conversion fixed upstream, or
leave it and rely on the bootstrapper? Re-publishing is better if
`ace-to-sqlite` is yours to push to.
