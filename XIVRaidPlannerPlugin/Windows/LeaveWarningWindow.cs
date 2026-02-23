using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using XIVRaidPlannerPlugin.Services;

namespace XIVRaidPlannerPlugin.Windows;

/// <summary>
/// Warning overlay shown when the player tries to leave a savage instance
/// while they have high priority for unclaimed loot.
/// </summary>
public class LeaveWarningWindow : Window, IDisposable
{
    private readonly LeaveWarningService _leaveWarning;

    /// <summary>Fired when user chooses to leave anyway.</summary>
    public event Action? OnLeaveAnyway;

    /// <summary>Fired when user chooses to stay.</summary>
    public event Action? OnStay;

    public LeaveWarningWindow(LeaveWarningService leaveWarning)
        : base("Wait! You may have unclaimed loot",
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _leaveWarning = leaveWarning;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 120),
            MaximumSize = new Vector2(450, 300),
        };
    }

    public override void Draw()
    {
        if (!_leaveWarning.ShouldShowWarning || _leaveWarning.WarningItems.Count == 0)
        {
            IsOpen = false;
            return;
        }

        foreach (var item in _leaveWarning.WarningItems)
        {
            var rankColor = item.Rank switch
            {
                1 => new Vector4(1, 0.843f, 0, 1),   // Gold
                2 => new Vector4(0.753f, 0.753f, 0.753f, 1), // Silver
                3 => new Vector4(0.804f, 0.498f, 0.196f, 1), // Bronze
                _ => new Vector4(1, 1, 1, 1),
            };

            ImGui.TextColored(rankColor, $"You're priority #{item.Rank} for:");
            ImGui.SameLine();
            ImGui.Text(FormatDropName(item.DropType));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Leave Anyway"))
        {
            _leaveWarning.Dismiss();
            OnLeaveAnyway?.Invoke();
            IsOpen = false;
        }

        ImGui.SameLine();

        if (ImGui.Button("Stay"))
        {
            _leaveWarning.Dismiss();
            OnStay?.Invoke();
            IsOpen = false;
        }
    }

    private static string FormatDropName(string drop)
    {
        return drop switch
        {
            "ring" or "ring1" or "ring2" => "Ring",
            "universal_tomestone" => "Universal Tomestone",
            _ => char.ToUpper(drop[0]) + drop[1..],
        };
    }

    public void Dispose() { }
}
