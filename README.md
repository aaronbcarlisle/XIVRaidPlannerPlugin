# XIV Raid Planner - Dalamud Plugin

In-game companion plugin for [FFXIV Raid Planner](https://github.com/aaronbcarlisle/ffxiv-raid-planner). Displays loot priority overlays during savage raids, tracks BiS gear progress, and auto-logs loot drops to the web app.

## Features

- **Priority Overlay** — Loot priority rankings per floor during savage raids with top 3 per drop slot
- **BiS Gear Viewer** — In-game BiS gear table with progress tracking, equipped item comparison, and materia details
- **Auto-Loot Logging** — Detects loot distribution from chat and logs drops to the raid planner API
- **Gear Sync** — Sync your equipped gear to the web app to update your player card
- **Purchase Detection** — Detects BiS tome/book vendor purchases and offers to log them
- **BiS Highlighting** — Tints BiS items in the Need/Greed loot window and tome vendor shops
- **Floor Clear Tracking** — One-click "Mark Floor Cleared" for book tracking
- **Party Matching** — Automatically matches in-game party members to planner player entries
- **Leave Warning** — Warns if you have unclaimed priority loot when leaving an instance

## Installation

### Via Dalamud Custom Repository (Recommended)

1. Open **FFXIV** with **XIVLauncher**
2. Open **Dalamud Settings** (System menu or `/xlsettings`)
3. Go to the **Experimental** tab
4. Under **Custom Plugin Repositories**, add the following URL:
   ```
   https://raw.githubusercontent.com/aaronbcarlisle/XIVRaidPlannerPlugin/main/repo.json
   ```
5. Click the **+** button, then **Save and Close**
6. Open the **Plugin Installer** (`/xlplugins`)
7. Search for **XIV Raid Planner** and click **Install**

### Manual Build

Requires .NET 10 SDK and a Dalamud development environment.

```bash
cd XIVRaidPlannerPlugin
dotnet build --configuration Release
dotnet test   # optional — runs 28 unit tests
```

The built plugin will be in `XIVRaidPlannerPlugin/bin/Release/XIVRaidPlannerPlugin/`.

## Setup

1. In-game, open settings with `/xrp config`
2. On the **Connection** tab, click **Sign in with browser** — this opens your browser, authenticates via Discord, and mints an API key automatically
3. Once connected, go to the **Static** tab and select your static group (tier defaults to Auto)
4. Go to the **Players** tab and link your party members to their planner roster entries

> **Manual key (Advanced):** If browser sign-in isn't available, generate an API key from the [FFXIV Raid Planner](https://xivraidplanner.app) web app (User Menu > API Keys) and paste it in the **Advanced** section of the Connection tab.

## Commands

| Command | Description |
|---------|-------------|
| `/xrp` | Toggle the BiS gear viewer (default) |
| `/xrp bis` | Toggle the BiS gear viewer |
| `/xrp priority` | Toggle the priority overlay |
| `/xrp sync` | Sync equipped gear and mount to the web app |
| `/xrp config` | Open the configuration window |
| `/xrp mountsync` | Reads mounts and totems, reports count in chat, syncs to API
| ` /xrp gearsync` |  Runs gear-only sync

## How It Works

1. **Enter a savage instance** — the plugin fetches priority data and BiS gear from the API
2. **Priority overlay** appears showing who should get each drop slot (configurable timing)
3. **Loot drops** are detected from chat — the plugin offers to log them (Confirm/Auto/Manual modes)
4. **BiS viewer** shows your gear progress with equipped vs. BiS comparison
5. **Gear sync** reads your equipped items and updates your player card on the web app

## Configuration

### Settings Tab

- **Auto-Log Mode** — Confirm (dialog before logging), Auto (logs automatically), or Manual (overlay buttons only)
- **Overlay Timing** — Show on instance entry, duty complete, and/or loot window open
- **Leave Warning** — Warn when leaving with unclaimed priority loot
- **BiS Highlighting** — Highlight BiS items in Need/Greed and tome vendor windows

### Advanced Tab

Override API and frontend URLs for local development/testing. Hidden behind a "Use custom URLs" checkbox — not needed for normal use.

## Requirements

- [FFXIV Raid Planner](https://xivraidplanner.app) account
- Dalamud ([XIVLauncher](https://goatcorp.github.io/))
- FFXIV with a static group configured in the web app

## Project Structure

See [ARCHITECTURE.md](ARCHITECTURE.md) for the full layout, composition root details, event flow, and the browser sign-in sequence.
