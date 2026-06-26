# XIV Raid Planner — Dalamud Plugin

A Dalamud plugin for FFXIV that syncs gear, mounts, and collection data with the XIV Raid Planner web app.

## IMPORTANT: Git Commit & PR Rules

**NEVER add AI attribution to commits or PRs.** No "Co-Authored-By: Claude", no "Generated with Claude Code", no AI tool attribution of any kind. This is **absolute and non-negotiable**.

---

## Commands

```bash
# Build (requires Dalamud SDK — downloads automatically via NuGet)
dotnet build --configuration Release

# Restore dependencies
dotnet restore

# Run tests
dotnet test --configuration Release

# Format check (CI enforces this)
dotnet format --verify-no-changes

# Apply formatting
dotnet format
```

Build output: `XIVRaidPlannerPlugin/bin/Release/XIVRaidPlannerPlugin/latest.zip`

---

## Project Structure

```
XIVRaidPlannerPlugin/
├── Api/
│   ├── Models.cs               # Request/response DTOs for every API call
│   ├── RaidPlannerClient.cs    # HTTP client; all API calls go through here
│   └── PluginThread.cs         # RunOnUi / RunBackground thread helpers
├── Services/
│   ├── GearSyncService.cs      # Gear/gearset sync
│   ├── GearsetService.cs       # Lumina gearset data reader
│   ├── ItemMappingService.cs   # Lumina Item sheet → game data
│   ├── MountFarmService.cs     # Mount ownership + totem sync
│   ├── CollectionSyncService.cs # Collection participant state sync (Phase 4)
│   └── CatalogIdResolverService.cs # Lumina ID resolver for admin import
├── Windows/
│   ├── CharacterSyncOverlay.cs # ... tray (gear, mounts, collections)
│   ├── ConfigWindow.cs         # /xrp config settings panel
│   ├── BiSViewerWindow.cs      # In-game BiS gear viewer
│   ├── PriorityOverlayWindow.cs # In-game loot priority overlay
│   ├── SplitClearOverlayWindow.cs # Split-clear run assignment display
│   └── LeaveWarningWindow.cs   # Party-leave countdown warning
├── Plugin.cs                   # Entry point; service wiring, command handler
├── Configuration.cs            # All persisted settings (auto-saved by Dalamud)
├── XIVRaidPlannerPlugin.json   # Manifest: Description, Changelog, Tags, Icon
└── XIVRaidPlannerPlugin.csproj # Version lives here (<Version>x.y.z</Version>)
```

---

## Adding a New Service

Follow the `MountFarmService` / `CollectionSyncService` pattern:

1. **Service class** in `Services/` — constructor takes `RaidPlannerClient`, `PluginThread`, any Dalamud services, and `Configuration`. Expose `event Action<bool, string>? SyncCompleted`.
2. **Thread safety**: Dalamud API (PlayerState, InventoryManager, Lumina) must be read on the framework/UI thread. API calls must run on a background thread via `_thread.RunBackground(async () => { ... })`. Results posted back with `_thread.RunOnUi(...)`.
3. **Wire it in `Plugin.cs`** — instantiate in the service-wiring block, pass to `CharacterSyncOverlay` constructor.
4. **Add a tray state** in `CharacterSyncOverlay` — new `TrayState.SyncingX`, a menu item that calls `TriggerSyncX()`, and an `OnXSyncCompleted` callback. Mirror the mount farm pattern exactly.
5. **Add DTOs to `Models.cs`** — one request class and one result class per new endpoint.
6. **Add client method to `RaidPlannerClient.cs`** — use `PostReturnAsync<TRequest, TResult>` for typed responses, `PostAsync<T>` for fire-and-forget.

---

## API Conventions

All endpoints under the web app backend at `https://xivraider.com/api` (or `localhost:8001` in dev).

- Auth: `X-Api-Key: {apiKey}` header (set via `/xrp config` → API Key field).
- Requests/responses use **camelCase JSON** — Python backend uses `alias_generator=to_camel`. C# `[JsonPropertyName("camelCase")]` maps to it.
- Game-memory reads (PlayerState, InventoryManager) happen **on the framework thread**. Never call these from a background thread.
- Plugin cooldowns are enforced in the service (e.g., 60-second sync cooldown) — don't add them in the UI layer.

---

## Plugin Manifest & Changelog

The **Dalamud installer** shows `Changelog` from `XIVRaidPlannerPlugin.json`. Update it whenever releasing a version that adds visible features:

```json
{
  "Changelog": "v0.4.0 — Collection sync\n\nAdded\n- Sync Collections button syncs mount ownership and totem counts to your static\n- /xrp resolve-ids resolves catalog mount and token names against Lumina sheets\n\nFixed\n- ...",
  "Description": "..."
}
```

Keep the `Description` field current with all active `/xrp` commands.

---

## How to Release

The release pipeline is fully automated. Releasing is just **bumping the version on `main`** — `.github/workflows/auto-release.yml` does the tagging for you.

### Step-by-step

1. **Bump `<Version>` in `XIVRaidPlannerPlugin.csproj`** — use semver (e.g., `0.3.3` → `0.4.0` for new features, `0.3.3` → `0.3.4` for fixes).
2. **Update `Changelog` in `XIVRaidPlannerPlugin.json`** — prepend the new version's notes. The installer shows this.
3. **Update `Description` in `XIVRaidPlannerPlugin.json`** if new `/xrp` commands were added.
4. **Commit and merge the PR to main** (or push directly to main for hotfixes).

That's it — no manual tagging. When the version-bump commit lands on `main`, `auto-release.yml` detects the new `<Version>`, pushes the `v{version}` tag, and invokes `release.yml`. Ordinary merges that don't change the version are left alone (paths filter on the `.csproj` + an existing-tag guard make it a safe no-op).

**Manual fallback:** if you ever need to (re)release a specific tag without a version-bump commit, push the tag yourself — `release.yml` still triggers on any `v*` tag push:
```bash
git tag v0.4.0
git push origin v0.4.0
```

In both paths the `release.yml` workflow then:
- Downloads the latest Dalamud distrib
- Builds the plugin in Release mode
- Creates a GitHub Release with `latest.zip` as the artifact
- Updates `repo.json` (version, download URLs, API level, changelog) and commits it to `main`

The Dalamud plugin repo/installer list points at `repo.json` on `main`. Once committed, the listing updates within the next Dalamud catalog refresh.

### What NOT to do

- Do not manually edit `repo.json` version/download fields — the workflow owns those.
- Do not tag before merging the version bump — `release.yml` checks out the tagged commit and derives the published version from the tag name, so the tag must point at the commit that contains the bumped `<Version>`.
- Do not use a `v` prefix in the `.csproj` version — just `0.4.0`, not `v0.4.0`. The tag is `v0.4.0`.

---

## Pre-PR Checklist

Run this before opening or declaring a PR ready:

### Build & Tests
- [ ] `dotnet build --configuration Release` succeeds with zero errors
- [ ] `dotnet test --configuration Release` passes (if tests cover the changed area)
- [ ] `dotnet format --verify-no-changes` exits 0 (CI enforces formatting)

### Correctness
- [ ] All game-memory reads (PlayerState, InventoryManager, Lumina sheets) are on the **framework/UI thread**
- [ ] All API calls are on a **background thread** via `_thread.RunBackground(...)`
- [ ] Results from background threads are posted back via `_thread.RunOnUi(...)`
- [ ] New Configuration fields default to safe values (feature enabled by default only when it can't break existing users)
- [ ] Cooldowns/rate-limits are in the service, not the UI
- [ ] No silent swallowing of exceptions — log with `_log.Error(...)` and surface to user via `_chat.PrintError(...)`

### API Compatibility
- [ ] New request/response DTOs in `Models.cs` use `[JsonPropertyName("camelCase")]` to match the Python backend's camelCase aliases
- [ ] New endpoints are called through `RaidPlannerClient` — no raw `HttpClient` in services
- [ ] If the endpoint returns a typed body, use `PostReturnAsync<TBody, TResult>`. If fire-and-forget (bool success), use `PostAsync<T>`

### CharacterSyncOverlay (if adding a new sync action)
- [ ] New `TrayState.SyncingX` enum value added
- [ ] Menu item disabled while `_state == TrayState.SyncingX`
- [ ] `BuildStatus()` switch arm added for the new state
- [ ] `Dispose()` unsubscribes the `SyncCompleted` event
- [ ] `RefreshStatus()` guard updated to include the new syncing state

### Manifest (if releasing)
- [ ] `<Version>` bumped in `.csproj`
- [ ] `Changelog` updated in `XIVRaidPlannerPlugin.json`
- [ ] `Description` updated in `XIVRaidPlannerPlugin.json` if `/xrp` commands changed

### Git hygiene
- [ ] No AI attribution in commits or PR description
- [ ] Commit messages follow conventional commits (`feat:`, `fix:`, `chore:`, `refactor:`, `docs:`)
- [ ] No debug-only code, `Console.WriteLine`, or temporary commented blocks left in

---

## Companion Repository

Web app: [`aaronbcarlisle/ffxiv-raid-planner`](https://github.com/aaronbcarlisle/ffxiv-raid-planner)

For API contract changes (new endpoints, request/response schema changes), check both repos before editing. The web app CLAUDE.md documents its own pre-PR checklist.
