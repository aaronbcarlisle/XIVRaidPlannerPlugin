# XIV Raid Planner - Dalamud Plugin

In-game loot priority overlay and auto-logging for [FFXIV Raid Planner](https://github.com/aaronbcarlisle/ffxiv-raid-planner).

## Features

- **Priority Overlay**: Displays loot priority rankings per floor during savage raids
- **Auto-Loot Logging**: Detects loot distribution and logs drops to the raid planner API
- **Floor Clear Tracking**: One-click "Mark Floor Cleared" for book tracking
- **Party Matching**: Automatically matches in-game party members to planner player entries
- **Leave Warning**: Warns if you're in top 3 priority for an unclaimed drop and try to leave

## Setup

1. Generate an API key from the FFXIV Raid Planner web app (User Menu > API Keys)
2. Install this plugin via custom repository
3. Open settings with `/xrp config`
4. Enter your API key and select your static group

## Commands

| Command | Description |
|---------|-------------|
| `/xrp` | Toggle the priority overlay |
| `/xrp config` | Open the configuration window |

## Requirements

- [FFXIV Raid Planner](https://github.com/aaronbcarlisle/ffxiv-raid-planner) account with an API key
- Dalamud (XIVLauncher)

## Building

Requires .NET 8 SDK and Dalamud development environment.

```bash
dotnet build
```

## Project Structure

```
XIVRaidPlannerPlugin/
  Plugin.cs                          # Entry point (IDalamudPlugin)
  Configuration.cs                   # Persisted settings
  Api/
    RaidPlannerClient.cs             # HttpClient wrapper for API
    Models.cs                        # C# DTOs matching API responses
  Services/
    TerritoryService.cs              # Detect savage raid instances
    PartyMatchingService.cs          # Match party -> planner players
    LootDetectionService.cs          # Chat message loot parsing
    LeaveWarningService.cs           # Warn if leaving with unclaimed priority loot
  Windows/
    ConfigWindow.cs                  # Settings UI (ImGui)
    PriorityOverlayWindow.cs         # Main overlay during raids
    LootConfirmationWindow.cs        # Confirm before logging
    LeaveWarningWindow.cs            # "You have unclaimed loot" warning
```
