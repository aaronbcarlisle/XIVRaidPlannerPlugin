# XIV Raid Planner Plugin — Architecture

Dalamud companion plugin for the [FFXIV Raid Planner](https://xivraidplanner.app) web app. Written in C# / .NET 9 (via Dalamud.NET.Sdk 15) with ImGui for the UI. Authenticates via `xrp_` API keys — either pasted manually or minted in one click via the browser sign-in flow (no copy-paste required).

---

## Project Layout

```
XIVRaidPlannerPlugin/
├── XIVRaidPlannerPlugin/
│   ├── Plugin.cs                       # Composition root (~250 lines)
│   ├── Configuration.cs                # Persisted settings (Dalamud config)
│   ├── Theme.cs                        # Shared ImGui colours/styles (role colours, etc.)
│   ├── PluginThread.cs                 # Framework/tick helpers
│   ├── GameConstants.cs                # Territory IDs, floor mappings
│   ├── Api/
│   │   ├── RaidPlannerClient.cs        # HttpClient with Bearer xrp_ auth
│   │   ├── Models.cs                   # C# DTOs (camelCase JSON ↔ PascalCase C#)
│   │   └── ApiResult.cs                # Discriminated-union result type
│   ├── Auth/
│   │   ├── BrowserAuthService.cs       # Loopback OAuth / PKCE sign-in
│   │   └── PkceCodes.cs               # PKCE verifier + challenge generation
│   ├── Services/
│   │   ├── RaidSessionService.cs       # Coordinates session state (priority, party, BiS)
│   │   ├── GearSyncService.cs          # Diff + POST equipped gear to player card
│   │   ├── LootLogCoordinator.cs       # Routes detected loot per AutoLogMode
│   │   ├── LeaveWarningService.cs      # Warns about unclaimed priority loot on exit
│   │   ├── TerritoryService.cs         # Fires OnSavageEntered/Exited events
│   │   ├── PartyMatchingService.cs     # Orchestrates party → planner player matching
│   │   ├── PartyMatcher.cs            # Pure matching logic (testable, no Dalamud deps)
│   │   ├── LootDetectionService.cs     # Chat SeString parsing for loot events
│   │   ├── ItemMappingService.cs       # BiS item ID lookup (O(1) dictionary + Lumina)
│   │   ├── ItemSourceClassifier.cs     # Classifies equipped items by source (raid/tome/etc.)
│   │   ├── BiSDataService.cs           # Fetches/caches player BiS gear from API
│   │   ├── InventoryService.cs         # Reads equipped gear from game memory
│   │   └── AddonHighlightService.cs    # BiS highlighting in NeedGreed/shop addons
│   └── Windows/
│       ├── ConfigWindow.cs             # 4-tab settings + Advanced tab (ImGui)
│       ├── PriorityOverlayWindow.cs    # Top-3 priority per drop slot
│       ├── BiSViewerWindow.cs          # BiS gear table with progress + sync button
│       ├── LootConfirmationWindow.cs   # Confirm/skip popup for detected drops
│       └── LeaveWarningWindow.cs       # "You have unclaimed loot" warning
└── XIVRaidPlannerPlugin.Tests/
    └── (29 unit tests — pure logic, no Dalamud runtime required)
```

---

## Composition Root

`Plugin.cs` is the single composition root. It declares `[PluginService]` fields for Dalamud-injected services, constructs the service graph and windows via constructor injection, wires events between them, and disposes everything on unload. No service or window reaches back into `Plugin` via a static property — dependencies flow inward through constructors only.

**Construction order:**

1. `PluginThread` — framework tick helper
2. `RaidPlannerClient` — HTTP client (reads `Configuration` for API key / URLs)
3. `BrowserAuthService` — loopback sign-in (gets `RaidPlannerClient` ref to refresh auth after key mint)
4. Leaf services: `TerritoryService`, `PartyMatchingService`, `LootDetectionService`, `ItemMappingService`, `BiSDataService`, `InventoryService`, `AddonHighlightService`, `LeaveWarningService`
5. Windows: `ConfigWindow`, `PriorityOverlayWindow`, `BiSViewerWindow`, `LootConfirmationWindow`, `LeaveWarningWindow`
6. Coordinator services (need windows + leaf services): `RaidSessionService`, `LootLogCoordinator`, `GearSyncService`
7. `LeaveWarningService.Initialize(...)` — late-wires the window reference

---

## Event Flow

**Entering a savage instance:**
`TerritoryService.OnSavageEntered` → `RaidSessionService.OnSavageEntered` (fetches priority rankings, runs party match via `PartyMatchingService`, triggers BiS fetch via `BiSDataService`) → `PriorityOverlayWindow` opens.

**Loot detected in chat:**
`LootDetectionService.OnLootObtained` → `LootLogCoordinator.OnLootObtained` (checks `AutoLogMode`: Confirm → show `LootConfirmationWindow`; Auto → POST immediately; Manual → update overlay buttons only).

**Gear sync:**
`BiSViewerWindow.OnSyncRequested` → `GearSyncService.Sync()` (reads equipped gear via `InventoryService`, diffs against cached BiS, POSTs changed slots, logs new acquisitions back through `LootLogCoordinator`).

**Leave warning:**
`IAddonLifecycle SelectYesno` (setup event) → `LeaveWarningService.OnSelectYesnoSetup` (checks unclaimed priority loot from `RaidSessionService`) → shows `LeaveWarningWindow` if any unclaimed items remain.

---

## Browser Sign-In Flow

The plugin supports a one-click sign-in that mints an `xrp_` API key without manual copy-paste.

1. User clicks **Sign in with browser** in `ConfigWindow` (Connection tab).
2. `BrowserAuthService` generates a PKCE code verifier + SHA-256 challenge (`PkceCodes`), generates a random `state`, and starts an ephemeral `HttpListener` on `http://127.0.0.1:{port}/callback/`.
3. Plugin opens the browser to `{frontendUrl}/plugin-auth?redirect_uri=http://127.0.0.1:{port}/callback/&state={state}&code_challenge={challenge}&code_challenge_method=S256`.
4. Web app authenticates the user (Discord OAuth or existing session), shows an "Authorize plugin" consent page, then 302-redirects to the loopback `redirect_uri` with `code` and `state` query params.
5. `HttpListener` receives the redirect. Plugin validates `state` matches, then POSTs to `/api/api-keys/plugin-auth/exchange` with `{ code, code_verifier }`.
6. Backend exchanges the one-time code for a new `xrp_` API key and returns it.
7. Plugin stores the key in `Configuration`, calls `RaidPlannerClient.UpdateAuth(key)`, and closes the listener.

> **Note:** The web app backend endpoints (`/api/api-keys/plugin-auth/authorize` and `/exchange`) are in a companion PR in the `ffxiv-raid-planner` repo. Browser sign-in is non-functional until those deploy. Manual API key entry (under **Advanced** in ConfigWindow) works today.

---

## Build and Test

**Requirements:**
- .NET 10 SDK (CI installs both 9 and 10; local builds work with either)
- Dalamud dev assemblies — auto-detected from `%APPDATA%\XIVLauncher\addon\Hooks\dev\` on Windows; set `DALAMUD_HOME` otherwise

**Commands:**

```bash
# Build
dotnet build --configuration Release

# Run unit tests (29 tests, no game runtime needed)
dotnet test

# Verify formatting
dotnet format --verify-no-changes
```

The built plugin lands in `XIVRaidPlannerPlugin/bin/Release/XIVRaidPlannerPlugin/`.

**Unit test coverage** (pure logic, no Dalamud runtime):
- `Theme` colour helpers
- `ApiResult` discriminated union
- `ItemSourceClassifier` slot classification
- `PartyMatcher` name-matching logic
- `GearSyncService` diff calculation
- `PkceCodes` PKCE verifier/challenge generation
- Slot → floor mapping (`GameConstants`)

---

## CI Gates

All three checks must pass on every PR:

| Check | Command |
|-------|---------|
| Build | `dotnet build --configuration Release` |
| Tests | `dotnet test` |
| Formatting | `dotnet format --verify-no-changes` |

CI also uploads the built plugin as an artifact.
