using System;
using System.Collections.Generic;
using System.Numerics;
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

    private static readonly Vector4 ColorAccent = new(0.298f, 0.722f, 0.659f, 1f);
    private static readonly Vector4 ColorSuccess = new(0.133f, 0.773f, 0.369f, 1f);
    private static readonly Vector4 ColorError = new(0.937f, 0.267f, 0.267f, 1f);
    private static readonly Vector4 ColorMuted = new(0.4f, 0.4f, 0.45f, 1f);

    private PriorityResponse? _priorityData;
    private string? _currentFloorKey;
    private string _floorName = "";
    private string _staticName = "";

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

        // Semi-transparent background
        BgAlpha = 0.85f;
    }

    public void SetPriorityData(PriorityResponse? data, int floor, string floorName, string staticName = "")
    {
        _priorityData = data;
        _currentFloorKey = $"floor{floor}";
        _floorName = floorName;
        if (!string.IsNullOrEmpty(staticName))
            _staticName = staticName;
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

        // Header — show static name if available
        var headerLabel = !string.IsNullOrEmpty(_staticName)
            ? $"{_staticName} - {_floorName}"
            : $"XIV Raid Planner - {_floorName}";
        ImGui.TextColored(ColorAccent, headerLabel);
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
                    // Column headers
                    foreach (var drop in dropTypes)
                    {
                        ImGui.TableSetupColumn(FormatDropName(drop));
                    }
                    ImGui.TableHeadersRow();

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
                                    ImGui.TextColored(ColorMuted, $"{row + 1}. {entry.PlayerName}");
                                    ImGui.SameLine();
                                    ImGui.TextColored(ColorSuccess, "OK");
                                }
                                else
                                {
                                    var color = GetRoleColor(entry.Job);

                                    // Rank + player name with role color
                                    ImGui.TextColored(color, $"{row + 1}. {entry.PlayerName} ({entry.Job})");
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
