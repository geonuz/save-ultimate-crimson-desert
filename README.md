# SaveUltimate for Crimson Desert

SaveUltimate is an unofficial offline save editor for Crimson Desert on Windows
x64. This is the C#/.NET edition, a rewrite of the original Python/Qt editor.
Developed and maintained by geonuz with assistance from AI agents.

The interface supports English, Korean, Japanese and Russian. Game names and
descriptions use the game's localization data. Version 1.1.5 is verified with
Crimson Desert 2.04.05 and its first DLC, and still opens 2.03.02, 2.04.00,
2.04.03 and 2.04.04 saves; the window shows both versions under Settings. Without the DLC
installed (no `dlc/dlc_01` in the game folder), the header says so and DLC
content is hidden and cannot be edited.

## Features

| Page | Features |
|---|---|
| Items | Inventory quantities and deletion, repurchase swaps, equipment enchantment, sockets and Abyss Gear, item database |
| Adventure Journal | Knowledge, challenge and checkpoint completion, fishing records |
| Skills | Kliff, Oongka and Damiane skill trees, level investment and learning by observation |
| Maps | Exploration fog, map discoveries by category, Abyss Nexus travel and the base game's Abyss Cressets |
| NPC | Companions (add or remove Grey-0 robot workers); pet and special mount names and order; NPC trust and bond |
| Backup/Restore | Create, browse and restore save backups |

Opening a save reads every page at once. Edits appear immediately in the editor;
**Save** writes them to disk. **Ctrl+Z** undoes the last unsaved edit and
**Reset All Changes** reloads the file from disk.

Edits that add, remove or move records are checked against the save's full
structure before they are applied. Saving validates the edited data, writes it,
then reads the written file back and validates it again; a file that fails a
check is never replaced.

## Installation

1. Download the Windows ZIP from [Releases](../../releases/latest), extract it to
   a writable folder and run `SaveUltimate.exe`. No .NET installation is needed.
   Keep the `.dll` files beside `SaveUltimate.exe`; the editor does not run
   without them.
2. In Settings, select your Crimson Desert installation and choose **Sync game data**.
3. Choose **Open**, make your edits and use **Save** to save them.

Sync reads the installed game without modifying it. It extracts item information,
localized names and item, fish and skill images locally; images are not downloaded
or bundled with the editor. Placeholder icons appear until the first sync.

Keep `resources/` beside the EXE. The editor stores its settings, backups and
synchronized data in the same folder:

```text
SaveUltimate.exe
*.dll             Native libraries (shipped)
resources/        Baseline catalogs, translations and artwork (shipped)
licenses/         Third-party notices (shipped)
editor_config.json
error.log         Details of reported errors
backups/          Copies of your save backups
game_data/        Synchronized game data and images
```

Backups are also written to a `backups/` folder beside each save, including a
`.PRISTINE.bak` copy made the first time a save is opened. Sync again after a
game update; unchanged data is reused. When updating SaveUltimate, replace the
EXE, the `.dll` files, `resources/` and `licenses/` and keep your settings, backups and `game_data/`.

## Error codes

Errors show a code such as `[SU-101]`, and the full details are appended to
`error.log` beside the EXE. Include both when reporting a problem.

| Code | Meaning |
|---|---|
| SU-101 | A required DLL is missing from the program folder |
| SU-102 | Windows denied access to a required DLL |
| SU-103 | Windows blocked a required DLL as a threat |
| SU-104 | A Windows code integrity policy blocked a required DLL |
| SU-105 | A required DLL is damaged or is not a 64-bit Windows library |
| SU-106 | A library that a required DLL needs is missing (the missing file is named) |
| SU-109 | A required DLL could not be loaded for another reason (the Windows error is shown) |
| SU-201 | A file or folder was not found |
| SU-202 | Access to a file or folder was denied |
| SU-203 | The file is in use by another program |
| SU-204 | Not enough disk space |
| SU-205 | The file path is too long or invalid |
| SU-209 | Another file read or write error |
| SU-301 | The save data could not be read or is not supported |
| SU-302 | The game data could not be read or is not supported |
| SU-303 | The editor refused the operation (the reason is shown) |
| SU-401 | Not enough memory |
| SU-901 | Unexpected error (the error type is shown) |

## Use and copyright

SaveUltimate is unofficial. You assume all responsibility for its use. Correct
operation and data safety are not guaranteed. Back up your saves before editing.

Copyright in Crimson Desert and all game assets and information belongs to
Pearl Abyss.

## Credits

- [NattKh](https://github.com/NattKh/CRIMSON-DESERT-SAVE-EDITOR-AND-GAME-MODS):
  the original editor and CrimsonSaveEditor 2.1.0, which SaveUltimate builds on.
- jacobdyoung20-tech / jkiip: community updates to Save Editor v1.17-v11.
- gek: the original Qt desktop editor and save encryption/decryption work.
- potter4208467 / Potter420 and crimson_rs contributors: Rust parsing and archive
  tools; the archive reader here is derived from crimson-rs. The MIT notice credits Tommy Tran.
- LukeFZ: pycrimson utilities and binary/archive analysis.
- Tooltip: companion name parsing and renaming.
- Hahmlet: The Hahmlet Project Authors / Hypertype, Minjoo Ham and Mark Frömberg.

The editor uses .NET and WPF, CommunityToolkit.Mvvm, K4os.Compression.LZ4,
Magick.NET and LZ4. GCC/MinGW-w64 builds the native save validator. Their
notices are included in `licenses/` and Settings → About.

## License

Original SaveUltimate code is available under the [MIT license](LICENSE).
Inherited MPL-2.0 code, third-party components, fonts and game assets retain
their own terms; see [license scope](LICENSES.md). The MIT license does not
authorize redistribution of Pearl Abyss assets.

## Development and support

Build, test and packaging instructions are in [CONTRIBUTING.md](CONTRIBUTING.md).
See [CHANGELOG.md](CHANGELOG.md) for release changes. Report bugs through Issues
with the editor and game versions, whether the DLC is installed and steps to
reproduce the problem. Do not
attach personal saves or credentials.
