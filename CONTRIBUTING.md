# Development

Use Windows x64, Git, the .NET SDK 10.0.402 (or a later 10.0.4xx patch, see
`global.json`). No C++ compiler or Python is needed.

```text
src/
  SaveUltimate.App/     WPF application (pages, view models, themes)
  SaveUltimate.Core/    Save container, records, edits, Undo, backups, game-data sync
  SaveUltimate.Tools/   Command-line inspect/extract/texture helpers (not shipped)
resources/              Baseline catalogs, UI translations, fonts and original artwork
tests/                  MSTest regression suite
scripts/                Build and verification scripts
```

The application loads `resources/` from beside the EXE. In-app synchronization
writes local updates and images to `game_data/`, which takes precedence over the
baseline data and is never committed.

## Build

From the repository root:

```powershell
./scripts/Build.ps1 -Test
```

The script restores packages in locked mode, builds the solution and runs the
tests.

Keep `packages.lock.json` files in sync with
`Directory.Packages.props`; restore with `--force-evaluate` after changing a package.

## Changes and tests

Reuse the existing edit, validation, relocation and Undo paths in
`SaveDocument`. Every edit builds candidate bytes and goes through `Transact`,
which validates them against the save layout before replacing the document. Add
a focused test for changes to save data or nontrivial behavior.

```powershell
dotnet test tests/SaveUltimate.Tests -c Release --filter "TestCategory!=PrivateFixtures"
```

Tests come in four layers, each a `TestCategory`:

| Layer | What it checks | Needs |
|---|---|---|
| `Unit` | Parsers, formats, error codes | Nothing |
| `Contract` | Rules that hold on any save: `FeatureContracts` lists every feature with its postcondition and the sections it may change | Saves |
| `Scenario` | What only a known save can show: exact values, refusals, boundaries | Saves, game |
| `UI` | Windows and pages, run one at a time | Saves |

A rule that holds on any save belongs in `FeatureContracts`, not in a scenario. A new editor feature needs a contract.
UI tests start the app with `TestUi.EnsureApp`, which mirrors the resources in `App.xaml`, and never open a real dialog:
nothing can click it, so the run stops at the 5-minute limit in `test.runsettings`. Use `TempFolder` for temporary files.
While working, run only the layer you touched, and run everything before a release; the whole suite should stay under
3 minutes:

```powershell
dotnet test tests/SaveUltimate.Tests -c Release --filter "TestCategory=Contract"
```

`Unit` needs no saves or game installation. Tests marked `PrivateFixtures` edit real saves and report Inconclusive unless
their inputs are configured through the environment variables `SAVEULTIMATE_FIXTURES` (a folder of saves) and
`SAVEULTIMATE_GAME` (the installed game). Those inputs are personal data and stay outside Git. Saves are named
`<player>/<game version>_<stage>_<purpose>.save`, with numbered player folders and no real names; tests name the purpose
through `FixtureSaves.Named`. `ContractTests` runs every feature on every save that holds what the feature needs, requires
each feature to reach 3 players, and writes `COVERAGE.md` beside the saves. Keep saves that add a player, a progress stage, a
format or values a test needs, not more of the same. The README in the private fixture folder describes adding a save.

Test classes run side by side. A test that uses WPF or a process-wide static such as `DlcContent.Hidden` must carry
`[DoNotParallelize]`.

Keep saves, extracted game files, settings, backups, credentials and research
outputs out of the repository. `docs/` and `artifacts/` hold private notes and
measurement evidence and are ignored.

Preserve existing copyright notices. Original contributions use MIT; changes to
inherited files retain their existing terms, as listed in [LICENSES.md](LICENSES.md).

## Releases

```powershell
./scripts/Build.ps1 -Test -Publish
./scripts/Build.ps1 -Publish -OutputDirectory ../releases/SaveUltimate-x.y.z
```

`-Publish` creates a self-contained `SaveUltimate.exe` (no .NET installation
needed) with its native `.dll` files beside it in
`releases/SaveUltimate-<timestamp>/` unless `-OutputDirectory` is given. The
folder also contains `resources/`, `licenses/`,
this repository's license and change notes, `dependencies.lock.json` and a
`release-manifest.json` with the SHA-256 of every file. ZIP that folder for
distribution. Builds are unsigned unless you sign them afterwards; record the
final hashes again after signing.

Set `Version` and the verified `GameVersion` in `Directory.Build.props`; the
window shows both under Settings. Add the matching `## [x.y.z]` entry to
`CHANGELOG.md` in the same change.

Every push to `main` runs `.github/workflows/release.yml`, which tests, builds and
publishes a GitHub release tagged `v<version>-<commit>` with
`SaveUltimate-<version>-windows-x64.zip`. Editors in the field read that tag, asset
name and hash, so keep them stable, and raise `Version` whenever users should get a
build: the updater offers only a higher version. The version is also part of the
game-data check, so each new version syncs once at its first start. Keep commits that
change only documents local until the next release.
