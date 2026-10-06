# Windows Cleanup Manager (`wcm`)

An ncdu-like TUI for semi-automatic disk cleanup on Windows.
It scans a drive with multiple threads and suggests what to delete (by rules or by the [Jev](https://openrouter.ai/labs/jev) model).
You check items, confirm, and it deletes them to the Recycle Bin or permanently. Supports files, folders, Docker images, volumes and build cache.

## Running

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
