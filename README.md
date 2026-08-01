# hackchi-cli

Headless **macOS / Linux** CLI for Nintendo Classic Mini consoles running **hakchi**.

- List games over USB RNDIS + SSH  
- **Add games** (one file, many files, or globs) for **NES + SNES** — no Windows full-library sync  
- FEL / clovershell helpers for low-level work  

This is **not** the Hakchi2-CE Windows GUI. It is a small GPL-3 project inspired by and partly ported from [Hakchi2-CE](https://github.com/TeamShinkansen/Hakchi2-CE). See [NOTICE](NOTICE) for credits.

## Important: the Mini must already run hakchi

This tool **does not** install or flash hakchi onto a stock Classic Mini.

You need a console that already has **hakchi custom firmware** (installed earlier with Hakchi2-CE on Windows, or another supported install path). When powered on to the game menu, USB should enumerate as **RNDIS `04E8:6863`**. Stock firmware will not show that device, and `add-game` / `games` cannot talk to it.

## Download (no build)

Releases: [github.com/jyrmyx/hackchi-cli/releases](https://github.com/jyrmyx/hackchi-cli/releases)

After the **Release** workflow finishes for a tag, you should see **platform binaries** (not just source):

| Asset | Machine |
|-------|---------|
| `hackchi-cli-<ver>-osx-arm64.zip` | Apple Silicon Mac (M1/M2/M3/…) |
| `hackchi-cli-<ver>-osx-x64.zip` | Intel Mac |
| `hackchi-cli-<ver>-linux-x64.zip` | Linux x86_64 |
| `hackchi-cli-<ver>-linux-arm64.zip` | Linux ARM64 |

GitHub also always offers **Source code (zip)** / **Source code (tar.gz)** — those are the repo snapshot only (you must build). Prefer the platform zips above when present.

```bash
unzip hackchi-cli-*-osx-arm64.zip
cd hackchi-cli-*-osx-arm64
./hackchi status
./hackchi games
./hackchi add-game ~/Downloads/DuckTales.zip
```

These platform builds are **self-contained** (no .NET SDK). On macOS, `libusb` is bundled in the zip when the release was built with Homebrew libusb available. If `./hackchi status` still says libusb is missing: `brew install libusb`.

First USB access may show a macOS permission prompt.

If a release only has source archives, either build from source (below) or re-run the **Release** workflow for that tag (Actions → Release → Re-run jobs) after the workflow is fixed.

## Build from source

```bash
brew install dotnet libusb   # Apple Silicon / Homebrew

export PATH="/opt/homebrew/bin:$PATH"
export DOTNET_ROOT="$(brew --prefix dotnet)/libexec"

dotnet build Hakchi.Port.slnx
./run status
./run games
./run add-game ~/Downloads/DuckTales.zip
./run add-game ~/Downloads/Mortal\ Kombat\ II.zip
```

### Make a local release zip

```bash
./scripts/publish-release.sh                 # current OS default RIDs
RIDS=osx-arm64 ./scripts/publish-release.sh  # one platform only
# → artifacts/release/hackchi-cli-<version>-<rid>.zip
```

Tagging `v0.1.0` (or any `v*`) on GitHub runs the release workflow and attaches zips automatically.

### Continuous integration

**CI** (`.github/workflows/ci.yml`) runs on:

- every **pull request**
- every **push to `main`**
- manual **workflow_dispatch**

(Not on every feature-branch push alone — that would double-run with the PR event.)

It restores, builds Release, runs unit tests, and smoke-checks CLI `--help` on Ubuntu and macOS. Hardware/USB tests are not run in CI (no Classic attached).

```bash
dotnet test Hakchi.Port.slnx -c Release   # same tests locally
```

**Dependabot** (`.github/dependabot.yml`) opens monthly PRs for NuGet and GitHub Actions updates (grouped minors/patches).

No arguments to `./run` / `./hackchi` opens an interactive menu.

## Adding games (add-only)

`add-game` packages one or more ROMs (or zips each containing one ROM) and uploads **only those games**. It does **not** mass-delete titles the way Windows “sync” can.

```bash
./run add-game game.zip                           # one file
./run add-game a.zip b.nes c.sfc                  # multiple files
./run add-game ~/Downloads/*.zip                  # shell expands the glob
./run add-game '~/Downloads/*.sfc'                # CLI expands the glob
./run add-game a.zip b.zip --force                # replace same CLV codes only
./run add-game *.zip --dry-run                    # package only, no USB
./run add-game *.zip --stop-on-error              # abort batch on first failure
```

Batch mode packages everything first, uses **one** SSH session, uploads in order, and refreshes the menu once at the end.

| Input | System | Notes |
|-------|--------|--------|
| `.nes` / zip with one `.nes` | NES | Stock kachikachi |
| `.sfc` / `.smc` / `.sfrom` / zip | SNES | Converted to canoe `.sfrom` |

On the console, custom games live under letter folders (e.g. **AKU – NIN**, **POC – TOE**). The CLI places titles by sort name; `./run games` lists everything on disk.

Safety: by default the CLI refuses destructive shell ops and will not replace an existing CLV folder unless you pass `--force`.

### Other systems

Only **NES** and **SNES** packaging are wired up so far. If you need another **system** (e.g. Genesis/Mega Drive, GB/GBC, N64, arcade cores, or whatever your hakchi box already emulates), open an issue or ask — adding a system is usually a small, focused change once the console already has the right core/emulator.

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

Working: device detect, FEL, RNDIS SSH, game list, **add-only multi-upload** (NES + SNES), letter-folder placement, self-contained release zips.

Intentionally not included: installing hakchi on stock hardware, Windows full-library sync, scrapers, hmod GUI, WinForms. More **systems** (beyond NES/SNES) on request.

## License

[GNU GPL v3](LICENSE) — same family as Hakchi2-CE. See [NOTICE](NOTICE) for attribution.
