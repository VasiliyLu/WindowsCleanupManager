# Windows Cleanup Manager (`wcm`)

[![CI](https://github.com/VasiliyLu/WindowsCleanupManager/actions/workflows/ci.yml/badge.svg)](https://github.com/VasiliyLu/WindowsCleanupManager/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/VasiliyLu/WindowsCleanupManager)](https://github.com/VasiliyLu/WindowsCleanupManager/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/VasiliyLu/WindowsCleanupManager/total)](https://github.com/VasiliyLu/WindowsCleanupManager/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Find out where your disk space went, and get it back safely.** An ncdu-like terminal UI for Windows
that scans a drive in seconds and tells you what is safe to delete: stale `node_modules`, `bin`/`obj`, package caches,
Windows Update leftovers, crash dumps, unused Docker images. You review, check, confirm.

## Features

- **Fast parallel scan** of a whole drive, with an ncdu-style tree browser.
- **54 built-in rules** for temp files, browser and GPU caches, build outputs (.NET, Node, Rust, Maven, Gradle, Python, Unity…) and package caches (NuGet, npm, pnpm, pip, Cargo, Go…).
  Build output is only suggested when the project marker is next to it and the project hasn't changed in a while.
- **Safe by design**: Windows, Program Files, your documents, `.git` folders and VM disks are protected and re-checked right before anything is deleted.
  Recycle Bin by default, and every deletion is logged.
- **Optional AI classification** of large unknown folders with the [Jev](https://openrouter.ai/labs/jev) model. It has a hard per-scan budget (default $0.05), asks before sending anything, anonymizes your profile path and caches answers.
- **Docker cleanup**: unused images, dangling volumes, BuildKit cache.
- **Your own rules**: press `R` on any suggestion to make it permanent, or `N` to never see it again.
- **Headless mode** (`wcm scan C:\ --json out.json`) for scripting; it never deletes.

## Install

Download `wcm-<version>-win-x64.zip` (or `win-arm64`) from the [latest release](https://github.com/VasiliyLu/WindowsCleanupManager/releases/latest),
unzip and run `wcm.exe`. It's a single self-contained exe, so no .NET install is needed.

## Build from source

```powershell
dotnet run --project src/Wcm                 # TUI
dotnet run --project src/Wcm -- scan C:\     # no TUI: only a list of suggestions (deletes nothing)
dotnet run --project src/Wcm -- scan D:\Dev --no-ai --json out.json
dotnet run --project src/Wcm -- rules        # active rules

# single exe
dotnet publish src/Wcm -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Jev needs an OpenRouter key: `setx OPENROUTER_API_KEY sk-or-...` or the `openRouterApiKey` field in the config.
Without a key only rules work (plus previously cached Jev answers).

## How decisions are made

1. **Protect** — `C:\Windows`, `Program Files`, Documents/Desktop/…, `.git`, the Docker disk, etc. Never suggested or deleted
   (except explicit exceptions like `Windows\Temp`, `SoftwareDistribution\Download`).
2. **Rules** — built-in (`src/Wcm/Classification/builtin-rules.json`) and your own (`%APPDATA%\Wcm\rules.json`, which take priority).
3. **Jev decision cache** (`%APPDATA%\Wcm\jev-cache.json`) — keyed by path + content fingerprint, 90-day TTL.
4. **Jev** — only for the remaining large objects (folders ≥ 1 GB, files ≥ 2 GB), descending into the subfolders
   where the size actually is. Per-scan limits: 100 requests and $0.05; you're asked for confirmation before anything is sent.
   The model receives the path (profile replaced with `%USERPROFILE%`), sizes and names of the largest nested entries.

Jev answer: `deletable ≥ 0.85` and a safe category → checked; `0.6–0.85` or a risky category → suggested but unchecked.
In the list, `R` turns any suggestion into a permanent rule, `N` means "never suggest again".

## Keys

| Screen | Keys |
|---|---|
| Menu | `↑↓` select, `Enter` scan, `I` toggle Jev, `R` reload config, `Q` quit |
| List | `Space` check, `A` all safe, `U` uncheck all, `S` sort, `Enter`/`B` tree, `R` rule, `N` never suggest, `Del`/`D` to Recycle Bin, `Shift+Del`/`X` permanently |
| Tree | `Enter`/`→` open, `←`/`Backspace` up, `Space` check manually, `Del`/`X` delete, `Q` back to list |

## Rule format

```jsonc
{
  "id": "my-unity-cache",
  "match": ["**\\Library"],            // ** — any depth, * — part of a name, %ENV% vars are expanded
  "kind": "dir",                        // dir | file | any
  "action": "suggest",                  // suggest | never | protect | container
  "when": { "siblingExists": "ProjectSettings", "minSize": "100MB", "olderThanDays": 30 },
  "category": "build",
  "safety": "safe",                     // safe — checked right away, review — not
  "contents": false,                    // true — delete the contents, keep the folder itself
  "reason": "Unity cache"
}
```

Deletion log: `%APPDATA%\Wcm\deletions.log`.

Docker: space is freed inside `docker_data.vhdx`, but the file itself may not shrink —
run `wsl --shutdown`, then `Optimize-VHD` / `diskpart compact vdisk`.

## Releases

Prebuilt `wcm.exe` (win-x64, win-arm64) is on the [Releases](../../releases) page. To cut a release, push a version tag:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions runs the tests, publishes self-contained single-file exes and creates the release with notes generated from commits.
Tags with a suffix (`v1.1.0-beta.1`) become pre-releases.

## License

[MIT](LICENSE)
