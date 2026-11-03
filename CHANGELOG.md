# Changelog

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
