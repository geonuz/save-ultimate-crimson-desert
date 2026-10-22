# Historical Python editor license scope (reference only)

The root MIT license covers original SaveUltimate code and documentation except
the material listed below. It does not replace existing third-party licenses or
grant rights to game assets.

## Inherited editor code: MPL-2.0

The following modules derive from NattKh's editor and retain MPL-2.0, including
their local modifications. The original copyright notice is in
`licenses/Upstream-MPL-2.0.txt` (Copyright 2026 RicePaddySoftware).

```text
main.py
localization.py
resources/locale/en.json
resources/locale/ja.json
catalogs/client_database.py
catalogs/item_db.py
catalogs/item_template_db.py
save/ben_save_decrypt.py
save/item_scanner.py
save/models.py
save/native_backend.py
save/parc_inserter2.py
save/parc_serializer.py
save/save_crypto.py
save/save_parser.py
save/save_pet_rename.py
ui/icon_cache.py
ui/main_window.py
ui/save_load.py
ui/socket_editor.py
ui/startup_splash.py
ui/table_items.py
ui/theme.py
ui/dialogs/items.py
ui/tabs/backups.py
ui/tabs/companions.py
ui/tabs/equipment.py
ui/tabs/inventory.py
ui/tabs/item_database.py
ui/tabs/knowledge.py
ui/tabs/quest_database.py
ui/tabs/repurchase.py
native/src/*
native/upstream/*
```

Native source provenance is recorded in `native/SOURCE_MANIFEST.json`.
Each release's source archive contains the corresponding source; build instructions
are in `native/README.md` and `CONTRIBUTING.md`.

## Other components

| Material | Terms |
|---|---|
| `native/third_party/lz4/` | BSD 2-Clause; retain the original LZ4 notices |
| Hahmlet fonts | SIL OFL 1.1; `licenses/Hahmlet-OFL.txt` |
| crimson_rs | MIT; `licenses/crimson-rs-MIT.txt` |
| Qt, PySide6 and Shiboken | LGPL-3.0; bundled Qt notices also include GPL-3.0 |
| Python, lz4, cryptography, cffi, pycparser, PyInstaller, Pillow and native runtimes | Their respective original notices in `licenses/` |
| Game catalogs in `resources/data/`, game text in `resources/locale/names_*.json`, and locally extracted artwork in `game_data/images/` | Copyright Pearl Abyss; excluded from the MIT grant |

The SaveUltimate app icon and placeholder artwork are original project assets
covered by the root MIT license.

Game images are extracted from the user’s installed copy and are not included in
the source repository or release package.
