# SaveUltimate for Crimson Desert

SaveUltimate is an unofficial offline save editor for Crimson Desert on Windows
x64. This is the C#/.NET edition, a rewrite of the original Python/Qt editor.
Developed and maintained by geonuz with assistance from AI agents.

The interface supports English, Korean, Japanese and Russian. Game names and
descriptions use the game's localization data. Version 1.0.0 is verified with
Crimson Desert 2.03.02; the window shows both versions under Settings.

## Features

| Page | Features |
|---|---|
| Items | Inventory quantities and deletion, repurchase swaps, equipment enchantment, sockets and Abyss Gear, item database |
| Adventure Journal | Knowledge, challenge and checkpoint completion, fishing records |
| Skills | Kliff, Damiane and Oongka skill trees, level investment and learning by observation |
| Maps | Exploration fog and all map discovery actions |
| NPC | Companion, pet and mount names; NPC friendship |
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
2. In Settings, select your Crimson Desert installation and choose **Sync game data**.
3. Choose **Open**, make your edits and use **Save** to save them.

Sync reads the installed game without modifying it. It extracts item information,
localized names and item, fish and skill images locally; images are not downloaded
or bundled with the editor. Placeholder icons appear until the first sync.

Keep `resources/` beside the EXE. The editor stores its settings, backups and
synchronized data in the same folder:

```text
SaveUltimate.exe
resources/        Baseline catalogs, translations and artwork (shipped)
licenses/         Third-party notices (shipped)
editor_config.json
backups/          Copies of your save backups
game_data/        Synchronized game data and images
```

Backups are also written to a `backups/` folder beside each save, including a
`.PRISTINE.bak` copy made the first time a save is opened. Sync again after a
game update; unchanged data is reused. When updating SaveUltimate, replace the
EXE, `resources/` and `licenses/` and keep your settings, backups and `game_data/`.

On first start Windows unpacks the editor's native libraries into
`%TEMP%\.net\SaveUltimate\`; this is normal for single-file .NET applications.

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
with the editor and game versions and steps to reproduce the problem. Do not
attach personal saves or credentials.
