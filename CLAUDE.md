# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`wcm`: an ncdu-like TUI for semi-automatic disk cleanup on Windows (.NET 10, `net10.0-windows`). It scans a drive in parallel, suggests deletions from rules or the Jev model (via OpenRouter), and deletes to the Recycle Bin or permanently. It also handles Docker images, volumes and build cache. The README documents the user-facing behaviour, key bindings and rule format.

## Commands

```powershell
dotnet build WindowsCleanupManager.slnx
dotnet test                                                    # all tests
dotnet test --filter "FullyQualifiedName~RuleTests"            # one class
dotnet test --filter "FullyQualifiedName~RuleTests.<Method>"   # one test

dotnet run --project src/Wcm                                   # TUI
dotnet run --project src/Wcm -- scan D:\Dev --no-ai --json out.json   # headless, never deletes
dotnet run --project src/Wcm -- rules                          # list active rules
dotnet publish src/Wcm -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

CI (`.github/workflows/ci.yml`) builds and tests on `windows-latest` for every push to `main` and every PR. Pushing a `v*.*.*` tag runs `release.yml`: it reuses CI, then publishes compressed single-file exes for win-x64 and win-arm64 (the version comes from the tag) and creates a GitHub Release with zips and `SHA256SUMS.txt`. For stable tags (no `-` suffix), the `packages` job then commits a bump of `bucket/wcm.json` (the repo doubles as a Scoop bucket) to `main`, and opens a winget-pkgs PR via `wingetcreate` if the `WINGET_TOKEN` secret is set. `packaging/winget/` holds the initial 0.1.0 winget manifests; later versions are derived from what's in winget-pkgs.

Use `scan --no-ai` for safe manual checks. Without `--no-ai` and with `OPENROUTER_API_KEY` set, the scan makes paid API calls; it asks first unless you pass `-y`.

## Architecture

`Pipeline` (`src/Wcm/Pipeline.cs`) is the shared flow for both the TUI (`Ui/App.cs`) and headless `scan` (`Program.cs`):
**scan → rules → Jev plan (cache) → Jev calls → Docker**. Every stage adds `CleanupItem`s to a `ScanSession`.

- **Scanning** (`Scanning/`): `ParallelScanner` builds an `FsNode` tree. Files under `MinFileNodeSize` (1MB by default) don't become nodes and only count toward `SmallFilesSize`/`SmallFilesCount`. Size, file count and last-write time propagate to ancestors through `Interlocked`. `Markers` holds the project marker files found in a dir (`*.csproj`, `package.json`, …). Rules use them for `siblingExists`, and they go into the Jev prompt.
- **Rules** (`Classification/Rule.cs`, `RuleEngine.cs`, `Classifier.cs`): built-in rules are in `builtin-rules.json`, embedded as a resource with LogicalName `builtin-rules.json`. User rules live in `%APPDATA%\Wcm\rules.json`. Match order: all `protect` rules first, then user rules, then built-in rules; within each group, `never` > `suggest` > `container`. Inside a protected subtree only `allowInProtected` rules (and user `never`) can match. `Classifier` sets each node's `NodeMark` (`Suggested`/`Known`/`Protected`/`Container`) and `ContainsProtected` on ancestors. Later stages depend on these marks.
- **Jev** (`JevPlanner.cs`, `JevClient.cs`, `DecisionCache.cs`): the planner computes an "effective size" that excludes anything already suggested, known or protected. It descends into big children when they hold most of the size, and stops at `MaxCallsPerScan`. Cached verdicts are keyed by path + fingerprint and are used even when AI is off. `StateBuilder` builds the prompt and replaces the user profile path with `%USERPROFILE%`. Thresholds and the risky-category lists decide `Safe` (pre-checked) vs `Review`.
- **Deletion** (`Deletion/`): `Deleter.Validate` is the last line of defence. Right before touching disk it re-checks `ContainsProtected`, `RuleEngine.BlockReason` (a path-only protect check walked from the drive root), existence and reparse points. Every outcome is logged to `%APPDATA%\Wcm\deletions.log`.
- **Docker** (`Docker/DockerProvider.cs`): shells out to `docker.exe`. Docker items have no `Node`. `VhdxCompactor` shrinks `docker_data.vhdx`: stop Docker Desktop → `wsl --shutdown` → elevated diskpart (a detach script always runs after the compact one) → restart Docker. UI in `Ui/CompactFlow.cs`, CLI `docker-compact`.
- **UI** (`Ui/`): a hand-written ANSI full-screen terminal (`Term`) with whole-frame redraws. There is no TUI library. Long operations run in background tasks while the UI loop polls and redraws.
- **Storage** (`Storage/Config.cs`): `AppPaths` (under `%APPDATA%\Wcm`), `AppConfig`, and the shared `Json.Options`: camelCase, kebab-case enums, comments allowed, atomic save via temp file + rename.

`ScanSession.EffectiveSelection()` drops checked items that sit under another checked folder. Keep this in mind when you change selection or size totals.

## Conventions

- Everything is in English: user-facing strings (CLI help, TUI, rule reasons), comments, README.
- Tests use xUnit. `InternalsVisibleTo` exposes internals to `Wcm.Tests`. Build in-memory trees with `Helpers/TreeBuilder` (no disk access). Jev tests use a fake `HttpMessageHandler`. `FileSystemTests` uses real temp directories and junctions.
- Safety is the core invariant. Any change to rule matching, protection or deletion must keep protected paths impossible to delete. Add a test for it in `RuleTests`/`FileSystemTests`.
