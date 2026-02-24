using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Semi-transparent ImGui overlay that displays loot priority during savage raids.
/// Shows priority per drop slot on the current floor.
/// </summary>
public class PriorityOverlayWindow : Window, IDisposable
{
    // Role colors matching the web app
    private static readonly Dictionary<string, Vector4> RoleColors = new()
    {
        ["tank"] = new Vector4(0.353f, 0.624f, 0.831f, 1f),     // #5a9fd4
        ["healer"] = new Vector4(0.353f, 0.831f, 0.565f, 1f),   // #5ad490
        ["melee"] = new Vector4(0.831f, 0.353f, 0.353f, 1f),    // #d45a5a
        ["ranged"] = new Vector4(0.831f, 0.627f, 0.353f, 1f),   // #d4a05a
        ["caster"] = new Vector4(0.706f, 0.353f, 0.831f, 1f),   // #b45ad4
    };

    // Job abbreviation -> ClassJob ID (for icon lookup: 62100 + ID = framed icon)
    private static readonly Dictionary<string, uint> JobIconIds = new()
    {
        ["PLD"] = 19, ["WAR"] = 21, ["DRK"] = 32, ["GNB"] = 37,
        ["WHM"] = 24, ["SCH"] = 28, ["AST"] = 33, ["SGE"] = 40,
        ["MNK"] = 20, ["DRG"] = 22, ["NIN"] = 30, ["SAM"] = 34, ["RPR"] = 39, ["VPR"] = 41,
        ["BRD"] = 23, ["MCH"] = 31, ["DNC"] = 38,
        ["BLM"] = 25, ["SMN"] = 27, ["RDM"] = 35, ["PCT"] = 42,
    };

    // Gear slot icon names (loaded from embedded resources)
    private static readonly HashSet<string> SlotIconNames = new()
    {
        "weapon", "head", "body", "hands", "legs", "feet",
        "earring", "necklace", "bracelet", "ring",
    };

    // Job abbreviation -> embedded PNG filename (lowercase)
    private static readonly Dictionary<string, string> JobIconFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "pld", ["WAR"] = "war", ["DRK"] = "drk", ["GNB"] = "gnb",
        ["WHM"] = "whm", ["SCH"] = "sch", ["AST"] = "ast", ["SGE"] = "sge",
        ["MNK"] = "mnk", ["DRG"] = "drg", ["NIN"] = "nin", ["SAM"] = "sam", ["RPR"] = "rpr", ["VPR"] = "vpr",
        ["BRD"] = "brd", ["MCH"] = "mch", ["DNC"] = "dnc",
        ["BLM"] = "blm", ["SMN"] = "smn", ["RDM"] = "rdm", ["PCT"] = "pct",
    };

    // Cached slot icon textures (loaded from embedded PNGs)
    private readonly Dictionary<string, ISharedImmediateTexture?> _slotIcons = new();

    // Cached job icon textures (loaded from embedded PNGs)
    private readonly Dictionary<string, ISharedImmediateTexture?> _jobIcons = new();

    // Material type -> eligible augmentation slots
    private static readonly Dictionary<string, string[]> MaterialSlotOptions = new()
    {
        ["twine"] = new[] { "head", "body", "hands", "legs", "feet" },
        ["glaze"] = new[] { "earring", "necklace", "bracelet", "ring1", "ring2" },
        ["solvent"] = new[] { "weapon" },
    };

    private static readonly Vector4 ColorAccent = new(0.298f, 0.722f, 0.659f, 1f);
    private static readonly Vector4 ColorSuccess = new(0.133f, 0.773f, 0.369f, 1f);
    private static readonly Vector4 ColorError = new(0.937f, 0.267f, 0.267f, 1f);
    private static readonly Vector4 ColorMuted = new(0.4f, 0.4f, 0.45f, 1f);
    private static readonly Vector4 ColorLink = new(0.4f, 0.7f, 1.0f, 1f);

    // Floor colors matching the web app design system
    private static readonly Dictionary<int, Vector4> FloorColors = new()
    {
        [1] = new Vector4(0.133f, 0.773f, 0.369f, 1f),  // #22c55e - Floor 1 (Accessories)
        [2] = new Vector4(0.231f, 0.510f, 0.965f, 1f),  // #3b82f6 - Floor 2 (Left Side)
        [3] = new Vector4(0.659f, 0.333f, 0.969f, 1f),  // #a855f7 - Floor 3 (Body)
        [4] = new Vector4(0.961f, 0.620f, 0.043f, 1f),  // #f59e0b - Floor 4 (Weapon)
    };

    private readonly Configuration _config;

    private PriorityResponse? _priorityData;
    private string? _currentFloorKey;
    private int _currentFloor;
    private string _floorName = "";
    private string _staticName = "";
    private string _tierName = "";

    // Track logged entries: key = "playerId|slot"
    private readonly HashSet<string> _loggedEntries = new();

    // Floor cleared state
    private bool _floorCleared;

    // Material slot selection popup state
    private string _pendingMaterialPlayerId = "";
    private string _pendingMaterialSlot = "";
    private string _pendingMaterialPlayerName = "";
    private string[]? _pendingMaterialEligibleSlots;
    private bool _openMaterialPopup;

    // Confirmation popup state
    private enum ConfirmAction { None, LogLoot, ClearFloor }
    private ConfirmAction _confirmAction;
    private string _confirmPlayerId = "";
    private string _confirmSlot = "";
    private string _confirmPlayerName = "";
    private string? _confirmSlotAugmented;
    private bool _openConfirmPopup;

    // Temporary status message
    private string _statusMessage = "";
    private Vector4 _statusColor;
    private DateTime _statusExpiry;

    /// <summary>Drop types that have been manually logged via the overlay.</summary>
    public IReadOnlySet<string> LoggedEntries => _loggedEntries;

    /// <summary>Fired when user clicks a "Log" button for a specific player+slot.</summary>
    public event Action<string, string, string, string?>? OnManualLog; // playerId, slot, playerName, slotAugmented

    /// <summary>Fired when user clicks "Mark Floor Cleared".</summary>
    public event Action? OnMarkFloorCleared;

    /// <summary>Fired when user clicks the refresh button.</summary>
    public event Action? OnRefresh;

    public PriorityOverlayWindow(Configuration config)
        : base("XIV Raid Planner",
            ImGuiWindowFlags.NoCollapse)
    {
        _config = config;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(1200, 800),
        };

        // Mostly opaque background for readability
        BgAlpha = 0.95f;

        // Load gear slot icons from embedded resources
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in SlotIconNames)
        {
            var resourceName = $"XIVRaidPlannerPlugin.Images.{name}.png";
            _slotIcons[name] = Plugin.TextureProvider.GetFromManifestResource(assembly, resourceName);
        }

        // Load job icons from embedded resources
        foreach (var (abbrev, fileName) in JobIconFileNames)
        {
            var resourceName = $"XIVRaidPlannerPlugin.Images.jobs.{fileName}.png";
            _jobIcons[abbrev.ToUpperInvariant()] = Plugin.TextureProvider.GetFromManifestResource(assembly, resourceName);
        }
    }

    public void SetPriorityData(PriorityResponse? data, int floor, string floorName, string staticName = "", string tierName = "")
    {
        _priorityData = data;
        _currentFloorKey = $"floor{floor}";
        _currentFloor = floor;
        _floorName = floorName;
        _loggedEntries.Clear();
        if (!string.IsNullOrEmpty(staticName))
            _staticName = staticName;
        if (!string.IsNullOrEmpty(tierName))
            _tierName = tierName;

        // Title bar: "XIV Raid Planner | <Static Name>"
        if (!string.IsNullOrEmpty(_staticName))
            WindowName = $"XIV Raid Planner  |  {_staticName}###XIVRaidPlanner";
        else
            WindowName = $"XIV Raid Planner###XIVRaidPlanner";
    }

    public void ClearData()
    {
        _priorityData = null;
        _currentFloorKey = null;
        _floorName = "";
        _loggedEntries.Clear();
        _floorCleared = false;
        _statusMessage = "";
    }

    /// <summary>Mark a player+slot as successfully logged.</summary>
    public void MarkAsLogged(string playerId, string slot, string playerName)
    {
        _loggedEntries.Add($"{playerId}|{slot}");
        ShowStatus($"Logged {FormatDropName(slot)} -> {playerName}", ColorSuccess);
    }

    /// <summary>Show a log failure message.</summary>
    public void MarkLogFailed(string slot, string playerName)
    {
        ShowStatus($"Failed to log {FormatDropName(slot)} -> {playerName}", ColorError);
    }

    /// <summary>Mark the floor as successfully cleared.</summary>
    public void MarkFloorCleared()
    {
        _floorCleared = true;
        ShowStatus($"{_floorName} marked as cleared!", ColorSuccess);
    }

    public void ShowStatus(string message, Vector4 color)
    {
        _statusMessage = message;
        _statusColor = color;
        _statusExpiry = DateTime.UtcNow.AddSeconds(5);
    }

    public override void Draw()
    {
        if (_priorityData == null || _currentFloorKey == null)
        {
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Waiting for priority data...");
            return;
        }

        if (!_priorityData.Priority.TryGetValue(_currentFloorKey, out var floorPriority))
        {
            ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), $"No priority data for {_floorName}");
            return;
        }

        // Subheader: tier name - floor name in floor color (Ctrl+Click to open web app), week in gray
        var floorColor = FloorColors.GetValueOrDefault(_currentFloor, ColorAccent);
        if (!string.IsNullOrEmpty(_tierName))
        {
            var tierUrl = BuildWebAppUrl();
            DrawLinkText($"{char.ToUpper(_tierName[0])}{_tierName[1..]}", tierUrl, floorColor);
            ImGui.SameLine();
            ImGui.TextColored(floorColor, "-");
            ImGui.SameLine();
        }

        var floorUrl = BuildWebAppUrl("loot", _currentFloor);
        DrawLinkText(_floorName, floorUrl, floorColor);
        ImGui.SameLine();

        var weekUrl = BuildWebAppUrl("log", week: _priorityData.CurrentWeek);
        DrawLinkText($"Week {_priorityData.CurrentWeek}", weekUrl, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        ImGui.Separator();

        // Render priority columns for each drop type
        var dropTypes = new List<string>(floorPriority.Keys);
        var columnCount = dropTypes.Count;

        // Reserve space for bottom area (button + status + hint)
        var bottomHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2 + 2;
        if (_statusMessage.Length > 0 && DateTime.UtcNow < _statusExpiry)
            bottomHeight += ImGui.GetTextLineHeightWithSpacing();
        if (!string.IsNullOrEmpty(_config.DefaultGroupShareCode))
            bottomHeight += ImGui.GetTextLineHeightWithSpacing();
        var tableHeight = Math.Max(100, ImGui.GetContentRegionAvail().Y - bottomHeight);

        if (columnCount > 0)
        {
            // Scrollable child region for the table — keeps button pinned at bottom
            if (ImGui.BeginChild("priority_scroll", new Vector2(0, tableHeight)))
            {
                if (ImGui.BeginTable("priority_table", columnCount,
                    ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame |
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersOuterH))
                {
                    // Column setup (needed for sizing)
                    foreach (var drop in dropTypes)
                    {
                        ImGui.TableSetupColumn(FormatDropName(drop));
                    }

                    // Custom header row with slot icons
                    ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                    for (var col = 0; col < dropTypes.Count; col++)
                    {
                        ImGui.TableSetColumnIndex(col);
                        var drop = dropTypes[col];
                        var iconKey = drop is "ring1" or "ring2" ? "ring" : drop;
                        if (_slotIcons.TryGetValue(iconKey, out var slotTex) && slotTex != null)
                        {
                            var wrap = slotTex.GetWrapOrDefault();
                            if (wrap != null)
                            {
                                ImGui.Image(wrap.Handle, new Vector2(18, 18));
                                ImGui.SameLine();
                            }
                        }
                        ImGui.TableHeader(FormatDropName(drop));
                    }

                    // Find max row count across all columns
                    var maxRows = 0;
                    foreach (var drop in dropTypes)
                    {
                        if (floorPriority[drop].Count > maxRows)
                            maxRows = floorPriority[drop].Count;
                    }

                    // Data rows
                    for (var row = 0; row < maxRows; row++)
                    {
                        ImGui.TableNextRow();
                        for (var col = 0; col < dropTypes.Count; col++)
                        {
                            ImGui.TableSetColumnIndex(col);
                            var entries = floorPriority[dropTypes[col]];

                            if (row < entries.Count)
                            {
                                var entry = entries[row];
                                var logKey = $"{entry.PlayerId}|{dropTypes[col]}";
                                var isLogged = _loggedEntries.Contains(logKey);

                                if (isLogged)
                                {
                                    // Logged: dim the row and show "View" link
                                    DrawJobIcon(entry.Job, new Vector2(20, 20), true);
                                    ImGui.SameLine();
                                    var playerUrl = BuildWebAppUrl("players", playerId: entry.PlayerId);
                                    DrawLinkText($"{row + 1}. {entry.PlayerName}", playerUrl, ColorMuted);
                                    ImGui.SameLine();
                                    var logUrl = BuildWebAppUrl("log", week: _priorityData?.CurrentWeek);
                                    DrawLinkText("View", logUrl, ColorLink);
                                }
                                else
                                {
                                    var color = GetRoleColor(entry.Job);

                                    // Job icon + rank + player name with role color (Ctrl+Click for player link)
                                    DrawJobIcon(entry.Job, new Vector2(20, 20));
                                    ImGui.SameLine();
                                    var playerUrl = BuildWebAppUrl("players", playerId: entry.PlayerId);
                                    DrawLinkText($"{row + 1}. {entry.PlayerName}", playerUrl, color);
                                    ImGui.SameLine();
                                    ImGui.TextDisabled($"[{entry.Score}]");

                                    // Log button
                                    ImGui.SameLine();
                                    ImGui.PushID($"log_{dropTypes[col]}_{row}");
                                    if (ImGui.SmallButton("Log"))
                                    {
                                        var dropSlot = dropTypes[col];
                                        if (dropSlot is "twine" or "glaze" or "solvent")
                                        {
                                            // Look up player-specific augmentable slots from priority data
                                            var playerInfo = _priorityData?.Players.Find(p => p.Id == entry.PlayerId);
                                            string[]? eligible = null;
                                            if (playerInfo?.AugmentableSlots != null &&
                                                playerInfo.AugmentableSlots.TryGetValue(dropSlot, out var playerSlots))
                                            {
                                                eligible = playerSlots.ToArray();
                                            }

                                            // Fall back to static mapping if no player-specific data
                                            eligible ??= MaterialSlotOptions.GetValueOrDefault(dropSlot);

                                            if (eligible is { Length: 1 })
                                            {
                                                // Single option — show confirmation with augment
                                                _confirmAction = ConfirmAction.LogLoot;
                                                _confirmPlayerId = entry.PlayerId;
                                                _confirmSlot = dropSlot;
                                                _confirmPlayerName = entry.PlayerName;
                                                _confirmSlotAugmented = eligible[0];
                                                _openConfirmPopup = true;
                                            }
                                            else if (eligible is { Length: > 1 })
                                            {
                                                // Multiple options — show material slot popup first
                                                _pendingMaterialPlayerId = entry.PlayerId;
                                                _pendingMaterialSlot = dropSlot;
                                                _pendingMaterialPlayerName = entry.PlayerName;
                                                _pendingMaterialEligibleSlots = eligible;
                                                _openMaterialPopup = true;
                                            }
                                            else
                                            {
                                                // No eligible slots — confirm without augmentation
                                                _confirmAction = ConfirmAction.LogLoot;
                                                _confirmPlayerId = entry.PlayerId;
                                                _confirmSlot = dropSlot;
                                                _confirmPlayerName = entry.PlayerName;
                                                _confirmSlotAugmented = null;
                                                _openConfirmPopup = true;
                                            }
                                        }
                                        else
                                        {
                                            // Gear or universal_tomestone — show confirmation
                                            _confirmAction = ConfirmAction.LogLoot;
                                            _confirmPlayerId = entry.PlayerId;
                                            _confirmSlot = dropSlot;
                                            _confirmPlayerName = entry.PlayerName;
                                            _confirmSlotAugmented = null;
                                            _openConfirmPopup = true;
                                        }
                                    }
                                    ImGui.PopID();
                                }
                            }
                        }
                    }

                    ImGui.EndTable();
                }
            }
            ImGui.EndChild();
        }

        // Material slot selection popup (opened from Log button, rendered at window level)
        if (_openMaterialPopup)
        {
            ImGui.OpenPopup("material_slot_select");
            _openMaterialPopup = false;
        }

        if (ImGui.BeginPopup("material_slot_select"))
        {
            ImGui.TextColored(ColorAccent, $"{FormatDropName(_pendingMaterialSlot)} -> {_pendingMaterialPlayerName}");
            ImGui.Separator();
            ImGui.TextDisabled("Augment which slot?");

            if (_pendingMaterialEligibleSlots != null)
            {
                for (var i = 0; i < _pendingMaterialEligibleSlots.Length; i++)
                {
                    var slot = _pendingMaterialEligibleSlots[i];
                    if (ImGui.Selectable(FormatDropName(slot)))
                    {
                        // Route through confirmation popup
                        _confirmAction = ConfirmAction.LogLoot;
                        _confirmPlayerId = _pendingMaterialPlayerId;
                        _confirmSlot = _pendingMaterialSlot;
                        _confirmPlayerName = _pendingMaterialPlayerName;
                        _confirmSlotAugmented = slot;
                        _openConfirmPopup = true;
                        ImGui.CloseCurrentPopup();
                    }
                }
            }

            ImGui.Separator();
            if (ImGui.Selectable("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        // Confirmation modal (centered, properly sized)
        if (_openConfirmPopup)
        {
            ImGui.OpenPopup("Confirm Action###confirm_action");
            _openConfirmPopup = false;
        }

        // Center the modal in the viewport
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(340, 0));

        if (ImGui.BeginPopupModal("Confirm Action###confirm_action", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            if (_confirmAction == ConfirmAction.LogLoot)
            {
                ImGui.TextColored(ColorAccent, $"Log {FormatDropName(_confirmSlot)} \u2192 {_confirmPlayerName}?");
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextWrapped($"This will record {_confirmPlayerName} received {FormatDropName(_confirmSlot)} from {_floorName}.");
                if (_confirmSlotAugmented != null)
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped($"This will also mark {FormatDropName(_confirmSlotAugmented)} as augmented.");
                }
            }
            else if (_confirmAction == ConfirmAction.ClearFloor)
            {
                ImGui.TextColored(ColorAccent, $"Mark {_floorName} as cleared?");
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextWrapped($"This will mark {_floorName} as cleared for all 8 players. Everyone will receive books for this week.");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Confirm", new Vector2(120, 0)))
            {
                if (_confirmAction == ConfirmAction.LogLoot)
                    OnManualLog?.Invoke(_confirmPlayerId, _confirmSlot, _confirmPlayerName, _confirmSlotAugmented);
                else if (_confirmAction == ConfirmAction.ClearFloor)
                    OnMarkFloorCleared?.Invoke();

                _confirmAction = ConfirmAction.None;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120, 0)))
            {
                _confirmAction = ConfirmAction.None;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }

        ImGui.Separator();

        // Status message (auto-clears after 5s)
        if (_statusMessage.Length > 0 && DateTime.UtcNow < _statusExpiry)
        {
            ImGui.TextColored(_statusColor, _statusMessage);
        }

        // Bottom bar: floor cleared + refresh
        if (_floorCleared)
        {
            ImGui.TextColored(ColorSuccess, $"{_floorName} Cleared");
        }
        else
        {
            if (ImGui.Button($"Mark {_floorName} Cleared"))
            {
                _confirmAction = ConfirmAction.ClearFloor;
                _openConfirmPopup = true;
            }
        }

        ImGui.SameLine();
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availWidth - ImGui.CalcTextSize("Refresh").X - ImGui.GetStyle().FramePadding.X * 2);
        if (ImGui.SmallButton("Refresh"))
        {
            OnRefresh?.Invoke();
            ShowStatus("Refreshing priority data...", ColorAccent);
        }

        // Ctrl+Click hint
        if (!string.IsNullOrEmpty(_config.DefaultGroupShareCode))
        {
            ImGui.TextDisabled("Ctrl+Click names to open in browser");
        }
    }

    private void DrawJobIcon(string jobAbbrev, Vector2 size, bool dimmed = false)
    {
        var key = jobAbbrev.ToUpperInvariant();
        var tint = dimmed ? new Vector4(0.5f, 0.5f, 0.5f, 0.6f) : new Vector4(1, 1, 1, 1);

        // Try embedded icon first
        if (_jobIcons.TryGetValue(key, out var embeddedTex) && embeddedTex != null)
        {
            var wrap = embeddedTex.GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, size, new Vector2(0, 0), new Vector2(1, 1), tint);
                return;
            }
        }

        // Fallback to game icon
        if (JobIconIds.TryGetValue(key, out var jobId))
        {
            var iconId = 62100u + jobId;
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(tex.Handle, size, new Vector2(0, 0), new Vector2(1, 1), tint);
        }
        else
        {
            ImGui.Text(jobAbbrev);
        }
    }

    private static Vector4 GetRoleColor(string job)
    {
        var role = job.ToUpperInvariant() switch
        {
            "PLD" or "WAR" or "DRK" or "GNB" => "tank",
            "WHM" or "SCH" or "AST" or "SGE" => "healer",
            "MNK" or "DRG" or "NIN" or "SAM" or "RPR" or "VPR" => "melee",
            "BRD" or "MCH" or "DNC" => "ranged",
            "BLM" or "SMN" or "RDM" or "PCT" => "caster",
            _ => "melee",
        };

        return RoleColors.GetValueOrDefault(role, new Vector4(1, 1, 1, 1));
    }

    private static string FormatDropName(string drop)
    {
        return drop switch
        {
            "universal_tomestone" => "Univ. Tome",
            "tome_weapon" => "Tome Weapon",
            _ => char.ToUpper(drop[0]) + drop[1..],
        };
    }

    private string? BuildWebAppUrl(string? tab = null, int? floor = null, int? week = null, string? playerId = null)
    {
        var baseUrl = !string.IsNullOrEmpty(_config.FrontendBaseUrl)
            ? _config.FrontendBaseUrl.TrimEnd('/')
            : _config.ApiBaseUrl.TrimEnd('/');
        var shareCode = _config.DefaultGroupShareCode;
        var tierId = _config.DefaultTierId;

        if (string.IsNullOrEmpty(shareCode) || string.IsNullOrEmpty(tierId))
            return null;

        var url = $"{baseUrl}/group/{shareCode}?tier={tierId}";
        if (tab != null) url += $"&tab={tab}";
        if (floor != null) url += $"&floor={floor}";
        if (week != null) url += $"&week={week}";
        if (playerId != null) url += $"&player={playerId}";
        return url;
    }

    private static bool DrawLinkText(string label, string? url, Vector4 color)
    {
        if (url == null)
        {
            ImGui.TextColored(color, label);
            return false;
        }

        ImGui.TextColored(color, label);
        var clicked = false;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var io = ImGui.GetIO();
            if (io.KeyCtrl && ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                clicked = true;
            }
        }
        return clicked;
    }

    public void Dispose() { }
}
