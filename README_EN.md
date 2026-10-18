# SplashFontReplacer / 多合一启动图字体 UI 替换器

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Release](https://img.shields.io/badge/Release-v1.0.0-blue.svg)](https://github.com/fylm-233/SplashFontReplacer-多合一启动图字体UI替换器/releases)
[![Issues](https://img.shields.io/badge/Feedback-Issues-green.svg)](../../issues)

> **中文版本：[README.md](README.md)** — the Chinese README is the primary documentation; this is a translated convenience copy.

A single WinUI 3 desktop application that replaces the splash screens and UI fonts of several creative/development tools — the capability of three previously separate tools, consolidated into one single-file executable.

| Module | Target | Approach |
| --- | --- | --- |
| DaVinci splash | Embedded PNG inside `Resolve.exe` | Length-preserving PNG rebuild overwrites the resource block; file size and PE layout stay identical |
| Splash screens (Adobe et al.) | Adobe suite + Unreal / Maya / 3ds Max / JetBrains IDEs / VS 2022 / Live2D | Four write-back channels behind one unified flow: PE resources (`UpdateResource`), loose image files, container-embedded images (equal-length in-place overwrite), jar/zip entries. Multi-resolution variants of one image are grouped automatically |
| DaVinci UI font | `platforms\qwindows.dll` | Equal-length rewrite of font family names in a Qt font fallback table |
| Adobe fonts | `Dictionaries\<locale>\<app>_<locale>.dat` | Rebuild the `FontStyle` font keys (size + family) and re-sync the `signatures.xml` digest; covers AE / PR / ME / AU, CS6–2026 |

Each module keeps its own backup and can be restored independently. The UI is split by a left-hand navigation: **DaVinci Splash / Splash (Adobe etc.) / DaVinci UI Font / Adobe Font / About**.

### Documentation

- Complete user guide (install, operation, FAQ, untested items): [`docs/使用说明.md`](docs/使用说明.md) (Chinese)
- Adobe font mechanism and version matrix: [`docs/Adobe字体替换整合方案.md`](docs/Adobe字体替换整合方案.md) (Chinese)
- Feasibility analysis for Adobe splash replacement: [`docs/Adobe启动图替换可行性分析.md`](docs/Adobe启动图替换可行性分析.md) (Chinese)
- Reverse-engineering notes of the closed-source tool and the rewrite: [`docs/闭源工具实现还原与重写记录.md`](docs/闭源工具实现还原与重写记录.md) (Chinese)

Full English user documentation is on the roadmap; the Chinese documents above are complete.

---

## 1. Download & run

Grab the latest build from [Releases](https://github.com/fylm-233/SplashFontReplacer-多合一启动图字体UI替换器/releases/latest):

| File | Notes |
| --- | --- |
| `多合一启动图字体UI替换器.exe` | Single-file build, just double-click |
| `多合一启动图字体UI替换器-目录版.zip` | Directory build, unzip and run |

Requirements and verified test environments are documented in each release. In short: **Windows 10 1809 (17763) or newer, x64. No .NET runtime or Windows App Runtime installation required.**

**Disclaimer**: this tool modifies resource files of commercial software (Adobe, DaVinci, JetBrains, …). Such modification is unofficial and not endorsed by any vendor. Use at your own risk, only on software you are legally licensed to use, and respect the relevant EULA. Always quit the target application before writing (the tool creates a `.bak` backup of the first modification automatically).

---

## 2. Tech stack

| Layer | Technology |
| --- | --- |
| UI framework | WinUI 3 (Windows App SDK) |
| Language / runtime | C#, .NET 8, self-contained (`net8.0-windows10.0.19041.0`) |
| System interop | P/Invoke: `BeginUpdateResource/UpdateResource/EndUpdateResource` (PE resources), Win32 common dialogs, `CheckSumMappedFile`, memory-mapped files |
| Image I/O | WinRT `Windows.Graphics.Imaging` for decode / scale / encode (PNG / BMP / JPEG) |
| Image rebuild | Home-grown PNG repacker: zlib re-compression (level 9), adaptive scanline re-filtering, lossy degradation ladder (transparent normalization → 6/5/4-bit quantization → alpha drop), length padding via a private ancillary chunk |
| Packaging | "Full directory + single-file self-extracting bootstrapper with embedded payload" (not `PublishSingleFile`, see below) |
| Tests | `tools/stest` (95 assertions) plus `imgtest` / `atest` / `verify` / `dlgprobe` |

### Why not `PublishSingleFile=true`

Self-contained Windows App SDK deployment requires the WinRT activation map (`Microsoft.UI.Xaml` `activatableClass` entries) and the matching DLLs to sit **next to the exe**. `PublishSingleFile` + `IncludeAllContentForSelfExtract` extracts the runtime into a temp directory, but the process path still points at the original bundle exe, so the activation factory cannot find the DLLs and startup fails with `0x80040111 ClassFactory cannot respond to COM request`.

Instead the app ships as a self-extracting bootstrapper: on first run it unpacks the payload into `%LOCALAPPDATA%\MultiReplacer\app\<build-id>\` and launches the real exe from there. The build id comes from the bootstrapper assembly MVID, so a rebuild gets a new folder and old ones are cleaned up.

---

## 3. Building from source

### Prerequisites

- .NET 8 SDK (`dotnet --list-sdks` must list an 8.x)
- Windows 10 SDK (pulled in automatically via the Windows App SDK NuGet packages)
- Optional: Python 3 (only for a higher-compression payload)

### One command

```cmd
build.cmd
```

or

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Output lands in `build\dist\`, the same layout the Release provides.

To rebuild only the bootstrapper (when the app itself has not changed):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -SkipAppPublish
```

### What the script does

1. `dotnet publish src\MultiReplacer.App -c Release -r win-x64 -p:Platform=x64 -p:DebugType=none -o build\app` — full self-contained app directory.
2. Zip that directory into `src\MultiReplacer.Bootstrapper\payload.zip`.
3. `dotnet publish src\MultiReplacer.Bootstrapper -c Release -r win-x64 -o build\dist` — single-file exe with the payload embedded.
4. Also emit a directory-version zip.

> `src/MultiReplacer.App/Assets/app.ico` is not tracked in git. If missing, generate it with `python tools/make-icon.py`.

### Running the self-test suite

```cmd
dotnet tools/stest/bin/Release/net8.0-windows10.0.19041.0/stest.dll
```

Covers PNG/BMP sniffing, the four write-back channels, installed-software enumeration, native dialog filter strings, real Photoshop 2026 and After Effects 2026 installs, plus import-chain regressions (round-trip pixel equality, lossy degradation for oversized images, graceful rejection of incompressible images).

---

## 4. Usage

For a full walkthrough see [`docs/使用说明.md`](docs/使用说明.md) (Chinese). Quick start:

### Splash (Adobe etc.)

1. The page lists installed software automatically; picking one fills the directory and starts scanning.
2. Results are ranked by a "looks like a splash screen" score, and multi-resolution variants are grouped.
3. Select an entry to preview the original image, then click "Select new png…" (PNG/JPG/BMP, aspect-preserving crop by original size, switchable to stretch).
4. "Apply" writes every variant of the group (requesting elevation when needed) and rescans.
5. "Restore backup" rolls back at any time.

**Quit the target application first.** The first write creates a `.bak` backup.

The container-embedded channel (e.g. Photoshop's `PSIconsHighRes.dat`) requires the replacement to be exactly the same length as the region; the tool re-compresses, re-filters and if necessary degrades the image to squeeze it in, and reports precisely how much it overshot if it cannot fit — which tells you how much simpler the image needs to be.

### Adobe fonts (AE / PR / ME / AU …)

1. Installed Adobe apps are detected automatically; or point at a `Dictionaries` directory manually.
2. Pick the font family and the large/medium/small sizes.
3. "Apply" rebuilds the language pack from the first backup as baseline and re-syncs the signature digest.
4. "Restore official language pack" rolls back.

### DaVinci splash / DaVinci UI font

Point at `Resolve.exe` and `platforms\qwindows.dll` respectively, scan, then write back the selected resources.

---

## 5. Feedback & contributing

Please file bugs and feature requests in [Issues](../../issues). The repository ships issue templates asking for:

- **Reproduction steps**
- **OS version**
- **Environment** (app version, target app + version, whether running elevated)
- **Error logs** (the day's log under `%LOCALAPPDATA%\MultiReplacer\logs\`)

Pull requests are welcome. Please keep the "independent backup, independent restore" boundary of each module, and do not merge two write-back channels.

---

## 6. License

[MIT](LICENSE). Because the tool modifies third-party commercial software resources, usage is intended for personal, legally licensed software only.
