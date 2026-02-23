using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using XIVRaidPlannerPlugin.Api;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Semi-transparent ImGui overlay that displays loot priority during savage raids.
/// Shows top 3 priority per drop slot on the current floor.
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

    private PriorityResponse? _priorityData;
    private string? _currentFloorKey;
    private string _floorName = "";

    /// <summary>Fired when user clicks a "Log" button for a specific player+slot.</summary>
    public event Action<string, string, string>? OnManualLog; // playerId, slot, playerName

    /// <summary>Fired when user clicks "Mark Floor Cleared".</summary>
    public event Action? OnMarkFloorCleared;

    public PriorityOverlayWindow()
        : base("XIV Raid Planner",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 150),
            MaximumSize = new Vector2(800, 600),
        };

        // Semi-transparent background
        BgAlpha = 0.85f;
    }

    public void SetPriorityData(PriorityResponse? data, int floor, string floorName)
    {
        _priorityData = data;
        _currentFloorKey = $"floor{floor}";
        _floorName = floorName;
    }

    public void ClearData()
    {
        _priorityData = null;
        _currentFloorKey = null;
        _floorName = "";
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

        // Header
        ImGui.TextColored(new Vector4(0.298f, 0.722f, 0.659f, 1f), $"XIV Raid Planner - {_floorName}");
        ImGui.Separator();

        // Render priority columns for each drop type
        var dropTypes = new List<string>(floorPriority.Keys);
        var columnCount = Math.Min(dropTypes.Count, 4);

        if (columnCount > 0 && ImGui.BeginTable("priority_table", columnCount, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchSame))
        {
            // Column headers
            foreach (var drop in dropTypes)
            {
                ImGui.TableSetupColumn(FormatDropName(drop));
            }
            ImGui.TableHeadersRow();

            // Data rows - show top 3 per column
            for (var row = 0; row < 3; row++)
            {
                ImGui.TableNextRow();
                for (var col = 0; col < dropTypes.Count; col++)
                {
                    ImGui.TableSetColumnIndex(col);
                    var entries = floorPriority[dropTypes[col]];

                    if (row < entries.Count)
                    {
                        var entry = entries[row];
                        var color = GetRoleColor(entry.Job);
                        ImGui.TextColored(color, $"{row + 1}. {entry.PlayerName} ({entry.Job})");
                        ImGui.SameLine();
                        ImGui.TextDisabled($"[{entry.Score}]");
                    }
                }
            }

            // Log buttons row
            ImGui.TableNextRow();
            for (var col = 0; col < dropTypes.Count; col++)
            {
                ImGui.TableSetColumnIndex(col);
                var entries = floorPriority[dropTypes[col]];
                if (entries.Count > 0)
                {
                    var top = entries[0];
                    ImGui.PushID($"log_{dropTypes[col]}");
                    if (ImGui.SmallButton($"Log -> {top.PlayerName}"))
                    {
                        OnManualLog?.Invoke(top.PlayerId, dropTypes[col], top.PlayerName);
                    }
                    ImGui.PopID();
                }
            }

            ImGui.EndTable();
        }

        ImGui.Separator();

        // Mark floor cleared button
        if (ImGui.Button($"Mark {_floorName} Cleared"))
        {
            OnMarkFloorCleared?.Invoke();
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"Week {_priorityData.CurrentWeek}");
    }

    private static Vector4 GetRoleColor(string job)
    {
        // Map job abbreviations to roles
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
