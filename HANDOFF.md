# XIV Raid Planner - Dalamud Plugin Handoff

## Project Overview

This is a Dalamud plugin for FFXIV that provides an in-game loot priority overlay and auto-logging for the [FFXIV Raid Planner](https://github.com/aaronbcarlisle/ffxiv-raid-planner) web app. The plugin is a **companion** to the web app — it handles real-time loot priority display and drop logging during savage raids so players don't need to alt-tab.

**Repo:** https://github.com/aaronbcarlisle/XIVRaidPlannerPlugin
**Backend PR:** https://github.com/aaronbcarlisle/ffxiv-raid-planner/pull/70

## Current State

The plugin scaffold is complete with all files written but **has not been compiled or tested yet**. It needs a Windows machine with .NET SDK and the Dalamud dev environment to build. That's what this session is for.

### What exists

| File | Status | Purpose |
|------|--------|---------|
| `XIVRaidPlannerPlugin.sln` | Written | Solution file |
| `XIVRaidPlannerPlugin/XIVRaidPlannerPlugin.csproj` | Written | Project file using `Dalamud.NET.Sdk/14.0.1` |
| `XIVRaidPlannerPlugin/XIVRaidPlannerPlugin.json` | Written | Plugin manifest |
| `XIVRaidPlannerPlugin/Plugin.cs` | Written | Entry point — wires services, windows, commands |
| `XIVRaidPlannerPlugin/Configuration.cs` | Written | Persisted settings (API key, static selection, auto-log mode) |
| `XIVRaidPlannerPlugin/Api/Models.cs` | Written | C# DTOs matching the API's camelCase JSON responses |
| `XIVRaidPlannerPlugin/Api/RaidPlannerClient.cs` | Written | HttpClient wrapper with `Bearer xrp_` auth |
| `XIVRaidPlannerPlugin/Services/TerritoryService.cs` | Written | Savage instance detection via Lumina TerritoryType sheet |
| `XIVRaidPlannerPlugin/Services/PartyMatchingService.cs` | Written | Match in-game party names to planner player entries |
| `XIVRaidPlannerPlugin/Services/LootDetectionService.cs` | Written | Chat SeString parsing for loot distribution events |
| `XIVRaidPlannerPlugin/Services/LeaveWarningService.cs` | Written | Warn if leaving with unclaimed priority loot |
| `XIVRaidPlannerPlugin/Windows/PriorityOverlayWindow.cs` | Written | ImGui overlay showing top 3 priority per drop slot |
| `XIVRaidPlannerPlugin/Windows/ConfigWindow.cs` | Written | 4-tab settings (Connection, Static, Players, Settings) |
| `XIVRaidPlannerPlugin/Windows/LootConfirmationWindow.cs` | Written | Confirm/skip popup for detected loot |
| `XIVRaidPlannerPlugin/Windows/LeaveWarningWindow.cs` | Written | "You have unclaimed loot" warning |

### What needs to happen next

1. **Get the project building** — Run `dotnet build` or `dotnet restore` and fix any compilation errors. The code was written without access to the Dalamud SDK, so there will likely be issues with:
   - Lumina API differences (the `GetExcelSheet<T>()` and sheet row access patterns may have changed in recent Dalamud/Lumina versions)
   - SeString payload types and properties (e.g., `PlayerPayload.PlayerName`, `ItemPayload.Item`)
   - ImGui API differences (parameter types, method signatures)
   - Missing `using` directives
   - `IClientState.TerritoryChanged` event signature (may be `Action<ushort>` or different)

2. **Verify Lumina data access** — The `TerritoryService.cs` uses Lumina to resolve territory IDs to savage floors. The `LootDetectionService.cs` uses Lumina to resolve item IDs to equipment slot categories. These patterns may need adjustment for the current Lumina API.

3. **Test in-game** — Once it builds, load it in Dalamud and verify:
   - `/xrp config` opens the config window
   - API key connection test works against the web app
   - Entering a savage instance triggers the overlay
   - Chat loot messages are correctly parsed

4. **Territory ID verification** — The hardcoded fallback territory IDs (1243-1246 for M9S-M12S) are placeholders. Verify these against actual game data or let the Lumina lookup handle it.

## Backend API (already deployed on PR #70)

The plugin authenticates with `Authorization: Bearer xrp_...` header. Key endpoints:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/auth/me` | Verify API key works |
| `GET` | `/api/static-groups` | List user's statics |
| `GET` | `/api/static-groups/{id}/tiers/{tierId}/priority?floor={1-4}` | Loot priority rankings |
| `GET` | `/api/static-groups/{id}/tiers/{tierId}/current-week` | Current week number |
| `POST` | `/api/static-groups/{id}/tiers/{tierId}/loot-log` | Log a gear drop |
| `POST` | `/api/static-groups/{id}/tiers/{tierId}/material-log` | Log a material drop |
| `POST` | `/api/static-groups/{id}/tiers/{tierId}/mark-floor-cleared` | Mark floor cleared (books) |

All POST requests include `"notes": "Logged via Dalamud plugin"` for audit trail. No CSRF token needed for API key auth.

## Architecture Notes

- **Plugin.cs** is the orchestrator — it creates all services and windows, wires events, and handles the lifecycle. Services fire events, Plugin catches them and coordinates responses.
- **TerritoryService** fires `OnSavageEntered(floor)` / `OnSavageExited()`. Plugin reacts by fetching priority data and showing/hiding the overlay.
- **LootDetectionService** fires `OnLootObtained(LootEvent)`. Plugin checks `AutoLogMode` and either shows the confirmation popup, auto-logs, or does nothing.
- **PartyMatchingService** is called manually after priority data is fetched. It matches `IPartyList` names against `PriorityResponse.Players`.
- All API calls are async via `Task.Run()` to avoid blocking the game thread.
- The overlay uses role colors matching the web app (tank=#5a9fd4, healer=#5ad490, melee=#d45a5a, ranged=#d4a05a, caster=#b45ad4).

## Known Issues / Watch Out For

- The `Dalamud.NET.Sdk/14.0.1` version in the csproj may need updating depending on when you build. Check https://github.com/goatcorp/Dalamud for the current API level.
- `EquipSlotCategory` mapping in `LootDetectionService.cs` (the switch on row IDs 1-12) needs verification against current game data. These IDs are generally stable but worth confirming.
- The `RaidPlannerClient.cs` sets `HttpClient.BaseAddress` in `UpdateAuth()` which can only be set once per HttpClient instance. If reconfiguring the URL fails, the client may need to be recreated instead of reused.
- Chat message types for loot detection (`XivChatType.LootNotice`, `(XivChatType)2105`) may need verification — the exact chat type numbers can vary.

## Git Commit Rules

- **NEVER add AI attribution** to commits or PRs. No "Co-Authored-By: Claude", no "Generated with Claude Code", nothing.
