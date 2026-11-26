# Development

Use Windows x64, Git, the .NET SDK 10.0.402 (or a later 10.0.4xx patch, see
`global.json`). No C++ compiler or Python is needed.

```text
src/
  CDSE.App/             WPF application (pages, view models, themes)
  CDSE.Core/            Save container, records, edits, edit history, backups, game-data sync
  CDSE.Tools/           Command-line helpers and the builders of the baseline catalogs (not shipped)
resources/              Baseline catalogs, UI translations, fonts and original artwork
tests/                  MSTest suite that needs no saves
scripts/                Build script
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

Reuse the existing edit, validation, relocation and edit history paths in
`SaveDocument`. Every edit builds candidate bytes and goes through `Transact`,
which validates them against the save layout before replacing the document. Add
a focused test for changes to save data or nontrivial behavior.

```powershell
dotnet test tests/CDSE.Tests -c Release
```

These tests need no saves. Each carries a `TestCategory`:

| Category | What it checks | Needs |
|---|---|---|
| `Unit` | Parsers, formats, catalogs, settings, error codes | Nothing |
| `UI` | Windows and dialogs that open without a save | Nothing |
| `Game` | The shipped catalogs against the installed game | `CDSE_GAME`, the game folder |

`Game` tests report Inconclusive without the game. UI tests start the app with `TestUi.EnsureApp`, which mirrors the
resources in `App.xaml`, and never open a real dialog. `TestRun` closes one that opens and fails the run with its title;
a Windows file dialog it cannot close, so the run stops at the 5-minute limit in `test.runsettings`. Use `TempFolder` for
temporary files. The whole suite should stay under 3 minutes.

Edits are also tested on real saves. Saves are personal data, so those tests and the saves stay outside the repository.

Test classes run side by side. A test that uses WPF or a process-wide static such as `DlcContent.Hidden` must carry
`[DoNotParallelize]`.

Keep saves, extracted game files, settings, backups, credentials and research
outputs out of the repository. Private notes and measurement evidence are kept
outside it.

Preserve existing copyright notices. Original contributions use MIT; changes to
inherited files retain their existing terms, as listed in [LICENSES.md](LICENSES.md).

## Releases

```powershell
./scripts/Build.ps1 -Test -Publish
./scripts/Build.ps1 -Publish -OutputDirectory releases/CDSE-x.y.z
./scripts/Build.ps1 -Publish -NoOnlineUpdate   # Nexus Mods package
```

`-Publish` creates a self-contained `CDSE.exe` (no .NET installation
needed) with its native `.dll` files beside it in
`releases/CDSE-<timestamp>/` unless `-OutputDirectory` is given. The
folder also contains `resources/`, `licenses/` (the notices in this repository's
`licenses/` plus those of .NET and the NuGet packages), `LICENSE`, `LICENSES.md`,
`README.md`, `CHANGELOG.md`, `dependencies.lock.json` and a
`release-manifest.json` with the SHA-256 of every file. ZIP that folder for
distribution. Builds are unsigned unless you sign them afterwards; record the
final hashes again after signing.

Set `Version` and the verified `GameVersion` in `Directory.Build.props`; the
window shows both under Settings. Add the matching `## [x.y.z]` entry to
`CHANGELOG.md` in the same change, and the same entry in every other interface
language in `changelog/<code>.md`. An entry lists what users notice, briefly,
under `### New`, `### Fixed` and `### Other` in that order; a fix to something new
in the same release is not listed. The release workflow refuses a version that a
language lacks, and uploads the entries as `release-notes.json`, from which the
updater shows the notes in the editor's language.

Every push to `main` runs `.github/workflows/release.yml`, which tests, builds and
publishes a GitHub release tagged `v<version>-<commit>` with three packages:
`CDSE-<version>-windows-x64.zip` (updates itself from GitHub),
`CDSE-<version>-windows-x64-noupdate.zip` (no online updates) and
`SaveUltimate-<version>-windows-x64.zip`, the same program as the first named
`SaveUltimate.exe` (`Build.ps1 -ExecutableName SaveUltimate.exe`).
Editors in the field read that tag and the updater package's
name and hash, so keep them stable, and raise `Version` whenever users should get a
build: the updater offers only a higher version. Versions up to 1.9.0 were named
SaveUltimate: their updater takes only the `SaveUltimate-` package, requires
`SaveUltimate.exe` in it and starts that, then waits for the
`SAVEULTIMATE_UPDATE_STARTED` signal, which `AppUpdate.ReportStarted` still answers.
A folder updated that way moves to `CDSE.exe` on its next update. Keep publishing the
`SaveUltimate-` package as long as such versions may still update. From 1.10.0 the updater reads the
release by repository ID, so a renamed repository does not affect it; older versions rely on GitHub's
redirect from the old name, so never create a new repository under that name. Dye and house files are
still written as `.sudye`/`.suhouse` so that 1.9.0 can open them; 1.10.0 also reads the `CDSE.Dye`/`CDSE.House`
formats (`.cdsedye`/`.cdsehouse`) that a later version will write. The version is also part of the
game-data check, so each new version syncs once at its first start. Keep commits that
change only documents local until the next release.

### Online updates

All update code (check, download, install, the Update button and the **Check for
Updates** menu item) lives in `src/CDSE.App/Updates/`. The rest of the app
calls it only through partial methods (`App.StartUpdates`,
`MainWindow.AddUpdateControls`, `MainWindow.LabelUpdateButton`), which the compiler
drops when their implementation is missing. The `OnlineUpdate` property in
`Directory.Build.props` (default `true`) decides whether that folder and
`AppUpdateTests.cs` are compiled; `-NoOnlineUpdate` passes `-p:OnlineUpdate=false`
and then fails the build if the EXE still contains `AppUpdate` or the GitHub API
address. That package also leaves out `README.md` and `CHANGELOG.md`, which
describe the GitHub release and updater; the Nexus Mods page and its Changelogs
tab take their place. Keep new update code inside `Updates/` and reach it through a partial
method, so the Nexus Mods build stays free of network code.
