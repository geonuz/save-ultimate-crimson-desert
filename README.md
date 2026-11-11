# SaveUltimate for Crimson Desert

SaveUltimate is an unofficial save editor for Crimson Desert on Windows
x64. This is the C#/.NET edition, a rewrite of the original Python/Qt editor.
Developed and maintained by geonuz with assistance from AI agents.

The interface supports English, Korean, Japanese and Russian. Game names and
descriptions use the game's localization data.

## Supported game versions

| Game version | Status |
|---|---|
| 2.04.08 with or without the first DLC | Verified with this release |
| Earlier 2.x updates | Saves open and edit; the test saves span 2.00.01 to 2.04.08. Syncing game data needs a game install of 2.04.06 or later |
| 1.x | Not supported; opening a save reports `[SU-301]` |

Settings shows the editor version and the game version it was verified with.
Without the DLC installed (no `dlc/dlc_01` in the game folder), the header says
so and DLC items, knowledge, fish, skills, challenges and map places are hidden
and cannot be edited.

## Features

### Items

- **Inventory**: set the quantity of one or several items (1 to 999,999,999) or
  delete them. Items carried by mercenaries are skipped.
- **Equipment**: set the enchantment level up to the item's game limit, including
  mercenary equipment; filter by owner.
- **Sockets**: unlock socket slots up to the equipment's limit and insert,
  replace or remove Abyss Gear.
- **Item Swap (Repurchase)**: replace an item in a vendor's buy-back list with
  another item, then buy it back in the game.
- **Item Database**: browse every item with its icon, key and localized name.

### Adventure Journal

- **Knowledge**: learn or unlearn selected entries, set the level of a learned
  entry, or apply maximum levels to many at once.
- **Challenges**: complete whole challenges, several challenges at once or
  selected checkpoints. Challenges that need a step in the game first, such as
  using a sealed artifact, are view-only.
- **Fishing**: learn fish knowledge and set the size of a recorded catch. One fish
  caught in the game is needed before records can be edited.

### Skills

Skill trees for Kliff, Oongka and Damiane. Raise a skill one level at a time,
paying the game's cost in Abyss Artifacts and Abyss Bonds, or learn it by
observation. Prerequisites and materials are checked first, and levels can only
be raised.

### Map

- **Exploration fog**: reveal the whole map.
- **Activate map groups** to add their discoveries and knowledge:
  - Nodes and places: nodes, regions, hidden spaces, caves, spires, shadow
    sanctums, witch hideouts, Greymane shrines and the Abyss
  - Crafting and utility: bonfires, grindstones, cauldrons, special cooking pots
    and griddles, anvils, wells, looms and workstations
  - Collectibles: treasure chests, memory fragments and bells
  - Resources: 13 kinds of ore and mineral deposits
  - Abyss travel: Abyss Nexus points and Abyss Cressets, including the DLC's

  Items, recipes and resources themselves are not awarded.

### NPC

- **Companions**: view level, skill points and equipment; add or remove Grey-0
  robot workers and festival recruits, even on a save that
  has none yet.
- **Pets & Special Mounts**: rename them and change their order, which the game
  does not offer.
- **NPC Trust**: set trust from 0 to 100 in steps of 5, and raise the DLC's bond
  toward Kliff, Oongka or Damiane. Raising Kliff's bond with a witch also advances
  her "Improve your bond" mission.

### Life

- **Business**: see every DLC sawmill, restaurant, forge and clothier,
  and set the renown (1–5) of the shops you run.

### Other

- **Time**: move the game clock forward by days, hours and minutes. It never
  goes back.

### Backup/Restore

Back up the save before writing it, keep a reference (PRISTINE) copy made the
first time a save is opened, and restore or delete backups.

### Save Doctor (Experimental)

**File > Save Doctor** checks every save slot, or a file you choose, against the
rules this editor knows, without changing anything: the file, its structure,
each page and item values. Each check shows as normal, suspect, error or not
checked. Normal means the rules pass; it does not promise that the game will load
the save.

It repairs only what can be worked out from the file: mission records that do
not follow the game's format, which can crash the game at the title screen even
for a slot that is never loaded, and a signature that no longer matches contents
that still read. You can also lower enchant levels past their limit. Other damage
cannot be repaired, so it offers the newest backup that still opens. The save is
backed up first. Close the game before repairing.

### Editing and saving

Opening a save reads every page at once. Edits appear immediately in the editor;
**Save** writes them to disk. **Ctrl+Z** undoes the last unsaved edit and
**Reset All Changes** reloads the file from disk.

Edits that add, remove or move records are checked against the save's full
structure before they are applied. Saving validates the edited data, writes it,
then reads the written file back and validates it again; a file that fails a
check is never replaced.

The interface offers seven themes and custom colors.

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
*.dll             Native libraries for images and the interface (shipped)
resources/        Baseline catalogs, translations and artwork (shipped)
licenses/         Third-party notices (shipped)
editor_config.json
error.log         Details of reported errors
backups/          Copies of your save backups
game_data/        Synchronized game data and images
.update/          Files of an update in progress, removed at the next start
```

Backups are also written to a `backups/` folder beside each save, including a
`.PRISTINE.bak` copy made the first time a save is opened.

### Updates

When it starts, the editor asks GitHub for the latest release; it sends nothing
about you or your saves, and editing works without a connection. If a newer
release exists, an **Update** button appears above Settings. **File > Check for
Updates** does the same check on demand. Updating replaces the release's files,
keeps your settings, backups and `game_data/`, and restarts the editor. If the
new version does not open its window within two minutes, the previous version is
put back. The download is checked against the hash GitHub publishes; releases are
not code-signed, so Windows may warn when you first run the program. To update
from 1.4.0 or earlier, extract the new ZIP over the old folder once.

Each start also compares the synced data with the installed game and this
release. If either changed, the sync runs again and reuses unchanged images. If
that automatic sync fails, the status bar says so and the existing data stays.

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
Magick.NET and LZ4. Their
notices are included in `licenses/` and Settings → About.

## License

Original SaveUltimate code is available under the [MIT license](LICENSE).
Inherited MPL-2.0 code, third-party components, fonts and game assets retain
their own terms; see [license scope](LICENSES.md). The MIT license does not
authorize redistribution of Pearl Abyss assets.

## Development and support

Build, test and packaging instructions are in [CONTRIBUTING.md](CONTRIBUTING.md).
See [CHANGELOG.md](CHANGELOG.md) for release changes.

Report bugs through Issues with the editor and game versions, whether the DLC is
installed and steps to reproduce the problem. Errors show a code such as
`[SU-301]` with an explanation; include the code and `error.log` from the
program folder, which holds the details. Do not attach personal saves or
credentials.
