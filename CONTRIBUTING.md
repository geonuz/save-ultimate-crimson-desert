# Development

Use Windows x64, Git, the .NET SDK 10.0.402 (or a later 10.0.4xx patch, see
`global.json`), CMake 3.24+ and an x64 C++20 compiler. The release DLLs are
built with MinGW-w64 GCC ([w64devkit](https://github.com/skeeto/w64devkit) works);
Visual Studio 2022 Build Tools also build them. Python is not needed.

```text
src/
  SaveUltimate.App/     WPF application (pages, view models, themes)
  SaveUltimate.Core/    Save container, records, edits, Undo, backups, game-data sync
  SaveUltimate.Tools/   Command-line inspect/extract/texture helpers (not shipped)
native/                 C++ save validator, record relocation and record reader
  src/                  MPL-2.0 sources derived from the original editor
  PORT_CHANGES.md       Changes made for this port
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
tests. It does not compile C++: `native/bin/` holds the committed
`parc_validation.dll`, `parc_index.dll` and `parc_reader.dll`, and builds and
releases ship those files unchanged so every release carries the same tested
DLLs with the same hashes. The current DLLs were built with MinGW-w64 GCC 16.2.0
from w64devkit 2.10.0; 2.11.0 builds the same bytes, 2.9.1 does not.
`native/bin/native-source.sha256` records the sources they were built from, and
the script stops if `native/CMakeLists.txt`, `native/src/` or
`native/third_party/` no longer match it.

After changing those sources, rebuild the DLLs with `-BuildNative` and commit
`native/bin/` with the change. A different compiler produces different files, so
use the same GCC where possible. The DLLs link with `-static` and may import only
Windows libraries; a test fails if one imports anything else, such as the
`libwinpthread-1.dll` that 1.1.2's DLLs needed and PCs without MinGW lack:

```powershell
./scripts/Build.ps1 -BuildNative -Test
# With a MinGW toolchain outside PATH:
./scripts/Build.ps1 -BuildNative -ToolchainDirectory C:/Tools/w64devkit/bin -Test
```

Keep `packages.lock.json` files in sync with
`Directory.Packages.props`; restore with `--force-evaluate` after changing a package.

## Changes and tests

Reuse the existing edit, validation, relocation and Undo paths in
`SaveDocument`. Structural edits must go through `Commit`, which validates the
candidate with the native validator before replacing the document. Add a
focused test for changes to save data or nontrivial behavior.

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
format or values a test needs, not more of the same. The test folder's README describes adding a save.

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
