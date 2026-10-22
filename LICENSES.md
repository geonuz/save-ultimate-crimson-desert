# License scope — C# port

The root MIT license covers original SaveUltimate code. It does not replace third-party notices or grant rights to game assets.

## Inherited MPL-2.0 code

`native/src/`, `src/SaveUltimate.Core/Saves/SaveContainer.cs`, and `src/SaveUltimate.Core/Saves/ParcLayout.cs` derive from the original editor and retain MPL-2.0. Preserve `licenses/Upstream-MPL-2.0.txt` and the original copyright notice (Copyright 2026 RicePaddySoftware). Corresponding source is in this directory. Native upstream provenance remains in `native/SOURCE_MANIFEST.json`; port-specific changes are listed in `native/PORT_CHANGES.md`.

Distribute binaries together with their matching MPL-covered source and build instructions (this repository and [CONTRIBUTING.md](CONTRIBUTING.md)).

## Other components

| Material | Terms |
|---|---|
| Archive format/checksum implementation derived from crimson-rs commit `61889474936bf38b10d7d3bf4954e3998b84a444` | MIT; `licenses/crimson-rs-MIT.txt` |
| `native/third_party/lz4/` | BSD 2-Clause; original notices remain in source |
| K4os.Compression.LZ4 1.3.8 | MIT; `licenses/K4os-LZ4-MIT.txt` |
| CommunityToolkit.Mvvm 8.4.2 | MIT; package license and third-party notices copied by build script |
| Magick.NET / Magick.NET.Core 15.0.0 and bundled codecs | Apache-2.0 and component-specific terms; package Notice.txt copied by build script |
| .NET runtime / Windows Desktop runtime | Microsoft distribution notices, copied from the SDK by build script |
| System.CommandLine and MSTest (tools/tests) | MIT; NuGet metadata and package notices |
| Hahmlet Regular/SemiBold embedded in the WPF application | SIL OFL 1.1; `licenses/Hahmlet-OFL.txt` |
| Game catalogs and translated game names | Copyright Pearl Abyss; excluded from the MIT grant |

The app icon and placeholder artwork are original project assets under MIT. Extracted game artwork and personal saves are not release inputs. Legacy Python/Qt notices retained in the source `licenses/` directory describe provenance; Python and Qt are not dependencies of this C# build.
