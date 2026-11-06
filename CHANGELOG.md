# Changelog

## [1.5.0]

Save Doctor checks a whole save, the editor can update itself, and saves are now read and written in C# instead of by
native libraries. These changes passed the test saves but were not checked in game.

- **New: Save Doctor checks each save.** File > Save Doctor shows the file, its structure, each page and item values as
  normal, suspect, error or not checked, for every slot or a file you choose. Normal means the known rules pass; it does
  not promise that the game will load the save. It repairs what the file itself can settle: mission records, a signature
  that no longer matches readable contents, and on request enchant levels past their limit. Other damage offers the
  newest backup that still opens.
- **New: updates inside the editor.** At start the editor asks GitHub for the latest release and shows an Update button
  when a newer one exists; File > Check for Updates does the same on demand. Nothing about you or your saves is sent.
  The download is checked against its published hash, your settings, backups and game data stay, and if the new version
  does not open its window the previous one is put back. Versions up to 1.4.0 must be updated by hand once.
- Synced game data is checked at every start and synced again when the game or the editor changed. Unchanged images are
  reused. A failed automatic sync shows in the status bar and keeps the data you have.
- `parc_reader.dll`, `parc_index.dll` and `parc_validation.dll` are gone: all save reading, checking and writing is now
  C#. Paths outside the Windows code page work.
- Syncing game data reads only the 2.04 client layout, so an older game install reports an error. Saves are not affected.

## [1.4.0]

Item swap fixes and more challenges. Item swap is verified in game with Crimson Desert 2.04.07. The newly enabled
challenges passed the test saves but were not checked in game.

- **Fixed: every item swap failed with `[SU-301]` on some saves.** The error names `Unrecognized FieldSaveData bytes`
  and has nothing to do with the chosen item. The parser misread an empty field record that saves from 2.03.02 onward
  can hold. The native DLLs are rebuilt from the fixed source, so the hashes of `parc_reader.dll` and `parc_index.dll`
  change in this release.
- Item Swap lists DLC equipment such as the Lumen Sword, which an outdated filter hid. The search now lists only items
  that can hold the sold item's quantity, enchantment, gems and other state, and any other swap is refused.
- Challenges: Garden of Trials, Management and Voyage can be completed. Sea Legends stays view-only because no save has
  cleared it.
- Knowledge status and fishing record sizes use the same green and yellow as challenge tables.
- Bundled game data is refreshed for 2.04.07.
- Tests: every feature is now checked on every test save that holds what it needs, with saves from at least three
  players per feature, one known gap aside.

## [1.3.0]

Fixes a crash caused by this editor, and adds a way to repair saves it damaged. Verified with Crimson Desert 2.04.06
and its first DLC.

- **Fixed: the game could crash at the title screen after completing challenges or activating Abyss Cressets.**
  Mission records this editor added lacked bytes the game expects, so the game read past them. A single such save
  crashes the game even if that slot is never loaded. Saves edited with those two features in earlier versions may be
  affected.
- **New: File > Save Doctor (Experimental).** It checks every save slot for those records and repairs them. Each save
  is backed up first and can be restored from Backup/Restore, and only the damaged records are rewritten. Close the
  game before repairing. Opening a damaged save shows a warning, and challenge or cresset edits on it are refused
  until it is repaired.
- Abyss Cressets: the DLC's 29 cressets can be activated again, together with their challenges. They were left out
  because of the crash above, whose cause was not the cressets. 12 of them use a location offset from other cressets
  because no save records their own. The challenge records for the DLC cressets follow the base game's series.
- Completed challenges now record the same used tags as the game does, per mission.
- Syncing game data works with 2.04.06. The item table layout changed in that update.
- Bundled game data is refreshed for 2.04.06.

## [1.2.1]

Save loading fix. Verified with Crimson Desert 2.04.05 and its first DLC.

- Saves with fish from crew fishing (new in 2.04.05) open again. The game stores those fish in the ship's inventory
  without an item number, which was reported as `[SU-301] Invalid or duplicated physical item.` They are listed as
  read-only items of the ship; items without a number anywhere else are still refused.

## [1.2.0]

Native parser fix. Verified with Crimson Desert 2.04.05 and its first DLC.

- Saves where a list of keys is followed directly by a stored record, such as a shop entry in a save edited by
  another editor, no longer refuse every edit that adds records. Activating map markers failed there with
  `[SU-301] Unparsed inline pointer; insertion requires parser review`. The parser took the record's first byte for
  the list's end marker and lost one internal pointer; every other tested save parses exactly as before.
- The native DLLs are rebuilt from the fixed source with the same toolchain (w64devkit 2.10.0, GCC 16.2.0), so their
  hashes change in this release.
- README lists the supported game versions in a table and what each page can edit. The error code table is gone;
  each error explains its code.

## [1.1.6]

Map page fix. Verified with Crimson Desert 2.04.05 and its first DLC.

- The map page opens for saves whose exploration fog was rewritten by another save editor. Those saves store the
  fog's jump table with 1024 slots instead of one per 64 runs, which was reported as `[SU-301] Invalid fog jump table.`
  The table is still checked slot by slot.
- Error messages from save edits are written in English and shown in Korean in the Korean interface, instead of
  always in Korean.

## [1.1.5]

Skills page fixes. Verified with Crimson Desert 2.04.05 and its first DLC.

- Opening a save no longer marks every skill as changed; only pending edits are marked.
- Learning a skill by observation that is already learned by level is marked as a change.
- Observation no longer refuses skills granted by a quest or a prior skill (Evasive Explosive Shot, Repulsion
  Emblem), and still refuses skills already learned by observation, including ones kept through a skill reset.
- Observation writes the record the game uses: a region demonstration for skills learned from one, combat
  observation for the rest. Double Jump can be learned by observation.
- Skill descriptions in every language show the default keyboard keys, such as "[F + Right Click]", instead of
  action names or "[Skill N]".
- The Skills title sits above the character tabs, and the selected tab keeps its fill after switching pages. Hovering
  a tab only brightens its text.
- Bundled game data is refreshed for 2.04.05.

## [1.1.4]

Cause of "Dll was not found." identified and guarded. Verified with Crimson Desert 2.04.04 and its first DLC.

- The cause of "Operation failed: Dll was not found." was that the `parc_*.dll` files of 1.1.2, and of earlier releases
  built the same way, needed `libwinpthread-1.dll`, which was not shipped and is missing on PCs without MinGW. 1.1.3's DLLs need only
  Windows libraries, and 1.1.4 ships the same files.
- The DLLs now link the MinGW runtime statically with any toolchain, and a test fails the release if one needs a
  library other than Windows'.
- A DLL that is present but needs a missing library is reported as `[SU-106]`, naming the missing file.

## [1.1.3]

Error codes and fixed native libraries. Verified with Crimson Desert 2.04.04 and its first DLC.

- Errors show a code such as `[SU-101]` with a message in the interface language, and the full details are appended to
  `error.log` beside `SaveUltimate.exe`. README lists the codes; include both when reporting a problem.
- When a DLL fails to load, the message names the file and the reason: missing from the program folder, access denied,
  blocked as a threat, blocked by a code integrity policy, or damaged. Earlier versions showed only "Dll was not found."
- Every release now ships the same tested `parc_*.dll` files. Earlier releases recompiled them, so their files changed
  from one release to the next.

## [1.1.2]

Native libraries ship beside the executable. Verified with Crimson Desert 2.04.04 and its first DLC.

- The editor's DLLs are now separate files next to `SaveUltimate.exe` instead of being unpacked into
  `%TEMP%\.net\SaveUltimate` on first run. Some antivirus products blocked the unpacked files, and saves failed to open
  with "Operation failed: Dll was not found." Keep the `.dll` files beside the executable.
- Nothing new is written to `%TEMP%` on startup. Folders left there by earlier versions can be deleted.

## [1.1.1]

Skill description fix. Verified with Crimson Desert 2.04.04 and its first DLC.

- Skill descriptions name the action where the game shows a key prompt ("Press [Roll] while being hit") instead of
  leaving a gap ("Press  while being hit").
- Links to other skills in descriptions show the skill's name; some showed the raw link, such as
  "Knowledge_Evade_Shot#Evasive Shot".

## [1.1.0]

Support for the first DLC (dlc_01) and game updates 2.04.00 to 2.04.04. Verified with Crimson Desert 2.04.04;
2.03.02, 2.04.00 and 2.04.03 saves still open.

Game 2.04 and the DLC
- Game data sync reads the 2.04 archive and table formats and merges installed DLC data: 8,373 items (1,566 new) with
  names in all languages, icons, and enchantment and socket rules. Bundled item data is refreshed from 2.04 for use
  before the first sync.
- Knowledge, skills, fishing and characters follow 2.04 and the DLC: 4,596 journal entries (657 new), 311 skills
  (33 new) on the updated skill trees, four new legendary fish, 7,600 character names and NPC titles.
- Challenges add the "Unknown Journey" category and the DLC missions. 417 of 445 challenges can be completed; the other
  28 DLC challenges are view-only until one challenge of their series is completed in game.
- The Map page reads and reveals the enlarged 2.04 exploration grid. Bulk actions cover the DLC region and the regions,
  caves and memory fragments added in 2.04, and Abyss Nexus travel covers 293 nexuses (27 from the DLC). The Abyss
  Cresset action covers the 101 base-game cressets only: the DLC's 29 crashed the game at the title screen when
  activated with estimated positions.
- Without the DLC installed (no `dlc/dlc_01` in the game folder), the header says so and DLC items, knowledge, fish,
  skills, challenges and map placements are hidden and refused by every edit.

NPC
- Companions, Pets & Special Mounts, and NPC Trust are separate tabs. Companions are view-only with level, skill points
  and equipment, except Grey-0 robot workers, which can be added and removed.
- Pets & Special Mounts has a tab per category; pets and special mounts can be moved up or down, which the game does
  not offer. Names are edited on this page, and every record shows its character key and instance number.
- NPC Trust leaves out pets, mounts and entries that only record a threat reward (wanted criminals, the culprit
  Corbin), and edits the extended trust stage the DLC adds (친밀도(확장), English "bond") per character, on a tab
  each for Kliff, Oongka and Damiane. Values the game left between steps of five are kept, and bonds can only be raised.
- Editing Kliff's bond with a witch also sets her "Improve your bond" mission in progress to the bond, up to its target
  of 40. Talking to her once in the game makes the quest pick up the new value. Other characters' bonds are left as
  they are, although the game lowers them when one character's bond rises.

Interface
- Wording follows the game in every language (Trust and Bond in English, 好感度 and 絆 in Japanese, Доверие and
  Симпатия in Russian), and character lists read Kliff, Oongka, Damiane everywhere.
- Items: the Equipment tab filters by owner, and the Money filter also lists currencies kept in the inventory, such
  as the DLC's Ancient Relics. Item columns read "item key" and "instance number".
- Tables keep every column header readable in all four languages, fixed-width columns no longer shrink below their
  designed width, and clipped text ends in "…" with the full text as a tooltip.
- Search boxes filter once typing pauses; a challenge search that matched every challenge went from 0.9 s to 0.1 s.
  Knowledge search no longer narrows its columns.
- Skills: a skill learned by investing can still be observed. The skill tabs line up with the other menus.

Development
- Tools `refresh-items`, `catalog-challenges`, `challenge-completion` and `catalog-knowledge` regenerate the bundled
  resources from an installed game, and a test checks the shipped resources against it.
- Tests no longer compare against the archived Python editor; private-save tests check each edit by reading it back,
  saving and undoing.

## [1.0.0]

First release of the C#/.NET edition, replacing the Python/Qt editor. Verified with Crimson Desert 2.03.02.

- Self-contained single-file Windows build; Python, Qt and Rust are no longer required.
- Inventory quantities and deletion, repurchase swaps, enchantment, sockets and Abyss Gear, item database.
- Knowledge, challenges and checkpoints, fishing records and skill trees.
- Map exploration, companion names and NPC friendship.
- All pages are read when a save is opened.
- Save backups, PRISTINE copies, restoration and exact Undo for unsaved edits.
- Native validation of structural edits and of every written save.
- Local game-data sync with translated names and item, fish and skill images, available under Save once the game folder is detected and recommended on first run.
- The header marks unsaved changes in the warning color and a completed save in the success color.
- English, Korean, Japanese and Russian interfaces with the original seven themes.
- The quest database, Raw Stream (.bin) inspection and research tools of the Python editor are not included.
