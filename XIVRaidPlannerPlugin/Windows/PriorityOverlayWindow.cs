using System;
using System.Collections.Generic;
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

    // Cached slot icon textures (loaded from embedded PNGs)
    private readonly Dictionary<string, ISharedImmediateTexture?> _slotIcons = new();

    private static readonly Vector4 ColorAccent = new(0.298f, 0.722f, 0.659f, 1f);
    private static readonly Vector4 ColorSuccess = new(0.133f, 0.773f, 0.369f, 1f);
    private static readonly Vector4 ColorError = new(0.937f, 0.267f, 0.267f, 1f);
    private static readonly Vector4 ColorMuted = new(0.4f, 0.4f, 0.45f, 1f);

    // Floor colors matching the web app design system
    private static readonly Dictionary<int, Vector4> FloorColors = new()
    {
        [1] = new Vector4(0.133f, 0.773f, 0.369f, 1f),  // #22c55e - Floor 1 (Accessories)
        [2] = new Vector4(0.231f, 0.510f, 0.965f, 1f),  // #3b82f6 - Floor 2 (Left Side)
        [3] = new Vector4(0.659f, 0.333f, 0.969f, 1f),  // #a855f7 - Floor 3 (Body)
        [4] = new Vector4(0.961f, 0.620f, 0.043f, 1f),  // #f59e0b - Floor 4 (Weapon)
    };

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

    // Temporary status message
    private string _statusMessage = "";
    private Vector4 _statusColor;
    private DateTime _statusExpiry;

    /// <summary>Drop types that have been manually logged via the overlay.</summary>
    public IReadOnlySet<string> LoggedEntries => _loggedEntries;

    /// <summary>Fired when user clicks a "Log" button for a specific player+slot.</summary>
    public event Action<string, string, string>? OnManualLog; // playerId, slot, playerName

    /// <summary>Fired when user clicks "Mark Floor Cleared".</summary>
    public event Action? OnMarkFloorCleared;

    public PriorityOverlayWindow()
        : base("XIV Raid Planner",
            ImGuiWindowFlags.NoCollapse)
    {
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
    }

    public void SetPriorityData(PriorityResponse? data, int floor, string floorName, string staticName = "", string tierName = "")
    {
        _priorityData = data;
        _currentFloorKey = $"floor{floor}";
        _currentFloor = floor;
        _floorName = floorName;
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

    private void ShowStatus(string message, Vector4 color)
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

        // Subheader: tier name - floor name in floor color, week in gray
        var floorColor = FloorColors.GetValueOrDefault(_currentFloor, ColorAccent);
        var tierLabel = !string.IsNullOrEmpty(_tierName)
            ? $"{char.ToUpper(_tierName[0])}{_tierName[1..]} - {_floorName}"
            : _floorName;
        ImGui.TextColored(floorColor, tierLabel);
        ImGui.SameLine();
        ImGui.TextDisabled($"Week {_priorityData.CurrentWeek}");
        ImGui.Separator();

        // Render priority columns for each drop type
        var dropTypes = new List<string>(floorPriority.Keys);
        var columnCount = dropTypes.Count;

        // Reserve space for bottom area (button + status)
        var bottomHeight = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 2 + 2;
        if (_statusMessage.Length > 0 && DateTime.UtcNow < _statusExpiry)
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
                                    // Logged: dim the row and show checkmark
                                    DrawJobIcon(entry.Job, new Vector2(16, 16), true);
                                    ImGui.SameLine();
                                    ImGui.TextColored(ColorMuted, $"{row + 1}. {entry.PlayerName}");
                                    ImGui.SameLine();
                                    ImGui.TextColored(ColorSuccess, "OK");
                                }
                                else
                                {
                                    var color = GetRoleColor(entry.Job);

                                    // Job icon + rank + player name with role color
                                    DrawJobIcon(entry.Job, new Vector2(16, 16));
                                    ImGui.SameLine();
                                    ImGui.TextColored(color, $"{row + 1}. {entry.PlayerName}");
                                    ImGui.SameLine();
                                    ImGui.TextDisabled($"[{entry.Score}]");

                                    // Log button
                                    ImGui.SameLine();
                                    ImGui.PushID($"log_{dropTypes[col]}_{row}");
                                    if (ImGui.SmallButton("Log"))
                                    {
                                        OnManualLog?.Invoke(entry.PlayerId, dropTypes[col], entry.PlayerName);
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

        ImGui.Separator();

        // Status message (auto-clears after 5s)
        if (_statusMessage.Length > 0 && DateTime.UtcNow < _statusExpiry)
        {
            ImGui.TextColored(_statusColor, _statusMessage);
        }

        // Mark floor cleared button — always pinned at bottom
        if (_floorCleared)
        {
            ImGui.TextColored(ColorSuccess, $"{_floorName} Cleared");
        }
        else
        {
            if (ImGui.Button($"Mark {_floorName} Cleared"))
            {
                OnMarkFloorCleared?.Invoke();
            }
        }
    }

    private static void DrawJobIcon(string jobAbbrev, Vector2 size, bool dimmed = false)
    {
        if (JobIconIds.TryGetValue(jobAbbrev.ToUpperInvariant(), out var jobId))
        {
            var iconId = 62100u + jobId; // Framed style icons
            var tex = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            var tint = dimmed ? new Vector4(0.5f, 0.5f, 0.5f, 0.6f) : new Vector4(1, 1, 1, 1);
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
            _ => char.ToUpper(drop[0]) + drop[1..],
        };
    }

    public void Dispose() { }
}
