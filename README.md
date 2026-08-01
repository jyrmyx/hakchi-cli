# hackchi-cli

Headless **macOS / Linux** CLI for Nintendo Classic Mini consoles running **hakchi**.

- List games over USB RNDIS + SSH  
- **Add games one-by-one** (NES + SNES) without Windows full-library sync  
- FEL / clovershell helpers for low-level work  

This is **not** the Hakchi2-CE Windows GUI. It is a small GPL-3 project inspired by and partly ported from [Hakchi2-CE](https://github.com/TeamShinkansen/Hakchi2-CE). See [NOTICE](NOTICE) for credits.

## Prerequisites (Apple Silicon / Homebrew)

```bash
brew install dotnet libusb
```

.NET **10** SDK and **libusb** are required.

## Build & run

```bash
export PATH="/opt/homebrew/bin:$PATH"
export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"

dotnet build Hakchi.Port.slnx

# Convenience wrapper (sets DOTNET_ROOT)
./run status
./run usb
./run games
./run add-game ~/Downloads/DuckTales.zip          # NES
./run add-game ~/Downloads/Mortal\ Kombat\ II.zip # SNES
./run add-game game.sfc --dry-run
./run add-game game.nes --force                   # replace same CLV only
```

No arguments opens an interactive menu.

## Adding games (add-only)

`add-game` packages a single ROM (or a zip containing one ROM) and uploads **only that game**. It does **not** mass-delete titles the way Windows “sync” can.

| Input | Notes |
|-------|--------|
| `.nes` / zip with one `.nes` | NES → kachikachi |
| `.sfc` / `.smc` / `.sfrom` / zip | SNES → canoe `.sfrom` |

On the console, custom games live under letter folders (e.g. **AKU – NIN**, **POC – TOE**). The CLI places titles by sort name; `./run games` lists everything on disk.

Safety: by default the CLI refuses destructive shell ops and will not replace an existing CLV folder unless you pass `--force`.

## Layout

| Path | Role |
|------|------|
| `hakchi-cli/` | Spectre.Console CLI entrypoint |
| `Hakchi.Usb` | libusb bootstrap + enumeration |
| `Hakchi.Fel` | Allwinner FEL (port of FelLib) |
| `Hakchi.Clovershell` | Clovershell USB shell (port) |
| `Hakchi.Rndis` | Userspace RNDIS + TCP for SSH |
| `Hakchi.Core` | Shared shell abstractions |
| `assets/` | Blank cart art, optional memboot images |
| `run` | Launch helper for Homebrew .NET |

## Hardware notes

| USB ID | Mode |
|--------|------|
| `04E8:6863` | hakchi RNDIS (list/add games over SSH) |
| `1F3A:EFE8` | FEL / clovershell bulk |

- macOS does **not** need Zadig; libusb talks to the device directly  
- Use a **data** USB cable; power-on to the **game menu** for RNDIS  
- Prefer a direct Mac port when possible  

## Status

Working: device detect, FEL, RNDIS SSH, game list, **add-only NES/SNES upload**, letter-folder placement.

Intentionally not included: Windows full-library sync, scrapers, hmod GUI, WinForms.

## License

[GNU GPL v3](LICENSE) — same family as Hakchi2-CE. See [NOTICE](NOTICE) for attribution.
